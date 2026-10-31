using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>
/// TASK-737 (§22.4, ADR-012): запис плану нагадувань (рядки beauty_reminders і задачі beauty_call_tasks), закриття плану,
/// проєкція в AppointmentDto.reminders і порт вхідних планувальника (<see cref="IReminderContextStore"/>).
/// Усе виконується під RLS tenant-а та в транзакції виклику (запис / перенос / зміна статусу).
/// </summary>
public sealed partial class EfBeautyStore : IReminderContextStore, IReminderReplanStore
{
    private const string CallTaskReasonReminder = "reminder";
    private const string CallTaskOpen = "open";
    private const string CallTaskCancelled = "cancelled";

    // ---------- запис плану ----------

    /// <summary>
    /// Додає в трекер рядки точок: повідомлення -> beauty_reminders (scheduled / skipped + skip_reason), активний дзвінок -> beauty_call_tasks.
    /// Пропущений дзвінок (no_phone / call_disabled) не зберігається: задача існує лише для людини, яка подзвонить.
    /// </summary>
    private void AddPlanRows(Guid appointmentId, Guid clientId, Guid locationId, PlannedReminders plan)
    {
        foreach (var p in plan.Points)
        {
            if (p.Kind == ReminderPointKind.Message)
            {
                db.Reminders.Add(new Reminder
                {
                    Id = Guid.NewGuid(), AppointmentId = appointmentId, ScheduledAt = p.DueAt.ToUniversalTime(),
                    Status = p.IsSkipped ? ReminderStatus.Skipped : ReminderStatus.Scheduled, SkipReason = p.SkipReason,
                    OffsetMinutes = (short)p.OffsetMinutes,
                    Method = plan.Method == ReminderMethods.Telegram ? ReminderMethods.Telegram : ReminderMethods.Auto,
                    PlannedStartsAt = plan.StartsAt.ToUniversalTime(),
                });
            }
            else if (!p.IsSkipped)
            {
                db.CallTasks.Add(new CallTask
                {
                    Id = Guid.NewGuid(), AppointmentId = appointmentId, ClientId = clientId, LocationId = locationId,
                    Reason = CallTaskReasonReminder, OffsetMinutes = (short)p.OffsetMinutes, DueAt = p.DueAt.ToUniversalTime(), Status = CallTaskOpen,
                });
            }
        }
    }

    /// <summary>
    /// scheduled-нагадування -> cancelled (skip_reason = код), відкриті задачі reason=reminder -> cancelled (closed_reason = код).
    /// Умовні UPDATE (не трекер): виконуються негайно в транзакції виклику, тож нові точки можна вставляти слідом, не порушуючи
    /// частковий UNIQUE. <paramref name="includePlannerSkipped"/>: при переплані закриваються й рядки, які пропустив сам планувальник
    /// (quiet_hours / merged / telegram_disabled), щоб старий план не лишався поряд із новим; результат роботи воркера (sent, failed,
    /// skipped:too_late тощо) не чіпається.
    /// </summary>
    private async Task ClosePlanAsync(Guid appointmentId, string code, bool includePlannerSkipped, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.Reminders
            .Where(r => r.AppointmentId == appointmentId
                        && (r.Status == ReminderStatus.Scheduled
                            || (includePlannerSkipped && r.Status == ReminderStatus.Skipped
                                && (r.SkipReason == ReminderSkipReasons.QuietHours || r.SkipReason == ReminderSkipReasons.Merged
                                    || r.SkipReason == ReminderSkipReasons.TelegramDisabled))))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ReminderStatus.Cancelled)
                .SetProperty(r => r.SkipReason, code).SetProperty(r => r.UpdatedAt, now), ct);
        await db.CallTasks
            .Where(t => t.AppointmentId == appointmentId && t.Status == CallTaskOpen && t.Reason == CallTaskReasonReminder)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, CallTaskCancelled)
                .SetProperty(t => t.ClosedReason, code).SetProperty(t => t.UpdatedAt, now), ct);
    }

    // ---------- проєкція AppointmentDto.reminders ----------

    private async Task<Dictionary<Guid, AppointmentRemindersDto>> LoadReminderViewsAsync(
        IReadOnlyList<(Guid Id, DateTimeOffset StartsAt, ReminderOption Option, string Method, short[] Offsets)> items, CancellationToken ct)
    {
        var result = new Dictionary<Guid, AppointmentRemindersDto>(items.Count);
        if (items.Count == 0) return result;
        var ids = items.Select(i => i.Id).ToList();

        // Поточний план: рядки з offset_minutes, які не закриті (cancelled) і заплановані для ПОТОЧНОГО starts_at.
        var rows = await db.Reminders.AsNoTracking()
            .Where(r => ids.Contains(r.AppointmentId) && r.OffsetMinutes != null && r.Status != ReminderStatus.Cancelled)
            .Select(r => new { r.AppointmentId, Offset = r.OffsetMinutes!.Value, r.ScheduledAt, r.Status, r.SkipReason, r.PlannedStartsAt, r.CreatedAt })
            .ToListAsync(ct);
        var tasks = await db.CallTasks.AsNoTracking()
            .Where(t => ids.Contains(t.AppointmentId) && t.Reason == CallTaskReasonReminder && t.Status != CallTaskCancelled && t.OffsetMinutes != null)
            .Select(t => new { t.AppointmentId, Offset = t.OffsetMinutes!.Value, t.DueAt, t.Status, t.CreatedAt })
            .ToListAsync(ct);
        var rowsByAppt = rows.ToLookup(r => r.AppointmentId);
        var tasksByAppt = tasks.ToLookup(t => t.AppointmentId);

        foreach (var i in items)
        {
            var points = new List<ReminderPointDto>();
            // По одній (найновішій) точці на offset: після переплану старий результат воркера не дублюється з новим рядком.
            foreach (var g in rowsByAppt[i.Id].Where(r => r.PlannedStartsAt == null || r.PlannedStartsAt == i.StartsAt).GroupBy(r => r.Offset))
            {
                var r = g.OrderByDescending(x => x.CreatedAt).First();
                points.Add(new ReminderPointDto("message", r.Offset, r.ScheduledAt, EnumText<ReminderStatus>.ToDb(r.Status), r.SkipReason));
            }
            if (tasksByAppt[i.Id].OrderByDescending(t => t.CreatedAt).FirstOrDefault() is { } task)
                points.Add(new ReminderPointDto("call", task.Offset, task.DueAt, task.Status));

            // Рядки, створені до розгортання TASK-737, мають (none, {}) при reminder_option 1h/2h: ефективний план - з дзеркала.
            var (method, offsets) = i.Method == ReminderMethods.None && i.Offsets.Length == 0 && i.Option != ReminderOption.None
                ? ReminderOffsets.FromLegacy(EnumText<ReminderOption>.ToDb(i.Option))
                : (i.Method, i.Offsets);
            result[i.Id] = new AppointmentRemindersDto(method, offsets.Select(o => (int)o).ToList(), points.OrderByDescending(p => p.OffsetMinutes).ToList());
        }
        return result;
    }

    // ---------- IReminderContextStore ----------

    public async Task<ReminderSettings> GetSettingsAsync(CancellationToken ct)
    {
        var row = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row is null ? ReminderSettings.Default : new ReminderSettings(row.TelegramEnabled, row.CallEnabled, row.QuietStart, row.QuietEnd);
    }

    public async Task<ReminderClientContext> GetClientContextAsync(ClientInput client, CancellationToken ct)
    {
        Guid clientId;
        bool phone;
        if (client.Id is { } id)
        {
            var found = await db.Clients.AsNoTracking().Where(c => c.Id == id && c.DeletedAt == null).Select(c => new { c.Phone }).FirstOrDefaultAsync(ct);
            if (found is null) return new ReminderClientContext(false, false, null, ClientFound: false);
            clientId = id;
            phone = !string.IsNullOrWhiteSpace(found.Phone);
        }
        else
        {
            var typed = client.Phone?.Trim();
            phone = !string.IsNullOrEmpty(typed);
            var existing = phone
                ? await db.Clients.AsNoTracking().Where(c => c.Phone == typed && c.DeletedAt == null).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
                : null;
            if (existing is not { } existingId) return new ReminderClientContext(phone, false, null); // нового клієнта ще немає: ні розмов, ні вподобань
            clientId = existingId;
        }

        var pref = await db.ClientContactPreferences.AsNoTracking().Where(p => p.ClientId == clientId)
            .Select(p => new { p.Method, p.OffsetsMinutes }).FirstOrDefaultAsync(ct);
        var telegram = await db.Conversations.AsNoTracking()
            .AnyAsync(c => c.ClientId == clientId && c.Channel!.Type == ChannelType.Telegram && c.Channel.IsActive, ct);
        return new ReminderClientContext(phone, telegram, pref is null ? null : new ClientReminderPreference(pref.Method, pref.OffsetsMinutes));
    }

    // ---------- IReminderReplanStore ----------

    public Task<bool> ReplanAppointmentAsync(Guid appointmentId, string method, IReadOnlyList<short> offsets, DateTimeOffset now, CancellationToken ct) =>
        InTransactionAsync(async () => await ReplanCoreAsync(appointmentId, method, offsets, await GetSettingsAsync(ct), now, ct), ct);

    public Task<int> ReplanUpcomingAsync(Guid clientId, string method, IReadOnlyList<short> offsets, int max, DateTimeOffset now, CancellationToken ct) =>
        InTransactionAsync(async () =>
        {
            var nowUtc = now.ToUniversalTime();
            var ids = await db.Appointments.AsNoTracking()
                .Where(a => a.ClientId == clientId && a.StartsAt > nowUtc
                            && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
                .OrderBy(a => a.StartsAt).Take(Math.Max(0, max)).Select(a => a.Id).ToListAsync(ct);
            var settings = await GetSettingsAsync(ct);
            var count = 0;
            foreach (var id in ids)
                if (await ReplanCoreAsync(id, method, offsets, settings, now, ct)) count++;
            return count;
        }, ct);

    private async Task<bool> ReplanCoreAsync(
        Guid appointmentId, string method, IReadOnlyList<short> offsets, ReminderSettings settings, DateTimeOffset now, CancellationToken ct)
    {
        var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appt is null || appt.StartsAt <= now || appt.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed)) return false;
        var timezone = await db.Locations.AsNoTracking().Where(l => l.Id == appt.LocationId).Select(l => l.Timezone).FirstAsync(ct);
        var phone = !string.IsNullOrWhiteSpace(await db.Clients.AsNoTracking().Where(c => c.Id == appt.ClientId).Select(c => c.Phone).FirstOrDefaultAsync(ct));
        var plan = ReminderPlanner.Plan(new ReminderPlanInput(appt.StartsAt, timezone, method, offsets, settings, now, phone));

        appt.ReminderMethod = plan.Method;
        appt.ReminderOffsets = plan.Offsets.ToArray();
        appt.ReminderOption = EnumText<ReminderOption>.FromDb(plan.Mirror);
        await ClosePlanAsync(appointmentId, ReminderSkipReasons.Replanned, includePlannerSkipped: true, ct);
        AddPlanRows(appointmentId, appt.ClientId, appt.LocationId, plan);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Виконує роботу в поточній транзакції контексту або у власній (комітиться, якщо відкрита тут).</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        await tx.CommitAsync(ct);
        return result;
    }
}
