using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Beauty;
using BeautyCrm.Tests.Regression;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Tests.Reminders;

/// <summary>
/// TASK-737 (§22.4 «Переплан», §22.12 сценарії 7, 13, 21): переплан наявних записів за новим вподобанням (store-порт для TASK-738/740)
/// і запис із чату з реальним сховищем: за вподобанням, а без нього - без нагадувань і з ознакою «потрібен вибір».
/// </summary>
public sealed class ReminderReplanDbTests : IClassFixture<AuthApiFixture>
{
    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;

    public ReminderReplanDbTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private static string Date(int days) => DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd");
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static TimeOnly Local(DateTimeOffset t) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, Kyiv).DateTime);

    private async Task<DateTimeOffset> SlotAsync(RegressionHarness.Salon s, string date, int hour) =>
        (await _h.SlotsAsync(s, date)).Select(x => x.GetProperty("startsAt").GetDateTimeOffset()).First(t => Local(t) == new TimeOnly(hour, 0));

    private async Task<Guid> BookAsync(RegressionHarness.Salon s, DateTimeOffset start, Guid clientId, object? reminders, string? reminder = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["locationId"] = s.LocationId, ["specialistId"] = s.SpecialistId, ["serviceId"] = s.ServiceId, ["startsAt"] = start,
            ["client"] = new { id = clientId }, ["paymentMethod"] = "cash",
        };
        if (reminders is not null) body["reminders"] = reminders;
        if (reminder is not null) body["reminder"] = reminder;
        var r = await _fx.Send(HttpMethod.Post, "/api/beauty/appointments", body, s.OwnerToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AuthApiFixture.Read<JsonElement>(r)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> ClientAsync(RegressionHarness.Salon s, bool phone = true)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var c = new Client { FullName = "Клієнт", Phone = phone ? "+38050" + Random.Shared.Next(1000000, 9999999) : null };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<T> WithStoreAsync<T>(Guid tenantId, Func<EfBeautyStore, Task<T>> action)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        return await action(new EfBeautyStore(ctx, new NoSecrets()));
    }

    private async Task<(List<Reminder> Rows, List<CallTask> Tasks, Appointment Appointment)> StateAsync(RegressionHarness.Salon s, Guid id)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        return (await ctx.Reminders.AsNoTracking().Where(r => r.AppointmentId == id).OrderBy(r => r.CreatedAt).ToListAsync(),
            await ctx.CallTasks.AsNoTracking().Where(t => t.AppointmentId == id).OrderBy(t => t.CreatedAt).ToListAsync(),
            await ctx.Appointments.AsNoTracking().SingleAsync(a => a.Id == id));
    }

    // ---------- переплан за вподобанням ----------

    [SkippableFact]
    public async Task replan_upcoming_replaces_the_plan_of_future_active_appointments_only()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var client = await ClientAsync(s);
        var t1 = await SlotAsync(s, Date(6), 14);
        var t2 = await SlotAsync(s, Date(7), 14);
        var t3 = await SlotAsync(s, Date(8), 14);
        var legacy = await BookAsync(s, t1, client, null, "1h");
        var call = await BookAsync(s, t2, client, new { method = "call", offsetsMinutes = new[] { 1440 } });
        var cancelled = await BookAsync(s, t3, client, null, "2h");
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{cancelled}/cancel", new { }, s.OwnerToken)).StatusCode);

        var replanned = await WithStoreAsync(s.Tenant.TenantId, st => st.ReplanUpcomingAsync(client, "telegram", [120, 1440], 50, DateTimeOffset.UtcNow, default));

        Assert.Equal(2, replanned); // cancelled не чіпається
        var a = await StateAsync(s, legacy);
        Assert.Equal("telegram", a.Appointment.ReminderMethod);
        Assert.Equal(new short[] { 120, 1440 }, a.Appointment.ReminderOffsets);
        Assert.Equal(ReminderOption.Custom, a.Appointment.ReminderOption);
        var old = Assert.Single(a.Rows, r => r.Status == ReminderStatus.Cancelled);
        Assert.Equal("replanned", old.SkipReason);
        var fresh = a.Rows.Where(r => r.Status == ReminderStatus.Scheduled).ToList();
        Assert.Equal(2, fresh.Count);
        Assert.Equal(t1.AddHours(-2), fresh.Single(r => r.OffsetMinutes == 120).ScheduledAt);
        Assert.Equal(t1.AddHours(-24), fresh.Single(r => r.OffsetMinutes == 1440).ScheduledAt);

        var c = await StateAsync(s, call);
        Assert.Equal("cancelled", Assert.Single(c.Tasks, t => t.Status == "cancelled").Status);
        Assert.Equal("replanned", c.Tasks.Single(t => t.Status == "cancelled").ClosedReason);
        Assert.Empty(c.Tasks.Where(t => t.Status == "open"));
        Assert.Equal(2, c.Rows.Count(r => r.Status == ReminderStatus.Scheduled));

        var x = await StateAsync(s, cancelled);
        Assert.Equal("auto", x.Appointment.ReminderMethod); // скасований запис не переплановується
        Assert.All(x.Rows, r => Assert.Equal(ReminderStatus.Cancelled, r.Status));
        Assert.Equal("appointment_inactive", x.Rows.Single().SkipReason);
    }

    [SkippableFact]
    public async Task replan_to_none_closes_every_scheduled_row_and_open_task_and_writes_no_new_points()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var client = await ClientAsync(s);
        var id = await BookAsync(s, await SlotAsync(s, Date(6), 14), client, null, "2h");

        Assert.Equal(1, await WithStoreAsync(s.Tenant.TenantId, st => st.ReplanUpcomingAsync(client, "none", [], 50, DateTimeOffset.UtcNow, default)));

        var a = await StateAsync(s, id);
        Assert.Equal("none", a.Appointment.ReminderMethod);
        Assert.Empty(a.Appointment.ReminderOffsets);
        Assert.Equal(ReminderOption.None, a.Appointment.ReminderOption);
        Assert.DoesNotContain(a.Rows, r => r.Status == ReminderStatus.Scheduled);
        Assert.Equal("replanned", Assert.Single(a.Rows).SkipReason);
    }

    [SkippableFact]
    public async Task replan_upcoming_respects_the_max_and_takes_the_nearest_first()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var client = await ClientAsync(s);
        var near = await BookAsync(s, await SlotAsync(s, Date(6), 14), client, null, "1h");
        var far = await BookAsync(s, await SlotAsync(s, Date(9), 14), client, null, "1h");

        Assert.Equal(1, await WithStoreAsync(s.Tenant.TenantId, st => st.ReplanUpcomingAsync(client, "telegram", [60], 1, DateTimeOffset.UtcNow, default)));

        Assert.Equal("telegram", (await StateAsync(s, near)).Appointment.ReminderMethod);
        Assert.Equal("auto", (await StateAsync(s, far)).Appointment.ReminderMethod);
    }

    [SkippableFact]
    public async Task replan_call_without_a_phone_stores_no_task()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var client = await ClientAsync(s, phone: false);
        var id = await BookAsync(s, await SlotAsync(s, Date(6), 14), client, null);

        Assert.Equal(1, await WithStoreAsync(s.Tenant.TenantId, st => st.ReplanUpcomingAsync(client, "call", [1440], 50, DateTimeOffset.UtcNow, default)));

        var a = await StateAsync(s, id);
        Assert.Equal("call", a.Appointment.ReminderMethod);
        Assert.Empty(a.Tasks);
    }

    [SkippableFact]
    public async Task replan_appointment_returns_false_for_a_foreign_or_closed_appointment_and_joins_the_callers_transaction()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var other = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var client = await ClientAsync(s);
        var id = await BookAsync(s, await SlotAsync(s, Date(6), 14), client, null, "1h");

        // чужий tenant: запис невидимий під RLS
        Assert.False(await WithStoreAsync(other.Tenant.TenantId, st => st.ReplanAppointmentAsync(id, "telegram", [60], DateTimeOffset.UtcNow, default)));

        // відкат зовнішньої транзакції відкочує й переплан (спільна транзакція з upsert вподобання)
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
        {
            await using var tx = await ctx.Database.BeginTransactionAsync();
            Assert.True(await new EfBeautyStore(ctx, new NoSecrets()).ReplanAppointmentAsync(id, "telegram", [120], DateTimeOffset.UtcNow, default));
            await tx.RollbackAsync();
        }
        Assert.Equal("auto", (await StateAsync(s, id)).Appointment.ReminderMethod);

        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", new { }, s.OwnerToken)).StatusCode);
        Assert.False(await WithStoreAsync(s.Tenant.TenantId, st => st.ReplanAppointmentAsync(id, "telegram", [60], DateTimeOffset.UtcNow, default)));
    }

    // ---------- чат: реальне сховище ----------

    private sealed record ChatSeed(Guid ConversationId);

    private async Task<ChatSeed> NewConversationAsync(RegressionHarness.Salon s)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ExternalChatId = Guid.NewGuid().ToString("N")[..10] };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        return new ChatSeed(conv.Id);
    }

    private async Task<T> WithChatAsync<T>(Guid tenantId, Func<ChatBookingService, Task<T>> action)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        var store = new EfBeautyStore(ctx, new NoSecrets());
        var booking = new BookingService(store, new SpyPayments(), new CancellationSettingsService(store), TimeProvider.System, store);
        var pub = new EfPublicBookingStore(ctx);
        var core = new OnlineBookingCore(pub, booking, new PublicBookingOptions(), TimeProvider.System);
        return await action(new ChatBookingService(new EfChatBookingStore(ctx), pub, store, core, new ChatBookingOptions(), TimeProvider.System, store));
    }

    private async Task<Guid> NewSummaryMessageAsync(RegressionHarness.Salon s, Guid conversationId)
    {
        await using var ctx = _fx.Db.CreateContext(s.Tenant.TenantId);
        var msg = new Message { ConversationId = conversationId, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "summary", Status = "sending" };
        ctx.Add(msg);
        await ctx.SaveChangesAsync();
        return msg.Id;
    }

    private async Task<ChatConfirmed> ConfirmChatBookingAsync(RegressionHarness.Salon s, ChatSeed seed, DateTimeOffset start)
    {
        var summaryMessage = await NewSummaryMessageAsync(s, seed.ConversationId);
        var prepared = await WithChatAsync(s.Tenant.TenantId, async svc =>
        {
            var r = await svc.PrepareAsync(new ChatPrepareRequest(seed.ConversationId, s.LocationId, s.ServiceId, s.SpecialistId, start), default);
            Assert.True(r.IsOk, r.Error?.Code);
            Assert.True(await svc.MarkShownAsync(seed.ConversationId, r.Value!.ProposalId, summaryMessage, default));
            return r.Value;
        });
        var confirmed = await WithChatAsync(s.Tenant.TenantId, svc =>
            svc.ConfirmAsync(new ChatConfirmRequest(seed.ConversationId, prepared.ProposalId, true, new TelegramProfile("Анна", "Коваль", null)), default));
        Assert.True(confirmed.IsOk, confirmed.Error?.Code);
        return confirmed.Value!;
    }

    [SkippableFact]
    public async Task chat_booking_without_a_preference_has_no_reminders_and_asks_for_a_choice()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var seed = await NewConversationAsync(s);
        var start = await SlotAsync(s, Date(4), 14);

        var confirmed = await ConfirmChatBookingAsync(s, seed, start);

        Assert.True(confirmed.ReminderChoiceNeeded);
        var a = await StateAsync(s, confirmed.Appointment.Id);
        Assert.Equal("none", a.Appointment.ReminderMethod);
        Assert.Empty(a.Appointment.ReminderOffsets);
        Assert.Equal(ReminderOption.None, a.Appointment.ReminderOption);
        Assert.Empty(a.Rows);
        Assert.Empty(a.Tasks);
        Assert.Equal("telegram", confirmed.Appointment.Source);
    }

    [SkippableFact]
    public async Task chat_booking_with_a_preference_is_planned_by_it_and_does_not_ask_again()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync(timezone: "Europe/Kyiv");
        var seed = await NewConversationAsync(s);

        // перший запис без вподобання створює лінивого клієнта розмови; далі клієнт обирає Telegram за 24 год і 2 год
        var first = await ConfirmChatBookingAsync(s, seed, await SlotAsync(s, Date(4), 14));
        Assert.True(first.ReminderChoiceNeeded);
        await using (var ctx = _fx.Db.CreateContext(s.Tenant.TenantId))
        {
            ctx.ClientContactPreferences.Add(new ClientContactPreference
            {
                ClientId = first.Appointment.ClientId, Method = "telegram", OffsetsMinutes = [1440, 120], Source = "chat", TelegramConversationId = seed.ConversationId,
            });
            await ctx.SaveChangesAsync();
        }

        var start = await SlotAsync(s, Date(5), 14);
        var second = await ConfirmChatBookingAsync(s, seed, start);

        Assert.False(second.ReminderChoiceNeeded);
        Assert.Equal("custom", second.Appointment.ReminderOption);
        var a = await StateAsync(s, second.Appointment.Id);
        Assert.Equal("telegram", a.Appointment.ReminderMethod);
        Assert.Equal(2, a.Rows.Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.Equal(start.AddHours(-2), a.Rows.Single(r => r.OffsetMinutes == 120).ScheduledAt);
        Assert.Equal(start.AddHours(-24), a.Rows.Single(r => r.OffsetMinutes == 1440).ScheduledAt);
    }
}
