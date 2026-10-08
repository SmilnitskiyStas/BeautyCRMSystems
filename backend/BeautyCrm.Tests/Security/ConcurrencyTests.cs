using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.AI.Beauty.Fakes;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Tests.Regression;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;
using AiStatus = BeautyCrm.Infrastructure.AI.Beauty.AiActionStatus;

namespace BeautyCrm.Tests.Security;

/// <summary>Платіжний провайдер-шпигун: рахує повернення, повільний (розширює вікно гонки), ключ = payment.Id.</summary>
internal sealed class RacingPayments : IPaymentService
{
    private readonly ConcurrentBag<Guid> _refundIds = [];
    public bool FailRefund;
    public int RefundCalls => _refundIds.Count;
    public IReadOnlyCollection<Guid> RefundIds => _refundIds;

    public Task<PaymentResult> ChargeAsync(Guid appointmentId, decimal amount, CancellationToken ct) =>
        Task.FromResult(new PaymentResult(true, $"pay_{appointmentId:N}", null));

    public async Task<RefundResult> RefundAsync(Guid paymentId, decimal amount, CancellationToken ct)
    {
        _refundIds.Add(paymentId);
        await Task.Delay(200, ct); // вікно, у якому другий cancel без CAS встиг би теж повернути кошти
        return FailRefund ? new RefundResult(false, null, "provider_down") : new RefundResult(true, $"refund_{paymentId:N}", null);
    }
}

/// <summary>
/// M1 (скасування), M2 (AI approve/reject/revert), L7 (inline-відправка AI проти воркера): гонки на реальному PostgreSQL
/// (роль без BYPASSRLS). Паралельні виклики мають виконати дію рівно один раз.
/// </summary>
public sealed class ConcurrencyTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private readonly RegressionHarness _h = new(fx);

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static DateTimeOffset At(int ahead, int hour = 10) => new(DateTime.UtcNow.Date.AddDays(ahead).AddHours(hour), TimeSpan.Zero);

    // ================= M1: CancellationService =================

    private HttpClient PaymentsClient(RacingPayments payments)
    {
        var f = fx.CreateFactory(permitLimit: 10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("PublicBooking:RateLimit:ReadPermit", "10000");
            b.UseSetting("PublicBooking:RateLimit:TokenPermit", "10000");
            b.UseSetting("PublicBooking:RateLimit:WritePermit", "10000");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IPaymentService>();
                s.AddSingleton<IPaymentService>(payments);
            });
        });
        _factories.Add(f);
        return f.CreateClient();
    }

    private async Task<Guid> BookCardAsync(RegressionHarness.Salon s, HttpClient c, DateTimeOffset at)
    {
        var r = await fx.Send(HttpMethod.Post, "/api/beauty/appointments", RegressionHarness.Booking(s, at, payment: "card"), s.OwnerToken, client: c);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await Read<JsonElement>(r)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetAsync(RegressionHarness.Salon s, Guid id)
    {
        var r = await fx.Send(HttpMethod.Get, $"/api/beauty/appointments/{id}", bearer: s.OwnerToken);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await Read<JsonElement>(r);
    }

    private async Task<(string PaymentStatus, int Count)> PaymentsOfAsync(Guid tenantId, Guid appointmentId)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        var rows = await ctx.Payments.Where(p => p.AppointmentId == appointmentId).ToListAsync();
        return (EnumText<PaymentStatus>.ToDb(rows.Single().Status), rows.Count);
    }

    [SkippableFact]
    public async Task parallel_staff_cancel_refunds_exactly_once()
    {
        NeedDb();
        var payments = new RacingPayments();
        var client = PaymentsClient(payments);
        var s = await _h.CreateSalonAsync();

        for (var round = 0; round < 3; round++)
        {
            var before = payments.RefundCalls;
            var id = await BookCardAsync(s, client, At(3, 10 + round * 2));
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken, client: client)));

            Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
            var losers = results.Where(r => r.StatusCode != HttpStatusCode.OK).ToList();
            Assert.Equal(5, losers.Count);
            foreach (var l in losers)
            {
                Assert.Equal(HttpStatusCode.Conflict, l.StatusCode);
                Assert.Equal("already_cancelled", await RegressionHarness.CodeAsync(l));
            }
            Assert.Equal(before + 1, payments.RefundCalls); // рівно одне повернення
            Assert.Equal("cancelled", (await GetAsync(s, id)).GetProperty("status").GetString());
            Assert.Equal(("refunded", 1), await PaymentsOfAsync(s.Tenant.TenantId, id));
        }
    }

    [SkippableFact]
    public async Task refund_idempotency_key_is_the_payment_id()
    {
        NeedDb();
        var payments = new RacingPayments();
        var client = PaymentsClient(payments);
        var s = await _h.CreateSalonAsync();
        var id = await BookCardAsync(s, client, At(3));
        Guid paymentId;
        await using (var ctx = fx.Db.CreateContext(s.Tenant.TenantId))
            paymentId = (await ctx.Payments.SingleAsync(p => p.AppointmentId == id)).Id;

        var r = await fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken, client: client);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(paymentId, Assert.Single(payments.RefundIds));
        Assert.Equal(1000m, (await Read<JsonElement>(r)).GetProperty("refundAmount").GetDecimal());
    }

    [SkippableFact]
    public async Task failed_refund_keeps_the_appointment_active_and_can_be_retried()
    {
        NeedDb();
        var payments = new RacingPayments { FailRefund = true };
        var client = PaymentsClient(payments);
        var s = await _h.CreateSalonAsync();
        var id = await BookCardAsync(s, client, At(3));

        var failed = await fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken, client: client);
        Assert.Equal(HttpStatusCode.PaymentRequired, failed.StatusCode);
        Assert.Equal("refund_failed", await RegressionHarness.CodeAsync(failed));
        var after = await GetAsync(s, id);
        Assert.Equal("confirmed", after.GetProperty("status").GetString()); // компенсація: claim скасовано
        Assert.Equal(("paid", 1), await PaymentsOfAsync(s.Tenant.TenantId, id));

        payments.FailRefund = false;
        var retry = await fx.Send(HttpMethod.Post, $"/api/beauty/appointments/{id}/cancel", bearer: s.OwnerToken, client: client);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(("refunded", 1), await PaymentsOfAsync(s.Tenant.TenantId, id));
    }

    [SkippableFact]
    public async Task parallel_public_cancel_refunds_exactly_once()
    {
        NeedDb();
        var payments = new RacingPayments();
        var client = PaymentsClient(payments);
        var s = await _h.CreateSalonAsync();

        var create = new HttpRequestMessage(HttpMethod.Post, $"/api/public/{s.Tenant.Slug}/appointments")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                locationId = s.LocationId, specialistId = s.SpecialistId, serviceId = s.ServiceId, startsAt = At(3),
                client = new { name = "Olena Test", phone = "+380501234567" }, reminder = "none", paymentMethod = "card",
            }, options: Json),
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var created = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var token = (await Read<JsonElement>(created)).GetProperty("publicToken").GetString()!;

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            client.PostAsync($"/api/public/{s.Tenant.Slug}/appointments/{token}/cancel", null)));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, payments.RefundCalls);
    }

    // ================= M2: AI compare-and-swap =================

    private sealed class RacePorts : ICreateAppointmentPort
    {
        private int _created, _cancelled;
        public bool FailCreate;
        public int Created => _created;
        public int Cancelled => _cancelled;

        public async Task<AppointmentCreated> CreateAsync(Guid t, Guid c, Guid s, Guid st, DateTimeOffset start, CancellationToken ct)
        {
            await Task.Delay(150, ct);
            if (FailCreate) throw new InvalidOperationException("slot_unavailable");
            Interlocked.Increment(ref _created);
            return new AppointmentCreated(Guid.NewGuid());
        }

        public async Task CancelAsync(Guid t, Guid appointmentId, CancellationToken ct)
        {
            await Task.Delay(150, ct);
            Interlocked.Increment(ref _cancelled);
        }
    }

    /// <summary>Виконавець зі своїм DbContext (окреме з'єднання) — як окремі HTTP-запити.</summary>
    private AiToolExecutor NewExecutor(Guid tenantId, ICreateAppointmentPort appointments)
    {
        var tenant = new TenantContext(tenantId);
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        var journal = new EfAiActionJournal(new BeautyDbContext(options.Options, tenant), tenant);
        var fake = new FakeAiPorts();
        return new AiToolExecutor(fake, appointments, fake, fake, fake, fake, fake, fake, journal, new AiSettings(), TimeProvider.System);
    }

    private async Task<Guid> PendingActionAsync(Guid tenantId)
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            serviceId = Guid.NewGuid(), staffId = Guid.NewGuid(), start = "2030-10-20T17:00:00+00:00",
        });
        var res = await NewExecutor(tenantId, new RacePorts()).ExecuteAsync(
            new AiToolContext(tenantId, Guid.NewGuid(), AiMode.Confirm, AiScope.Client), AiToolNames.CreateAppointment, input, default);
        Assert.Equal(AiStatus.PendingConfirmation, res.ActionStatus);
        return Guid.Parse(res.Content["pending_confirmation:".Length..][..36]);
    }

    private async Task<AiActionRecord> RecordAsync(Guid tenantId, Guid id)
    {
        var tenant = new TenantContext(tenantId);
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        return (await new EfAiActionJournal(new BeautyDbContext(options.Options, tenant), tenant).GetAsync(tenantId, id, default))!;
    }

    [SkippableFact]
    public async Task parallel_approve_executes_the_action_once()
    {
        NeedDb();
        var tenantId = Guid.NewGuid();
        var ports = new RacePorts();
        var id = await PendingActionAsync(tenantId);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => NewExecutor(tenantId, ports).ConfirmAsync(tenantId, id, default))));

        Assert.Single(results, r => !r.IsError);
        Assert.All(results.Where(r => r.IsError), r => Assert.Equal("action_not_pending", r.Content));
        Assert.Equal(1, ports.Created);
        var rec = await RecordAsync(tenantId, id);
        Assert.Equal(AiStatus.Done, rec.Status);
        Assert.StartsWith("appointment:", rec.Target);
        Assert.True(rec.Revertible);
    }

    [SkippableFact]
    public async Task parallel_approve_and_reject_end_in_exactly_one_outcome()
    {
        NeedDb();
        for (var round = 0; round < 3; round++)
        {
            var tenantId = Guid.NewGuid();
            var ports = new RacePorts();
            var id = await PendingActionAsync(tenantId);

            var tasks = Enumerable.Range(0, 3).Select(_ => Task.Run(() => NewExecutor(tenantId, ports).ConfirmAsync(tenantId, id, default)))
                .Concat(Enumerable.Range(0, 3).Select(_ => Task.Run(() => NewExecutor(tenantId, ports).RejectPendingAsync(tenantId, id, default))));
            var results = await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => !r.IsError));
            var rec = await RecordAsync(tenantId, id);
            if (rec.Status == AiStatus.Done) Assert.Equal(1, ports.Created);
            else
            {
                Assert.Equal(AiStatus.Rejected, rec.Status);
                Assert.Equal(0, ports.Created); // відхилену дію не виконано
            }
        }
    }

    [SkippableFact]
    public async Task parallel_reject_succeeds_once()
    {
        NeedDb();
        var tenantId = Guid.NewGuid();
        var id = await PendingActionAsync(tenantId);
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            Task.Run(() => NewExecutor(tenantId, new RacePorts()).RejectPendingAsync(tenantId, id, default))));
        Assert.Single(results, r => !r.IsError);
        Assert.Equal(AiStatus.Rejected, (await RecordAsync(tenantId, id)).Status);
    }

    [SkippableFact]
    public async Task parallel_revert_cancels_the_appointment_once()
    {
        NeedDb();
        var tenantId = Guid.NewGuid();
        var ports = new RacePorts();
        var id = await PendingActionAsync(tenantId);
        Assert.False((await NewExecutor(tenantId, ports).ConfirmAsync(tenantId, id, default)).IsError);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Task.Run(() => NewExecutor(tenantId, ports).RevertAsync(tenantId, id, default))));

        Assert.Single(results, r => !r.IsError);
        Assert.All(results.Where(r => r.IsError), r => Assert.Equal("not_revertible", r.Content));
        Assert.Equal(1, ports.Cancelled);
        var rec = await RecordAsync(tenantId, id);
        Assert.Equal(AiStatus.Reverted, rec.Status);
        Assert.False(rec.Revertible);
    }

    [SkippableFact]
    public async Task failed_approve_returns_the_action_to_pending_and_can_be_retried()
    {
        NeedDb();
        var tenantId = Guid.NewGuid();
        var id = await PendingActionAsync(tenantId);
        var broken = new RacePorts { FailCreate = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewExecutor(tenantId, broken).ConfirmAsync(tenantId, id, default));
        Assert.Equal(AiStatus.PendingConfirmation, (await RecordAsync(tenantId, id)).Status);
        Assert.Equal(0, broken.Created);

        var ok = new RacePorts();
        Assert.False((await NewExecutor(tenantId, ok).ConfirmAsync(tenantId, id, default)).IsError);
        Assert.Equal(1, ok.Created);
        Assert.Equal(AiStatus.Done, (await RecordAsync(tenantId, id)).Status);
    }

    [SkippableFact]
    public async Task cas_is_scoped_to_tenant_and_to_the_expected_status()
    {
        NeedDb();
        var tenantId = Guid.NewGuid();
        var id = await PendingActionAsync(tenantId);
        var foreignTenant = Guid.NewGuid();
        Assert.True((await NewExecutor(foreignTenant, new RacePorts()).RejectPendingAsync(foreignTenant, id, default)).IsError); // RLS ховає рядок
        Assert.Equal(AiStatus.PendingConfirmation, (await RecordAsync(tenantId, id)).Status);

        var journalTenant = new TenantContext(tenantId);
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(journalTenant));
        var journal = new EfAiActionJournal(new BeautyDbContext(options.Options, journalTenant), journalTenant);
        Assert.False(await journal.TryTransitionAsync(tenantId, id, AiStatus.Done, AiStatus.Reverting, default)); // не той статус
        Assert.True(await journal.TryTransitionAsync(tenantId, id, AiStatus.PendingConfirmation, AiStatus.Executing, default));
        Assert.False(await journal.TryTransitionAsync(tenantId, id, AiStatus.PendingConfirmation, AiStatus.Executing, default));
    }

    // ================= L7: inline-відправка AI проти воркера =================

    private sealed class ProbeAdapter(Func<Guid, Task<string?>> statusOf) : IChannelAdapter
    {
        public string Channel => ChannelIds.Telegram;
        private readonly ConcurrentBag<Guid> _sent = [];
        public ConcurrentBag<string?> StatusDuringSend { get; } = [];
        public int SentCount => _sent.Count;
        public bool VerifySignature(WebhookRequest req, string body) => false;
        public Task<IReadOnlyList<InboundMessage>> ParseInboundAsync(string body) => Task.FromResult<IReadOnlyList<InboundMessage>>([]);

        public async Task SendAsync(OutboundMessage msg, CancellationToken ct)
        {
            StatusDuringSend.Add(await statusOf(msg.MessageId));
            await Task.Delay(200, ct);
            _sent.Add(msg.MessageId);
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private BeautyDbContext NewContext(TenantContext tenant)
    {
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        return new BeautyDbContext(options.Options, tenant);
    }

    private async Task<(Guid TenantId, Guid DraftId)> SeedDraftAsync()
    {
        var tenantId = Guid.NewGuid();
        await using var db = NewContext(new TenantContext(tenantId));
        var channel = new Channel { Id = Guid.NewGuid(), Type = ChannelType.Telegram, Name = "tg", IsActive = true };
        var conversation = new Conversation { Id = Guid.NewGuid(), ChannelId = channel.Id, ExternalChatId = "42" };
        var message = new Message
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Direction = MessageDirection.Outbound,
            SenderType = MessageSenderType.Ai, Body = "Привіт", Status = "draft", SentAt = DateTimeOffset.UtcNow,
        };
        db.AddRange(channel, conversation);
        await db.SaveChangesAsync();
        db.Add(message);
        await db.SaveChangesAsync();
        return (tenantId, message.Id);
    }

    private DraftReplyAdapter NewDraftAdapter(Guid tenantId, IChannelAdapter adapter)
    {
        var tenant = new TenantContext(tenantId);
        var db = NewContext(tenant);
        var context = new ChannelRequestContext();
        var processor = new OutboxProcessor(new ChannelRegistry([adapter]), new EfChannelMessageRepository(db, context),
            new DeferredChannelQueue(NullLogger<DeferredChannelQueue>.Instance));
        return new DraftReplyAdapter(db, tenant, TimeProvider.System, new OutboundDispatcher(db, tenant, context, new PlainProtector(), processor));
    }

    private async Task<string> MessageStatusAsync(Guid tenantId, Guid id)
    {
        await using var db = NewContext(new TenantContext(tenantId));
        return (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == id)).Status;
    }

    [SkippableFact]
    public async Task aiSend_claims_the_draft_atomically_and_is_never_visible_to_the_worker_as_pending()
    {
        NeedDb();
        var (tenantId, draftId) = await SeedDraftAsync();
        var probe = new ProbeAdapter(id => MessageStatusAsync(tenantId, id).ContinueWith(t => (string?)t.Result));

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                await NewDraftAdapter(tenantId, probe).SendAsync(tenantId, draftId, default);
                return (string?)null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        })));

        Assert.Single(outcomes, o => o is null);                            // відправив один
        Assert.All(outcomes.Where(o => o is not null), o => Assert.Equal("draft_not_found", o)); // решта програли claim
        Assert.Equal(1, probe.SentCount);                                    // повідомлення пішло рівно раз
        Assert.Equal("sending", Assert.Single(probe.StatusDuringSend));      // воркер бере лише 'pending' -> не дублює
        Assert.Equal("sent", await MessageStatusAsync(tenantId, draftId));
    }

    [SkippableFact]
    public async Task aiSend_transient_failure_hands_the_message_to_the_worker_as_pending()
    {
        NeedDb();
        var (tenantId, draftId) = await SeedDraftAsync();
        var failing = new MockTelegramAdapter { FailNextSends = 1 };
        await NewDraftAdapter(tenantId, failing).SendAsync(tenantId, draftId, default); // RetryScheduled — не помилка
        Assert.Equal("pending", await MessageStatusAsync(tenantId, draftId));            // далі повтор воркером
        Assert.Empty(failing.Sent);

        // повторний inline-відправлення вже не можливе (не draft): жодного дубля
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewDraftAdapter(tenantId, failing).SendAsync(tenantId, draftId, default));
    }
}
