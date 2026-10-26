using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Beauty;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.ChatBooking;

/// <summary>TASK-726 на реальному PostgreSQL (RLS, exclusion, unique-ключ ідемпотентності): EfChatBookingStore + справжні сховища запису.</summary>
public sealed class ChatBookingDbTests : IClassFixture<PostgresRlsFixture>
{
    private const string Hours =
        """{"mon":[{"from":"08:00","to":"20:00"}],"tue":[{"from":"08:00","to":"20:00"}],"wed":[{"from":"08:00","to":"20:00"}],"thu":[{"from":"08:00","to":"20:00"}],"fri":[{"from":"08:00","to":"20:00"}],"sat":[{"from":"08:00","to":"20:00"}],"sun":[{"from":"08:00","to":"20:00"}]}""";

    private readonly PostgresRlsFixture _db;
    public ChatBookingDbTests(PostgresRlsFixture db) => _db = db;
    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed record Seed(Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ChannelId, DateTimeOffset Start);

    private async Task<Seed> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'cb-{tenantId.ToString("N")[..20]}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var ctx = _db.CreateContext(tenantId);
        var loc = new Location { Name = "Main", Address = "Street 1", Timezone = "Europe/Kyiv" };
        var sp = new Specialist { FullName = "Master A" };
        var svc = new Service { Name = "Haircut", DurationMinutes = 60 };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, sp, svc, ch);
        await ctx.SaveChangesAsync();
        ctx.Add(new SpecialistLocation { SpecialistId = sp.Id, LocationId = loc.Id, WorkingHours = Hours });
        ctx.Add(new SpecialistServiceLink { SpecialistId = sp.Id, ServiceId = svc.Id });
        ctx.Add(new ServicePrice { ServiceId = svc.Id, LocationId = null, Price = 500m });
        await ctx.SaveChangesAsync();

        var tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        var local = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(3).AddHours(10), DateTimeKind.Unspecified);
        return new Seed(tenantId, loc.Id, sp.Id, svc.Id, ch.Id, new DateTimeOffset(local, tz.GetUtcOffset(local)));
    }

    private async Task<(Guid Conversation, Guid Message)> NewConversationAsync(Seed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var conv = new Conversation { ChannelId = s.ChannelId, ExternalChatId = Guid.NewGuid().ToString("N")[..10] };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        var msg = new Message { ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "summary", Status = "sending" };
        ctx.Add(msg);
        await ctx.SaveChangesAsync();
        return (conv.Id, msg.Id);
    }

    /// <summary>Свій контекст (окреме з'єднання) на кожну операцію, як scope запиту.</summary>
    private async Task<T> WithServiceAsync<T>(Guid tenantId, Func<ChatBookingService, Task<T>> action)
    {
        await using var ctx = _db.CreateContext(tenantId);
        var store = new EfBeautyStore(ctx, new NoSecrets());
        var booking = new BookingService(store, new SpyPayments(), new CancellationSettingsService(store), TimeProvider.System);
        var pub = new EfPublicBookingStore(ctx);
        var core = new OnlineBookingCore(pub, booking, new PublicBookingOptions(), TimeProvider.System);
        return await action(new ChatBookingService(new EfChatBookingStore(ctx), pub, store, core, new ChatBookingOptions(), TimeProvider.System));
    }

    private static ChatConfirmRequest Confirm(Guid conv, Guid proposal) =>
        new(conv, proposal, true, new TelegramProfile("Анна", "Коваль", null));

    private Task<ChatPrepared> PrepareShownAsync(Seed s, Guid conv, Guid msg) => WithServiceAsync(s.TenantId, async svc =>
    {
        var r = await svc.PrepareAsync(new ChatPrepareRequest(conv, s.LocationId, s.ServiceId, s.SpecialistId, s.Start), default);
        Assert.True(r.IsOk, r.Error?.Code);
        Assert.True(await svc.MarkShownAsync(conv, r.Value!.ProposalId, msg, default));
        return r.Value;
    });

    [SkippableFact]
    public async Task prepare_then_confirm_creates_one_telegram_cash_booking_and_lazy_phoneless_client()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var (conv, msg) = await NewConversationAsync(s);
        var p = await PrepareShownAsync(s, conv, msg);

        await using (var ctx = _db.CreateContext(s.TenantId)) // до «Так» запису й клієнта немає
        {
            Assert.Equal(0, await ctx.Appointments.CountAsync());
            Assert.Equal(0, await ctx.Clients.CountAsync());
            Assert.Null((await ctx.Conversations.SingleAsync()).ClientId);
        }

        var first = await WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(conv, p.ProposalId), default));
        Assert.True(first.IsOk, first.Error?.Code);
        var again = await WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(conv, p.ProposalId), default));
        Assert.True(again.Value!.AlreadyConfirmed);
        Assert.Equal(first.Value!.Appointment.Id, again.Value.Appointment.Id);

        await using var check = _db.CreateContext(s.TenantId);
        var appt = await check.Appointments.SingleAsync();
        Assert.Equal(AppointmentSource.Telegram, appt.Source);
        Assert.Equal(AppointmentStatus.Confirmed, appt.Status);
        Assert.Equal(BeautyCrm.Infrastructure.Data.Entities.PaymentMethod.Cash, appt.PaymentMethod);
        Assert.Equal(500m, appt.PriceFinal);
        Assert.NotNull(appt.IdempotencyKeyHash);
        Assert.Null(appt.PublicTokenHash);
        var client = await check.Clients.SingleAsync();
        Assert.Equal("Анна Коваль", client.FullName);
        Assert.Null(client.Phone);
        Assert.False(client.MarketingConsent);
        Assert.Equal(client.Id, (await check.Conversations.SingleAsync()).ClientId);
        var proposal = await check.ChatBookingProposals.SingleAsync();
        Assert.Equal("confirmed", proposal.Status);
        Assert.Equal(appt.Id, proposal.AppointmentId);
        Assert.Equal(client.Id, proposal.ClientId);
    }

    [SkippableFact]
    public async Task parallel_confirms_of_one_proposal_yield_exactly_one_appointment()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var (conv, msg) = await NewConversationAsync(s);
        var p = await PrepareShownAsync(s, conv, msg);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(conv, p.ProposalId), default)))));

        Assert.Contains(results, r => r.IsOk && !r.Value!.AlreadyConfirmed);
        Assert.All(results, r => Assert.True(r.IsOk || r.Error!.Code == "confirmation_in_progress", r.Error?.Code));
        await using var check = _db.CreateContext(s.TenantId);
        Assert.Equal(1, await check.Appointments.CountAsync());
        Assert.Equal(1, await check.Clients.CountAsync());
    }

    [SkippableFact]
    public async Task parallel_confirms_of_two_conversations_for_one_slot_yield_one_appointment()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var a = await NewConversationAsync(s);
        var b = await NewConversationAsync(s);
        var pa = await PrepareShownAsync(s, a.Conversation, a.Message);
        var pb = await PrepareShownAsync(s, b.Conversation, b.Message);

        var results = await Task.WhenAll(
            Task.Run(() => WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(a.Conversation, pa.ProposalId), default))),
            Task.Run(() => WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(b.Conversation, pb.ProposalId), default))));

        Assert.Equal(1, results.Count(r => r.IsOk));
        Assert.Equal("slot_unavailable", results.Single(r => !r.IsOk).Error!.Code);
        await using var check = _db.CreateContext(s.TenantId);
        Assert.Equal(1, await check.Appointments.CountAsync());
    }

    [SkippableFact]
    public async Task proposals_and_conversations_of_another_tenant_are_unreachable()
    {
        SkipIfNoDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        var (conv, msg) = await NewConversationAsync(a);
        var p = await PrepareShownAsync(a, conv, msg);

        var confirm = await WithServiceAsync(b.TenantId, svc => svc.ConfirmAsync(Confirm(conv, p.ProposalId), default));
        Assert.Equal("proposal_not_found", confirm.Error!.Code);
        var prepare = await WithServiceAsync(b.TenantId, svc =>
            svc.PrepareAsync(new ChatPrepareRequest(conv, b.LocationId, b.ServiceId, b.SpecialistId, b.Start), default));
        Assert.Equal("conversation_not_found", prepare.Error!.Code);
        // і в межах tenant: чужа розмова
        var (otherConv, _) = await NewConversationAsync(a);
        Assert.Equal("proposal_not_found", (await WithServiceAsync(a.TenantId, svc => svc.ConfirmAsync(Confirm(otherConv, p.ProposalId), default))).Error!.Code);

        await using var check = _db.CreateContext(a.TenantId);
        Assert.Equal(0, await check.Appointments.CountAsync());
        Assert.Equal("shown", (await check.ChatBookingProposals.SingleAsync()).Status);
    }

    [SkippableFact]
    public async Task new_prepare_supersedes_active_proposal_in_database()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var (conv, msg) = await NewConversationAsync(s);
        var first = await PrepareShownAsync(s, conv, msg);
        var second = await WithServiceAsync(s.TenantId, svc =>
            svc.PrepareAsync(new ChatPrepareRequest(conv, s.LocationId, s.ServiceId, s.SpecialistId, s.Start.AddMinutes(60)), default));
        Assert.True(second.IsOk, second.Error?.Code);

        await using var check = _db.CreateContext(s.TenantId);
        var rows = await check.ChatBookingProposals.ToDictionaryAsync(x => x.Id, x => x.Status);
        Assert.Equal("superseded", rows[first.ProposalId]);
        Assert.Equal("prepared", rows[second.Value!.ProposalId]);
        Assert.Equal("proposal_not_active", (await WithServiceAsync(s.TenantId, svc => svc.ConfirmAsync(Confirm(conv, first.ProposalId), default))).Error!.Code);
    }
}
