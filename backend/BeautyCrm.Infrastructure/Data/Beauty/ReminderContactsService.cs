using System.Globalization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// TASK-738 (§22.6.2-§22.6.4, ADR-012): вподобання клієнта, задачі «зателефонувати», налаштування нагадувань tenant.
/// Tenant лише з автентифікованого <see cref="Actor"/> і scope (RLS FORCE); чужий id -> 404. Вподобання й задачі - owner/admin,
/// налаштування: читають owner/admin, змінює owner. Переплан майбутніх записів іде в тій самій транзакції, що й upsert вподобання.
/// Логи: id, коди й лічильники; телефон, імена, note не логуються.
/// </summary>
public sealed class ReminderContactsService(
    BeautyDbContext db, TenantContext tenant, IReminderReplanStore replan, IConfiguration config, TimeProvider clock,
    ILogger<ReminderContactsService> log)
{
    private const string UniqueViolation = "23505";
    private const string Open = "open", Done = "done", Skipped = "skipped", Cancelled = "cancelled";
    public const int MaxNoteLength = 300;
    public const int DefaultTake = 100, MaxTake = 200, MaxRangeDays = 31;
    private static readonly string[] StatusFilters = [Open, Done, Skipped, Cancelled, "all"];

    private int MaxUpcomingReplan => Math.Clamp(int.TryParse(config["Reminders:MaxUpcomingReplan"], CultureInfo.InvariantCulture, out var n) ? n : 50, 1, 500);

    private Error? Guard(Actor actor, bool ownerOnly = false)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        var allowed = ownerOnly ? actor.Role == Roles.Owner : actor.Role is Roles.Owner or Roles.Admin;
        return allowed ? null : Error.Forbidden(ReminderContactsErrors.ForbiddenRole, "This action is not available for your role.");
    }

    // ------------------------------------------------------------------ вподобання клієнта (§22.6.2)

    public async Task<Result<ContactPreferencesDto>> GetPreferencesAsync(Actor actor, Guid clientId, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        var client = await db.Clients.AsNoTracking().Where(c => c.Id == clientId && c.DeletedAt == null)
            .Select(c => new { c.Phone }).FirstOrDefaultAsync(ct);
        if (client is null) return ClientNotFound();
        return await BuildPreferencesAsync(clientId, client.Phone, replanned: null, ct);
    }

    public async Task<Result<ContactPreferencesDto>> PutPreferencesAsync(Actor actor, Guid clientId, PutContactPreferencesRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        var client = await db.Clients.AsNoTracking().Where(c => c.Id == clientId && c.DeletedAt == null)
            .Select(c => new { c.Phone }).FirstOrDefaultAsync(ct);
        if (client is null) return ClientNotFound();

        var parsed = ReminderPlanning.Validate(new ReminderPlanRequest(req.Method, req.OffsetsMinutes ?? []));
        if (!parsed.IsOk)
            return Error.Validation(parsed.Error!.Code == "invalid_reminder_method" ? "invalid_method" : "invalid_offsets", parsed.Error.Message);
        var (method, offsets) = parsed.Value;

        var settings = await LoadSettingsAsync(ct);
        var telegram = method == ReminderMethods.Telegram ? await LatestTelegramConversationAsync(clientId, ct) : null;
        if (method == ReminderMethods.Telegram)
        {
            if (!settings.TelegramEnabled) return Error.Conflict("telegram_reminders_disabled", "Telegram reminders are disabled.");
            if (telegram is null) return Error.Conflict("telegram_unavailable", "The client has no Telegram chat with an active channel.");
        }
        else if (method == ReminderMethods.Call)
        {
            if (!settings.CallEnabled) return Error.Conflict("call_reminders_disabled", "Call reminders are disabled.");
            if (string.IsNullOrWhiteSpace(client.Phone)) return Error.Conflict("phone_required", "The client has no phone number.");
        }

        var now = clock.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                int? replanned = null;
                await using (var tx = await db.Database.BeginTransactionAsync(ct))
                {
                    // Серіалізує одночасні PUT одного клієнта: переплан закриває й вставляє точки, паралельний переплан порушив би UNIQUE точки.
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock(hashtextextended({"contact-prefs:" + actor.TenantId + ":" + clientId}, 0))", ct);
                    var row = await db.ClientContactPreferences.FirstOrDefaultAsync(p => p.ClientId == clientId, ct);
                    if (row is null)
                        db.ClientContactPreferences.Add(row = new ClientContactPreference { TenantId = actor.TenantId, ClientId = clientId, CreatedAt = now });
                    row.Method = method;
                    row.OffsetsMinutes = offsets;
                    row.TelegramConversationId = telegram;
                    row.Source = "admin";
                    row.UpdatedByUserId = actor.UserId;
                    row.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);

                    if (req.ApplyToUpcoming ?? true)
                        replanned = await replan.ReplanUpcomingAsync(clientId, method, offsets, MaxUpcomingReplan, now, ct);
                    await tx.CommitAsync(ct);
                }
                log.LogInformation("contact_prefs_updated client={ClientId} user={UserId} method={Method} replanned={Replanned}",
                    clientId, actor.UserId, method, replanned ?? 0);
                return await BuildPreferencesAsync(clientId, client.Phone, replanned ?? 0, ct);
            }
            // гонка двох перших PUT (PK tenant_id + client_id): повторити як оновлення
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<ContactPreferencesDto> BuildPreferencesAsync(Guid clientId, string? phone, int? replanned, CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        var telegram = await LatestTelegramConversationAsync(clientId, ct) is not null;
        var pref = await db.ClientContactPreferences.AsNoTracking().Where(p => p.ClientId == clientId)
            .Select(p => new { p.Method, p.OffsetsMinutes, p.Source, p.UpdatedAt, p.UpdatedByUserId }).FirstOrDefaultAsync(ct);
        var phoneAvailable = !string.IsNullOrWhiteSpace(phone);
        if (pref is null)
            return new ContactPreferencesDto("unset", null, [], null, null, null, telegram, phoneAvailable, settings.TelegramEnabled, settings.CallEnabled, replanned);
        var by = pref.UpdatedByUserId is { } uid ? await UserRefAsync(uid, ct) : null;
        return new ContactPreferencesDto("set", pref.Method, pref.OffsetsMinutes.Select(o => (int)o).ToList(), pref.Source, pref.UpdatedAt, by,
            telegram, phoneAvailable, settings.TelegramEnabled, settings.CallEnabled, replanned);
    }

    private Task<Guid?> LatestTelegramConversationAsync(Guid clientId, CancellationToken ct) =>
        db.Conversations.AsNoTracking()
            .Where(c => c.ClientId == clientId && c.Channel!.Type == ChannelType.Telegram && c.Channel.IsActive)
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);

    private Task<UserRefDto?> UserRefAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new UserRefDto(u.Id, u.FullName)).FirstOrDefaultAsync(ct);

    private static Error ClientNotFound() => Error.NotFound(ReminderContactsErrors.ClientNotFound, "Client not found.");

    // ------------------------------------------------------------------ задачі «зателефонувати» (§22.6.3)

    private sealed record Candidate(Guid Id, Guid LocationId, DateTimeOffset DueAt, string Status);

    /// <param name="to">Необов'язкова кінцева дата діапазону (включно) разом із явною датою <paramref name="day"/>; ≤ 31 день.</param>
    public async Task<Result<CallTaskListDto>> ListCallTasksAsync(
        Actor actor, string? day, string? to, string? status, Guid? locationId, int? take, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        day = string.IsNullOrWhiteSpace(day) ? "today" : day.Trim().ToLowerInvariant();
        status = string.IsNullOrWhiteSpace(status) ? Open : status.Trim().ToLowerInvariant();
        if (Array.IndexOf(StatusFilters, status) < 0)
            return Error.Validation("invalid_status", "status must be open, done, skipped, cancelled or all.");

        var nowUtc = clock.GetUtcNow();
        int relativeDays = 0;
        DateOnly from = default, last = default;
        var explicitDay = false;
        if (day is "today" or "tomorrow")
        {
            if (!string.IsNullOrWhiteSpace(to)) return Error.Validation("invalid_day", "to is allowed only with a YYYY-MM-DD day.");
            relativeDays = day == "tomorrow" ? 1 : 0;
        }
        else
        {
            if (!TryDate(day, out from)) return Error.Validation("invalid_day", "day must be today, tomorrow or YYYY-MM-DD.");
            last = from;
            if (!string.IsNullOrWhiteSpace(to) && !TryDate(to.Trim(), out last)) return Error.Validation("invalid_day", "to must be YYYY-MM-DD.");
            if (last < from || last.DayNumber - from.DayNumber >= MaxRangeDays)
                return Error.Validation("invalid_day", $"The range must be 1..{MaxRangeDays} days.");
            explicitDay = true;
        }
        var size = Math.Clamp(take ?? DefaultTake, 1, MaxTake);

        var zones = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Timezone }).ToListAsync(ct);
        var tzById = zones.ToDictionary(z => z.Id, z => ResolveZone(z.Timezone));

        // Широке вікно за UTC (зони різні, ±14 год), далі точний відбір за локальною датою у зоні закладу задачі.
        var lo = explicitDay ? new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(-1) : nowUtc.AddDays(-1.5);
        var hi = explicitDay ? new DateTimeOffset(last.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddDays(2) : nowUtc.AddDays(3.5);
        var query = db.CallTasks.AsNoTracking().Where(t => t.DueAt >= lo && t.DueAt < hi);
        if (locationId is { } loc) query = query.Where(t => t.LocationId == loc);
        var window = await query.Select(t => new Candidate(t.Id, t.LocationId, t.DueAt, t.Status)).ToListAsync(ct);

        var inDay = window.Where(c =>
        {
            var tz = tzById.GetValueOrDefault(c.LocationId, TimeZoneInfo.Utc);
            var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(c.DueAt, tz).DateTime);
            if (explicitDay) return local >= from && local <= last;
            return local == DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime).AddDays(relativeDays);
        }).ToList();

        var counts = new CallTaskCountsDto(inDay.Count(c => c.Status == Open), inDay.Count(c => c.Status == Done), inDay.Count(c => c.Status == Skipped));
        var ids = inDay.Where(c => status == "all" || c.Status == status).OrderBy(c => c.DueAt).ThenBy(c => c.Id).Take(size).Select(c => c.Id).ToList();
        var items = await LoadTasksAsync(ids, tzById, ct);
        return new CallTaskListDto(items.OrderBy(i => i.DueAt).ThenBy(i => i.Id).ToList(), counts);
    }

    public async Task<Result<CallTaskDto>> PatchCallTaskAsync(Actor actor, Guid id, PatchCallTaskRequest req, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        var target = req.Status?.Trim().ToLowerInvariant();
        if (target is not (Open or Done or Skipped))
            return Error.Validation("invalid_status", "status must be done, skipped or open.");
        string? note = null;
        if (req.Note is not null)
        {
            note = req.Note.Trim();
            if (note.EnumerateRunes().Count() > MaxNoteLength || note.Any(ch => char.IsControl(ch) && ch != '\n'))
                return Error.Validation("invalid_note", $"note must be at most {MaxNoteLength} characters without control characters.");
            if (note.Length == 0) note = null;
        }

        var now = clock.GetUtcNow();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var cur = await db.CallTasks.AsNoTracking().Where(t => t.Id == id).Select(t => t.Status).FirstOrDefaultAsync(ct);
            if (cur is null) return Error.NotFound(ReminderContactsErrors.CallTaskNotFound, "Call task not found.");
            if (cur == Cancelled) return await ConflictAsync(ReminderContactsErrors.TaskCancelled, "The task was cancelled by the system.", id, ct);

            int changed;
            if (target == Open)
            {
                if (cur == Open) return await OneAsync(id, ct); // уже відкрита: ідемпотентно
                try
                {
                    changed = await db.CallTasks.Where(t => t.Id == id && (t.Status == Done || t.Status == Skipped))
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, Open).SetProperty(t => t.CompletedByUserId, (Guid?)null)
                            .SetProperty(t => t.CompletedAt, (DateTimeOffset?)null).SetProperty(t => t.Note, t => note ?? t.Note)
                            .SetProperty(t => t.UpdatedAt, now), ct);
                }
                // для запису вже є інша відкрита задача-нагадування (після переплану): ux_beauty_call_tasks_open_reminder
                catch (Exception ex) when (IsUniqueViolation(ex))
                {
                    return await ConflictAsync(ReminderContactsErrors.TaskSuperseded, "Another open call task already exists for this appointment.", id, ct);
                }
            }
            else
            {
                changed = await db.CallTasks.Where(t => t.Id == id && t.Status == Open)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, target).SetProperty(t => t.CompletedByUserId, (Guid?)actor.UserId)
                        .SetProperty(t => t.CompletedAt, (DateTimeOffset?)now).SetProperty(t => t.Note, t => note ?? t.Note)
                        .SetProperty(t => t.UpdatedAt, now), ct);
                if (changed == 0 && cur == Open) continue; // стан змінився між читанням і UPDATE: перечитати
                if (changed == 0)
                    return await ConflictAsync(ReminderContactsErrors.TaskAlreadyClosed, "The task has already been closed.", id, ct);
            }
            if (changed == 0) continue;
            log.LogInformation("call_task_updated task={TaskId} user={UserId} status={Status}", id, actor.UserId, target);
            return await OneAsync(id, ct);
        }
        return await ConflictAsync(ReminderContactsErrors.TaskAlreadyClosed, "The task was changed concurrently.", id, ct);
    }

    private async Task<Result<CallTaskDto>> OneAsync(Guid id, CancellationToken ct)
    {
        var tzById = await ZonesAsync(ct);
        var items = await LoadTasksAsync([id], tzById, ct);
        return items.Count == 1 ? items[0] : Error.NotFound(ReminderContactsErrors.CallTaskNotFound, "Call task not found.");
    }

    /// <summary>409 з поточним станом задачі (контракт: <c>task</c> у тілі; API-шар розкладає <c>Conflicts</c> у це поле).</summary>
    private async Task<Result<CallTaskDto>> ConflictAsync(string code, string message, Guid id, CancellationToken ct)
    {
        var tzById = await ZonesAsync(ct);
        var items = await LoadTasksAsync([id], tzById, ct);
        return items.Count == 1 ? Error.ConflictWith(code, message, items[0]) : Error.NotFound(ReminderContactsErrors.CallTaskNotFound, "Call task not found.");
    }

    private async Task<Dictionary<Guid, TimeZoneInfo>> ZonesAsync(CancellationToken ct) =>
        (await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Timezone }).ToListAsync(ct)).ToDictionary(z => z.Id, z => ResolveZone(z.Timezone));

    private async Task<List<CallTaskDto>> LoadTasksAsync(IReadOnlyList<Guid> ids, Dictionary<Guid, TimeZoneInfo> tzById, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        // Телефон - лише з клієнта в момент запиту (у задачі його немає).
        var rows = await db.CallTasks.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id, t.DueAt, t.Status, t.Reason, t.OffsetMinutes, t.CompletedAt, t.CompletedByUserId, t.Note,
                ClientId = t.ClientId, ClientName = t.Client!.FullName, ClientPhone = t.Client.Phone, ClientPhonePending = t.Client.PhonePending,
                AppointmentId = t.AppointmentId, StartsAt = t.Appointment!.StartsAt, AppointmentStatus = t.Appointment.Status,
                ServiceName = t.Appointment.Service!.Name, SpecialistName = t.Appointment.Specialist!.FullName,
                LocationId = t.LocationId, LocationName = t.Location!.Name, t.Location.Timezone,
            }).ToListAsync(ct);
        var userIds = rows.Where(r => r.CompletedByUserId != null).Select(r => r.CompletedByUserId!.Value).Distinct().ToList();
        var users = userIds.Count == 0
            ? new Dictionary<Guid, UserRefDto>()
            : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new UserRefDto(u.Id, u.FullName)).ToDictionaryAsync(u => u.Id, ct);

        return rows.Select(r =>
        {
            var tz = tzById.GetValueOrDefault(r.LocationId) ?? ResolveZone(r.Timezone);
            var due = TimeZoneInfo.ConvertTime(r.DueAt, tz);
            var starts = TimeZoneInfo.ConvertTime(r.StartsAt, tz);
            return new CallTaskDto(
                r.Id, r.DueAt, due.ToString("HH:mm", CultureInfo.InvariantCulture), r.Status, r.Reason, r.OffsetMinutes,
                new CallTaskClientDto(r.ClientId, r.ClientName, string.IsNullOrWhiteSpace(r.ClientPhone) ? null : r.ClientPhone,
                    string.IsNullOrWhiteSpace(r.ClientPhonePending) ? null : r.ClientPhonePending),
                new CallTaskAppointmentDto(r.AppointmentId, r.StartsAt, starts.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture),
                    r.ServiceName, r.SpecialistName, EnumText<AppointmentStatus>.ToDb(r.AppointmentStatus)),
                new CallTaskLocationDto(r.LocationId, r.LocationName, r.Timezone),
                r.CompletedAt, r.CompletedByUserId is { } u ? users.GetValueOrDefault(u) : null, r.Note);
        }).ToList();
    }

    // ------------------------------------------------------------------ налаштування нагадувань (§22.6.4)

    private async Task<ReminderSettings> LoadSettingsAsync(CancellationToken ct)
    {
        var row = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null ? ReminderSettings.Default : new ReminderSettings(row.TelegramEnabled, row.CallEnabled, row.QuietStart, row.QuietEnd);
    }

    private static ReminderSettingsDto ToDto(ReminderSettings s) =>
        new(s.TelegramEnabled, s.CallEnabled, s.QuietStart.ToString("HH:mm", CultureInfo.InvariantCulture), s.QuietEnd.ToString("HH:mm", CultureInfo.InvariantCulture));

    public async Task<Result<ReminderSettingsDto>> GetSettingsAsync(Actor actor, CancellationToken ct)
    {
        if (Guard(actor) is { } denied) return denied;
        return ToDto(await LoadSettingsAsync(ct));
    }

    public async Task<Result<ReminderSettingsDto>> PutSettingsAsync(Actor actor, ReminderSettingsDto req, CancellationToken ct)
    {
        if (Guard(actor, ownerOnly: true) is { } denied) return denied;
        if (req.TelegramEnabled is null || req.CallEnabled is null || req.QuietHoursStart is null || req.QuietHoursEnd is null)
            return Error.Validation("settings_incomplete", "telegramEnabled, callEnabled, quietHoursStart and quietHoursEnd are required.");
        if (!TimeOnly.TryParseExact(req.QuietHoursStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(req.QuietHoursEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
            || start == end || ((end.Hour * 60 + end.Minute) - (start.Hour * 60 + start.Minute) + 1440) % 1440 > 720)
            return Error.Validation("invalid_quiet_hours", "Quiet hours must be HH:mm, start must differ from end, and the span is at most 12 hours.");

        var now = clock.GetUtcNow();
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.ReminderSettings.FirstOrDefaultAsync(ct);
            if (row is null) db.ReminderSettings.Add(row = new ReminderSettingsRow { TenantId = actor.TenantId });
            row.TelegramEnabled = req.TelegramEnabled.Value;
            row.CallEnabled = req.CallEnabled.Value;
            row.QuietStart = start;
            row.QuietEnd = end;
            row.UpdatedByUserId = actor.UserId;
            row.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            // гонка двох перших PUT (PK tenant_id): повторити як оновлення
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
        log.LogInformation("reminder_settings_updated user={UserId} telegram={Telegram} call={Call}", actor.UserId, req.TelegramEnabled, req.CallEnabled);
        return ToDto(await LoadSettingsAsync(ct));
    }

    // ------------------------------------------------------------------ допоміжне

    private static bool TryDate(string s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    private static bool IsUniqueViolation(Exception ex) =>
        ex is PostgresException { SqlState: UniqueViolation } || ex.InnerException is PostgresException { SqlState: UniqueViolation };

    private static TimeZoneInfo ResolveZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
