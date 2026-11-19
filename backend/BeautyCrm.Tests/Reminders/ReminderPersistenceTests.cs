using System.Net;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Reminders;

/// <summary>
/// TASK-737 (§22.12 сценарії 1, 2, 3, 4, 5, 7, 13, 21): планувальник нагадувань у повному HTTP-конвеєрі на реальному PostgreSQL
/// (RLS, роль без BYPASSRLS): рядки beauty_reminders, задачі beauty_call_tasks, дзеркало reminder_option, AppointmentDto.reminders.
/// Заклад у Europe/Kyiv, графік 09:00-18:00 за місцевим часом.
/// </summary>
public sealed class ReminderPersistenceTests : IClassFixture<AuthApiFixture>
{
    private static readonly HttpMethod Patch = new("PATCH");
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");

    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;

    public ReminderPersistenceTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private Task<RegressionHarness.Salon> SalonAsync() => _h.CreateSalonAsync(timezone: "Europe/Kyiv");

    private static TimeOnly Local(DateTimeOffset utc) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Kyiv).DateTime);

    /// <summary>Слот на дату (місцева дата закладу) з заданим місцевим часом.</summary>
    private async Task<DateTimeOffset> SlotAsync(RegressionHarness.Salon s, string date, int hour, int minute = 0)
    {
        var slots = await _h.SlotsAsync(s, date);
        return slots.Select(x => x.GetProperty("startsAt").GetDateTimeOffset())
            .First(t => Local(t) == new TimeOnly(hour, minute));
    }

    private static string FutureDate(int days = 6) => DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd");

    private async Task<Guid> ClientAsync(RegressionHarness.Salon s, bool phone = true, bool telegram = false, ClientContactPreference? pref = null)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var c = new Client { FullName = "Клієнт", Phone = phone ? "+38050" + Random.Shared.Next(1000000, 9999999) : null };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        if (telegram)
        {
            var ch = await ctx.Channels.FirstOrDefaultAsync() ?? new Channel { Type = ChannelType.Telegram, Name = "bot" };
            if (ctx.Entry(ch).State == EntityState.Detached) { ctx.Add(ch); await ctx.SaveChangesAsync(); }
            ctx.Add(new Conversation { ChannelId = ch.Id, ClientId = c.Id, ExternalChatId = Random.Shared.Next(100000, 999999).ToString() });
        }
        if (pref is not null)
        {
            pref.ClientId = c.Id;
            ctx.Add(pref);
        }
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private Task<HttpResponseMessage> PostAsync(RegressionHarness.Salon s, DateTimeOffset start, Guid clientId, object? reminders, string? reminder = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["locationId"] = s.LocationId, ["specialistId"] = s.SpecialistId, ["serviceId"] = s.ServiceId, ["startsAt"] = start,
            ["client"] = new { id = clientId }, ["paymentMethod"] = "cash",
        };
        if (reminders is not null) body["reminders"] = reminders;
        if (reminder is not null) body["reminder"] = reminder;
        return _fx.Send(HttpMethod.Post, "/api/beauty/appointments", body, s.OwnerToken);
    }

    private async Task<(Guid Id, JsonElement Json)> CreatedAsync(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var json = await Read<JsonElement>(r);
        return (json.GetProperty("id").GetGuid(), json);
    }

    private async Task<List<Reminder>> RemindersOfAsync(RegressionHarness.Salon s, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        return await ctx.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).OrderBy(r => r.OffsetMinutes).ThenBy(r => r.CreatedAt).ToListAsync();
    }

    private async Task<List<CallTask>> TasksOfAsync(RegressionHarness.Salon s, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        return await ctx.CallTasks.AsNoTracking().Where(t => t.AppointmentId == id).OrderBy(t => t.CreatedAt).ToListAsync();
    }

    private static object Tg(params int[] offsets) => new { method = "telegram", offsetsMinutes = offsets };
    private static object Call(int offset) => new { method = "call", offsetsMinutes = new[] { offset } };

    // ---------- сценарій 1: кілька точок, дзеркало, DTO ----------

    [SkippableFact]
    public async Task telegram_with_two_offsets_writes_two_scheduled_rows_a_custom_mirror_and_the_dto()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var client = await ClientAsync(s, telegram: true);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, client, Tg(120, 1440)));

        var rows = await RemindersOfAsync(s, id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(ReminderStatus.Scheduled, r.Status);
            Assert.Equal("telegram", r.Method);
            Assert.Equal(start, r.PlannedStartsAt);
            Assert.Null(r.SkipReason);
        });
        Assert.Equal(start.AddMinutes(-120), rows.Single(r => r.OffsetMinutes == 120).ScheduledAt);
        Assert.Equal(start.AddMinutes(-1440), rows.Single(r => r.OffsetMinutes == 1440).ScheduledAt);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var a = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(ReminderOption.Custom, a.ReminderOption);
        Assert.Equal("telegram", a.ReminderMethod);
        Assert.Equal(new short[] { 120, 1440 }, a.ReminderOffsets);

        Assert.Equal("custom", json.GetProperty("reminderOption").GetString());
        var reminders = json.GetProperty("reminders");
        Assert.Equal("telegram", reminders.GetProperty("method").GetString());
        Assert.Equal([120, 1440], reminders.GetProperty("offsetsMinutes").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        var points = reminders.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(2, points.Count);
        Assert.All(points, p => { Assert.Equal("message", p.GetProperty("kind").GetString()); Assert.Equal("scheduled", p.GetProperty("status").GetString()); });
        Assert.Equal(start.AddMinutes(-1440), points[0].GetProperty("dueAt").GetDateTimeOffset()); // за спаданням offset
        Assert.False(points[0].TryGetProperty("skipReason", out _));

        // GET і список повертають той самий план
        var get = await Read<JsonElement>(await _fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{id}", bearer: s.OwnerToken));
        Assert.Equal(2, get.GetProperty("reminders").GetProperty("points").GetArrayLength());
        var list = await Read<JsonElement>(await _fx.Send(HttpMethod.Get,
            $"/api/beauty/appointments?from={Uri.EscapeDataString(start.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("O"))}", bearer: s.OwnerToken));
        Assert.Equal(2, list.EnumerateArray().Single().GetProperty("reminders").GetProperty("points").GetArrayLength());
    }

    [SkippableTheory]
    [InlineData(60, "1h", ReminderOption.OneHour)]
    [InlineData(120, "2h", ReminderOption.TwoHours)]
    public async Task single_standard_offset_keeps_the_legacy_mirror(int offset, string mirror, ReminderOption expected)
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(offset)));

        Assert.Equal(mirror, json.GetProperty("reminderOption").GetString());
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.Equal(expected, (await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == id)).ReminderOption);
    }

    // ---------- сценарій 2: сумісність ----------

    [SkippableTheory]
    [InlineData("1h", 60)]
    [InlineData("2h", 120)]
    public async Task legacy_reminder_is_an_auto_plan_with_one_row(string legacy, int offset)
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, phone: false), null, legacy));

        var r = Assert.Single(await RemindersOfAsync(s, id));
        Assert.Equal("auto", r.Method);
        Assert.Equal((short)offset, r.OffsetMinutes);
        Assert.Equal(start.AddMinutes(-offset), r.ScheduledAt);
        Assert.Equal(legacy, json.GetProperty("reminderOption").GetString());
        Assert.Equal("auto", json.GetProperty("reminders").GetProperty("method").GetString());
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var a = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("auto", a.ReminderMethod);
        Assert.Equal(new[] { (short)offset }, a.ReminderOffsets);
    }

    [SkippableFact]
    public async Task reminders_together_with_reminder_is_422_reminder_conflict_and_nothing_is_created()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var r = await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(60), "1h");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("reminder_conflict", await RegressionHarness.CodeAsync(r));
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.False(await ctx.Appointments.AnyAsync());
    }

    [SkippableFact]
    public async Task without_reminder_fields_and_without_preference_the_plan_is_none_and_no_rows_exist()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s), null));

        Assert.Empty(await RemindersOfAsync(s, id));
        Assert.Equal("none", json.GetProperty("reminderOption").GetString());
        Assert.Equal("none", json.GetProperty("reminders").GetProperty("method").GetString());
        Assert.Equal(0, json.GetProperty("reminders").GetProperty("points").GetArrayLength());
    }

    // ---------- валідація й 409 ----------

    [SkippableTheory]
    [InlineData("sms", new[] { 60 }, "invalid_reminder_method")]
    [InlineData("telegram", new int[] { }, "invalid_reminder_offsets")]
    [InlineData("telegram", new[] { 30 }, "invalid_reminder_offsets")]
    [InlineData("call", new[] { 60, 120 }, "invalid_reminder_offsets")]
    [InlineData("none", new[] { 60 }, "invalid_reminder_offsets")]
    public async Task invalid_reminders_are_422(string method, int[] offsets, string code)
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var r = await PostAsync(s, start, await ClientAsync(s, telegram: true), new { method, offsetsMinutes = offsets });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(code, await RegressionHarness.CodeAsync(r));
    }

    [SkippableFact]
    public async Task availability_conflicts_are_409_with_codes()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);

        var noTelegram = await PostAsync(s, start, await ClientAsync(s), Tg(60));
        Assert.Equal(HttpStatusCode.Conflict, noTelegram.StatusCode);
        Assert.Equal("telegram_unavailable", await RegressionHarness.CodeAsync(noTelegram));

        var noPhone = await PostAsync(s, start, await ClientAsync(s, phone: false, telegram: true), Call(60));
        Assert.Equal(HttpStatusCode.Conflict, noPhone.StatusCode);
        Assert.Equal("phone_required", await RegressionHarness.CodeAsync(noPhone));

        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
        {
            ctx.ReminderSettings.Add(new ReminderSettingsRow { TelegramEnabled = false, CallEnabled = false });
            await ctx.SaveChangesAsync();
        }
        var tgOff = await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(60));
        Assert.Equal("telegram_reminders_disabled", await RegressionHarness.CodeAsync(tgOff));
        var callOff = await PostAsync(s, start, await ClientAsync(s), Call(60));
        Assert.Equal("call_reminders_disabled", await RegressionHarness.CodeAsync(callOff));

        await using var check = _fx.Db.CreateContext(s.Tenant.TenantId);
        Assert.False(await check.Appointments.AnyAsync()); // жоден запис не створено
    }

    // ---------- дзвінок ----------

    [SkippableFact]
    public async Task call_plan_creates_one_open_task_and_no_reminder_rows()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 14);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s), Call(1440)));

        Assert.Empty(await RemindersOfAsync(s, id));
        var t = Assert.Single(await TasksOfAsync(s, id));
        Assert.Equal("reminder", t.Reason);
        Assert.Equal("open", t.Status);
        Assert.Equal((short)1440, t.OffsetMinutes);
        Assert.Equal(start.AddMinutes(-1440), t.DueAt);
        Assert.Null(t.ReminderId);
        var point = Assert.Single(json.GetProperty("reminders").GetProperty("points").EnumerateArray());
        Assert.Equal("call", point.GetProperty("kind").GetString());
        Assert.Equal("open", point.GetProperty("status").GetString());
    }

    // ---------- сценарії 3, 4: тиша й злиття в зоні закладу ----------

    [SkippableFact]
    public async Task quiet_hours_shift_to_08_00_local_and_the_boundary_is_kept()
    {
        NeedDb();
        var s = await SalonAsync();
        var client = await ClientAsync(s, telegram: true);
        var date = FutureDate();

        var at0930 = await SlotAsync(s, date, 9, 30);
        var (id1, _) = await CreatedAsync(await PostAsync(s, at0930, client, Tg(120))); // 07:30 -> 08:00
        var r1 = Assert.Single(await RemindersOfAsync(s, id1));
        Assert.Equal(ReminderStatus.Scheduled, r1.Status);
        Assert.Equal(new TimeOnly(8, 0), Local(r1.ScheduledAt));
        Assert.Equal(at0930.AddMinutes(-90), r1.ScheduledAt);

        var at0900 = await SlotAsync(s, FutureDate(7), 9);
        var (id2, _) = await CreatedAsync(await PostAsync(s, at0900, client, Tg(120))); // межа: лишається рівно 60 хв
        var r2 = Assert.Single(await RemindersOfAsync(s, id2));
        Assert.Equal(ReminderStatus.Scheduled, r2.Status);
        Assert.Equal(new TimeOnly(8, 0), Local(r2.ScheduledAt));

        var at1100 = await SlotAsync(s, FutureDate(8), 11);
        var (id3, _) = await CreatedAsync(await PostAsync(s, at1100, client, Tg(1440))); // 11:00 попередньої доби - не тиша
        Assert.Equal(at1100.AddHours(-24), Assert.Single(await RemindersOfAsync(s, id3)).ScheduledAt);
    }

    [SkippableFact]
    public async Task points_closer_than_60_minutes_are_merged_with_a_skipped_row()
    {
        NeedDb();
        var s = await SalonAsync();
        var start = await SlotAsync(s, FutureDate(), 9, 30);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(60, 120)));

        var rows = await RemindersOfAsync(s, id);
        var kept = rows.Single(r => r.OffsetMinutes == 60);
        Assert.Equal(ReminderStatus.Scheduled, kept.Status);
        Assert.Equal(start.AddHours(-1), kept.ScheduledAt);
        var merged = rows.Single(r => r.OffsetMinutes == 120);
        Assert.Equal(ReminderStatus.Skipped, merged.Status);
        Assert.Equal("merged", merged.SkipReason);
        Assert.Equal(new TimeOnly(8, 0), Local(merged.ScheduledAt));

        var skipped = json.GetProperty("reminders").GetProperty("points").EnumerateArray().Single(p => p.GetProperty("offsetMinutes").GetInt32() == 120);
        Assert.Equal("skipped", skipped.GetProperty("status").GetString());
        Assert.Equal("merged", skipped.GetProperty("skipReason").GetString());
    }

    // ---------- сценарій 5: DST ----------

    [SkippableFact]
    public async Task day_before_reminder_is_exactly_24_hours_across_the_spring_dst_transition()
    {
        NeedDb();
        var s = await SalonAsync();
        // 2030-03-31 (неділя) - перехід Києва на літній час; візит 12:00 за місцевим часом (UTC+3)
        var start = await SlotAsync(s, "2030-03-31", 12);
        var (id, _) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(1440)));

        var r = Assert.Single(await RemindersOfAsync(s, id));
        Assert.Equal(start.AddHours(-24), r.ScheduledAt);
        Assert.Equal(new TimeOnly(11, 0), Local(r.ScheduledAt)); // субота, ще UTC+2
    }

    // ---------- сценарій 7: перенос, скасування, статуси ----------

    [SkippableFact]
    public async Task reschedule_cancels_old_rows_as_replanned_and_creates_new_ones_by_the_same_plan()
    {
        NeedDb();
        var s = await SalonAsync();
        var date = FutureDate();
        var start = await SlotAsync(s, date, 14);
        var moved = await SlotAsync(s, date, 16);
        var (id, _) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(120, 1440)));

        var patch = await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { startsAt = moved }, s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var body = await Read<JsonElement>(patch);
        Assert.Equal(2, body.GetProperty("reminders").GetProperty("points").GetArrayLength());

        var rows = await RemindersOfAsync(s, id);
        Assert.Equal(4, rows.Count);
        var old = rows.Where(r => r.Status == ReminderStatus.Cancelled).ToList();
        Assert.Equal(2, old.Count);
        Assert.All(old, r => Assert.Equal("replanned", r.SkipReason));
        Assert.All(old, r => Assert.Equal(start, r.PlannedStartsAt));
        var fresh = rows.Where(r => r.Status == ReminderStatus.Scheduled).ToList();
        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, r => Assert.Equal(moved, r.PlannedStartsAt));
        Assert.Equal(moved.AddHours(-2), fresh.Single(r => r.OffsetMinutes == 120).ScheduledAt);
        Assert.Equal(moved.AddHours(-24), fresh.Single(r => r.OffsetMinutes == 1440).ScheduledAt);

        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var a = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("telegram", a.ReminderMethod);
        Assert.Equal(new short[] { 120, 1440 }, a.ReminderOffsets);
    }

    [SkippableFact]
    public async Task reschedule_replaces_the_open_call_task()
    {
        NeedDb();
        var s = await SalonAsync();
        var date = FutureDate();
        var start = await SlotAsync(s, date, 14);
        var moved = await SlotAsync(s, date, 16);
        var (id, _) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s), Call(120)));

        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { startsAt = moved }, s.OwnerToken)).StatusCode);

        var tasks = await TasksOfAsync(s, id);
        Assert.Equal(2, tasks.Count);
        var old = tasks.Single(t => t.Status == "cancelled");
        Assert.Equal("replanned", old.ClosedReason);
        Assert.Equal(start.AddHours(-2), old.DueAt);
        var open = tasks.Single(t => t.Status == "open");
        Assert.Equal(moved.AddHours(-2), open.DueAt);
    }

    [SkippableFact]
    public async Task reschedule_also_closes_planner_skipped_rows_so_the_old_plan_does_not_remain()
    {
        NeedDb();
        var s = await SalonAsync();
        var date = FutureDate();
        var start = await SlotAsync(s, date, 9, 30);
        var moved = await SlotAsync(s, date, 14);
        var (id, _) = await CreatedAsync(await PostAsync(s, start, await ClientAsync(s, telegram: true), Tg(60, 120))); // 120 -> merged

        var patch = await Read<JsonElement>(await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { startsAt = moved }, s.OwnerToken));
        var points = patch.GetProperty("reminders").GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(2, points.Count); // тільки нові точки; старий merged не дублюється
        Assert.All(points, p => Assert.Equal("scheduled", p.GetProperty("status").GetString()));
        Assert.DoesNotContain(await RemindersOfAsync(s, id), r => r.Status == ReminderStatus.Skipped);
    }

    [SkippableFact]
    public async Task cancel_closes_reminders_and_call_tasks_as_appointment_inactive()
    {
        NeedDb();
        var s = await SalonAsync();
        var date = FutureDate();
        var client = await ClientAsync(s, telegram: true);
        var (tgId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 14), client, Tg(120, 1440)));
        var (callId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 16), client, Call(1440)));

        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{tgId}/cancel", new { }, s.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{callId}/cancel", new { }, s.OwnerToken)).StatusCode);

        var rows = await RemindersOfAsync(s, tgId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Equal(ReminderStatus.Cancelled, r.Status); Assert.Equal("appointment_inactive", r.SkipReason); });
        var task = Assert.Single(await TasksOfAsync(s, callId));
        Assert.Equal("cancelled", task.Status);
        Assert.Equal("appointment_inactive", task.ClosedReason);
        Assert.Null(task.CompletedByUserId);
    }

    [SkippableTheory]
    [InlineData("completed")]
    [InlineData("no_show")]
    public async Task completed_and_no_show_close_scheduled_reminders_and_open_tasks(string status)
    {
        NeedDb();
        var s = await SalonAsync();
        var date = FutureDate();
        var client = await ClientAsync(s, telegram: true);
        var (tgId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 14), client, Tg(60, 1440)));
        var (callId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 16), client, Call(120)));

        foreach (var id in new[] { tgId, callId })
            Assert.Equal(HttpStatusCode.OK, (await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { status }, s.OwnerToken)).StatusCode);

        Assert.All(await RemindersOfAsync(s, tgId), r => { Assert.Equal(ReminderStatus.Cancelled, r.Status); Assert.Equal("appointment_inactive", r.SkipReason); });
        var task = Assert.Single(await TasksOfAsync(s, callId));
        Assert.Equal("cancelled", task.Status);
        Assert.Equal("appointment_inactive", task.ClosedReason);
    }

    [SkippableFact]
    public async Task confirming_does_not_touch_reminders()
    {
        NeedDb();
        var s = await SalonAsync();
        var (id, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, FutureDate(), 14), await ClientAsync(s, telegram: true), Tg(60)));
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(Patch, $"/api/beauty/appointments/{id}", new { status = "confirmed" }, s.OwnerToken)).StatusCode);
        Assert.Equal(ReminderStatus.Scheduled, Assert.Single(await RemindersOfAsync(s, id)).Status);
    }

    // ---------- сценарій 13: вподобання застосовується ----------

    [SkippableFact]
    public async Task client_preference_is_applied_when_no_reminder_fields_are_given()
    {
        NeedDb();
        var s = await SalonAsync();
        var pref = new ClientContactPreference { Method = "telegram", OffsetsMinutes = [1440, 120], Source = "chat" };
        var client = await ClientAsync(s, telegram: true, pref: pref);
        var start = await SlotAsync(s, FutureDate(), 14);
        var (id, json) = await CreatedAsync(await PostAsync(s, start, client, null));

        var rows = await RemindersOfAsync(s, id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("telegram", r.Method));
        Assert.Equal("custom", json.GetProperty("reminderOption").GetString());
    }

    [SkippableFact]
    public async Task call_preference_creates_a_task_and_explicit_none_overrides_the_preference()
    {
        NeedDb();
        var s = await SalonAsync();
        var client = await ClientAsync(s, pref: new ClientContactPreference { Method = "call", OffsetsMinutes = [1440], Source = "admin" });
        var date = FutureDate();
        var (callId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 14), client, null));
        Assert.Single(await TasksOfAsync(s, callId));

        var (noneId, _) = await CreatedAsync(await PostAsync(s, await SlotAsync(s, date, 16), client, new { method = "none", offsetsMinutes = Array.Empty<int>() }));
        Assert.Empty(await RemindersOfAsync(s, noneId));
        Assert.Empty(await TasksOfAsync(s, noneId));
    }

    // ---------- сценарій 21: регрес ----------

    [SkippableFact]
    public async Task old_clients_sending_only_reminder_none_still_work()
    {
        NeedDb();
        var s = await SalonAsync();
        var r = await _h.BookAsync(s, await SlotAsync(s, FutureDate(), 14), reminder: "none");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var json = await Read<JsonElement>(r);
        Assert.Equal("none", json.GetProperty("reminderOption").GetString());
        Assert.Empty(await RemindersOfAsync(s, json.GetProperty("id").GetGuid()));
    }
}
