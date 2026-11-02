using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Tests.Auth;
using BeautyCrm.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-769 (§25.5.3, §25.7, §25.10 п. 9, 10, 18): телефон клієнта на реальній БД (RLS) через повний конвеєр API і сервіси:
/// картка, phone-pending (ролі, IDOR, 404/409/422, no-store), SavePhone (saved/unchanged/pending), дублікати й порядок пошуку,
/// маска в Inbox, повний pending у call-tasks, відсутність номерів у логах, гонки.
/// </summary>
public sealed class ClientPhoneApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class LogSink : ILoggerProvider
    {
        private readonly List<string> _lines = [];
        public string All { get { lock (_lines) return string.Join("\n", _lines); } }
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }

        private sealed class L(LogSink o) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (o._lines) o._lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
            }
        }
    }

    private sealed record Env(HttpClient Client, LogSink Logs, WebApplicationFactory<Program> Factory);

    private Env NewEnv()
    {
        var logs = new LogSink();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.ConfigureLogging(l => { l.AddProvider(logs); l.SetMinimumLevel(LogLevel.Debug); }));
        _disposables.Add(f);
        return new Env(f.CreateClient(), logs, f);
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, string Owner, string Admin, string Spec)
    {
        public Guid Id => Seed.TenantId;
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        return new Tn(t, seed, owner.AccessToken, admin.AccessToken, spec.AccessToken);
    }

    private Task<HttpResponseMessage> Call(Env e, HttpMethod m, string path, string? token, object? body = null) =>
        fx.Send(m, "/api/beauty" + path, body, token, client: e.Client);

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;
    private static void AssertNoStore(HttpResponseMessage r) => Assert.Equal("no-store", r.Headers.CacheControl?.ToString());

    private async Task<Guid> AddClientAsync(Guid tenantId, string name, string? phone, Action<Client>? tweak = null)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        var c = new Client { FullName = name, Phone = phone, PhoneSource = phone is null ? null : PhoneSources.WebForm };
        tweak?.Invoke(c);
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<Client> ClientAsync(Guid tenantId, Guid id)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        return await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == id);
    }

    private async Task<Guid> AddConversationAsync(Guid tenantId, Guid? clientId, DateTimeOffset? awaiting = null)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ClientId = clientId, ExternalChatId = Guid.NewGuid().ToString("N")[..10], AwaitingStaffSince = awaiting };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        return conv.Id;
    }

    private async Task<T> WithPhoneService<T>(Env e, Guid tenantId, Func<ClientPhoneService, Task<T>> f)
    {
        using var scope = e.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenantId);
        return await f(scope.ServiceProvider.GetRequiredService<ClientPhoneService>());
    }

    // ---------- phone-pending: ролі, IDOR, коди ----------

    [SkippableFact]
    public async Task phone_pending_specialist_gets_403_forbidden_role_even_with_an_invalid_body()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", "+380501112233", c => { c.PhonePending = "+380671112233"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = DateTimeOffset.UtcNow; });
        foreach (var body in new object?[] { new { action = "accept" }, new { action = "boom" }, null })
        {
            var r = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Spec, body);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", await Code(r));
            AssertNoStore(r);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", null, new { action = "accept" })).StatusCode);
        Assert.NotNull((await ClientAsync(t.Id, id)).PhonePending); // нічого не змінено
    }

    [SkippableFact]
    public async Task phone_pending_accept_replaces_phone_moves_source_and_consent_clears_pending_and_resolves_awaiting_without_logging_numbers()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var at = DateTimeOffset.UtcNow.AddHours(-3);
        var id = await AddClientAsync(t.Id, "A", "+380501112233", c =>
        {
            c.PhonePending = "+380671112233"; c.PhonePendingSource = "telegram_contact"; c.PhonePendingAt = at; c.CallConsentAt = null;
        });
        var conv = await AddConversationAsync(t.Id, id, awaiting: DateTimeOffset.UtcNow.AddMinutes(-5));
        var otherConv = await AddConversationAsync(t.Id, null, awaiting: DateTimeOffset.UtcNow.AddMinutes(-5));

        var r = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Admin, new { action = "accept" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        AssertNoStore(r);
        var j = await J(r);
        Assert.Equal("+380671112233", j.GetProperty("phone").GetString());
        Assert.Equal("telegram_contact", j.GetProperty("phoneSource").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("phonePending").ValueKind);
        Assert.True(Math.Abs((j.GetProperty("callConsentAt").GetDateTimeOffset() - at).TotalSeconds) < 1);

        var c = await ClientAsync(t.Id, id);
        Assert.Equal("+380671112233", c.Phone);
        Assert.Equal("telegram_contact", c.PhoneSource);
        Assert.Null(c.PhonePending);
        Assert.Null(c.PhonePendingSource);
        Assert.Null(c.PhonePendingAt);
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            Assert.Null((await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == conv)).AwaitingStaffSince);
            Assert.NotNull((await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == otherConv)).AwaitingStaffSince); // чужа розмова не зачеплена
        }

        var again = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = "accept" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("no_pending_phone", await Code(again));
        AssertNoStore(again);

        var logs = e.Logs.All;
        Assert.Contains("client_phone_pending_resolved", logs);
        foreach (var digits in new[] { "380501112233", "380671112233", "0501112233", "0671112233", "50111", "67111" })
            Assert.DoesNotContain(digits, logs);
    }

    [SkippableFact]
    public async Task phone_pending_reject_clears_pending_and_keeps_phone_and_source()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", "+380501112233", c =>
        {
            c.PhonePending = "+380671112233"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = DateTimeOffset.UtcNow; c.CallConsentAt = DateTimeOffset.UtcNow.AddDays(-1);
        });
        var before = (await ClientAsync(t.Id, id)).CallConsentAt;
        var r = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = " Reject " });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await J(r);
        Assert.Equal("+380501112233", j.GetProperty("phone").GetString());
        Assert.Equal("web_form", j.GetProperty("phoneSource").GetString());
        Assert.Equal(JsonValueKind.Null, j.GetProperty("phonePending").ValueKind);
        var c = await ClientAsync(t.Id, id);
        Assert.Null(c.PhonePending);
        Assert.Equal(before, c.CallConsentAt);
        Assert.Equal(HttpStatusCode.Conflict, (await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = "reject" })).StatusCode);
    }

    [SkippableFact]
    public async Task phone_pending_unknown_deleted_and_foreign_tenant_client_is_404_and_invalid_action_is_422()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var other = await NewTenantAsync();
        Action<Client> pend = c => { c.PhonePending = "+380671112233"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = DateTimeOffset.UtcNow; };
        var foreign = await AddClientAsync(other.Id, "F", "+380501112233", pend);
        var deleted = await AddClientAsync(t.Id, "D", "+380501110000", c => { pend(c); c.DeletedAt = DateTimeOffset.UtcNow; });
        var own = await AddClientAsync(t.Id, "O", "+380501119999", pend);

        foreach (var id in new[] { foreign, deleted, Guid.NewGuid() })
        {
            var r = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = "accept" });
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal("client_not_found", await Code(r));
            AssertNoStore(r);
        }
        Assert.NotNull((await ClientAsync(other.Id, foreign)).PhonePending); // чужий tenant не зачеплено

        foreach (var body in new object?[] { new { action = "merge" }, new { action = "" }, new { }, null })
        {
            var r = await Call(e, HttpMethod.Post, $"/clients/{own}/phone-pending", t.Owner, body);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
            Assert.Equal("invalid_action", await Code(r));
        }
        Assert.NotNull((await ClientAsync(t.Id, own)).PhonePending);
    }

    [SkippableFact]
    public async Task phone_pending_parallel_accept_and_reject_resolve_exactly_once()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", "+380501112233", c => { c.PhonePending = "+380671112233"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = DateTimeOffset.UtcNow; });
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = i % 2 == 0 ? "accept" : "reject" })));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Null((await ClientAsync(t.Id, id)).PhonePending);
    }

    // ---------- картка клієнта ----------

    [SkippableFact]
    public async Task client_card_exposes_phone_status_source_consent_pending_and_possible_duplicate_to_owner_and_admin_only()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var original = await AddClientAsync(t.Id, "Оригінал", "+380501112233");
        var consent = DateTimeOffset.UtcNow.AddDays(-1);
        var id = await AddClientAsync(t.Id, "Дубль", "+380501112233", c =>
        {
            c.PhoneSource = PhoneSources.TelegramContact; c.CallConsentAt = consent; c.DuplicateOfClientId = original;
            c.PhonePending = "+380671112233"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = consent;
        });
        var noPhone = await AddClientAsync(t.Id, "Без номера", null);

        var r = await Call(e, HttpMethod.Get, $"/clients/{id}", t.Admin);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var c = (await J(r)).GetProperty("client");
        Assert.True(c.GetProperty("hasPhone").GetBoolean());
        Assert.Equal("+380501112233", c.GetProperty("phone").GetString());
        Assert.Equal("telegram_contact", c.GetProperty("phoneSource").GetString());
        Assert.True(Math.Abs((c.GetProperty("callConsentAt").GetDateTimeOffset() - consent).TotalSeconds) < 1);
        Assert.Equal("+380671112233", c.GetProperty("phonePending").GetProperty("phone").GetString());
        Assert.Equal("manual_chat", c.GetProperty("phonePending").GetProperty("source").GetString());
        Assert.Equal(original, c.GetProperty("possibleDuplicate").GetProperty("id").GetGuid());
        Assert.Equal("Оригінал", c.GetProperty("possibleDuplicate").GetProperty("name").GetString());

        var np = (await J(await Call(e, HttpMethod.Get, $"/clients/{noPhone}", t.Owner))).GetProperty("client");
        Assert.False(np.GetProperty("hasPhone").GetBoolean());
        Assert.False(np.TryGetProperty("phoneSource", out _));
        Assert.False(np.TryGetProperty("phonePending", out _));
        Assert.False(np.TryGetProperty("possibleDuplicate", out _));

        // список не змінюється: нових полів немає
        var list = await J(await Call(e, HttpMethod.Get, "/clients", t.Owner));
        foreach (var item in list.EnumerateArray())
            foreach (var f in new[] { "hasPhone", "phoneSource", "callConsentAt", "phonePending", "possibleDuplicate" })
                Assert.False(item.TryGetProperty(f, out _));

        // видалений оригінал не віддається
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Clients.Where(x => x.Id == original).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));
        var after = (await J(await Call(e, HttpMethod.Get, $"/clients/{id}", t.Owner))).GetProperty("client");
        Assert.False(after.TryGetProperty("possibleDuplicate", out _));

        // specialist не бачить картку (Management)
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(e, HttpMethod.Get, $"/clients/{id}", t.Spec)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(e, HttpMethod.Get, "/clients", t.Spec)).StatusCode);
    }

    [SkippableFact]
    public async Task staff_created_client_gets_phone_source_staff_and_phone_exists_compares_by_digits()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var r = await Call(e, HttpMethod.Post, "/clients", t.Owner, new { fullName = "Нова", phone = "050 111 22 33" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await J(r)).GetProperty("id").GetGuid();
        var c = await ClientAsync(t.Id, id);
        Assert.Equal(PhoneSources.Staff, c.PhoneSource);
        Assert.Null(c.CallConsentAt);

        var dup = await Call(e, HttpMethod.Post, "/clients", t.Owner, new { fullName = "Друга", phone = "+380501112233" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("phone_exists", await Code(dup));

        var noPhone = await Call(e, HttpMethod.Post, "/clients", t.Owner, new { fullName = "Без" });
        Assert.Equal(HttpStatusCode.Created, noPhone.StatusCode);
        Assert.Null((await ClientAsync(t.Id, (await J(noPhone)).GetProperty("id").GetGuid())).PhoneSource);
    }

    // ---------- SavePhoneAsync ----------

    [SkippableFact]
    public async Task save_phone_into_empty_client_saves_with_source_and_consent()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", null);
        var conv = await AddConversationAsync(t.Id, id);
        var res = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "050 111 22 33", PhoneSources.ManualChat, consent: true, conv));
        Assert.Equal(PhoneSaveOutcome.Saved, res.Outcome);
        Assert.True(res.IsSuccess);
        var c = await ClientAsync(t.Id, id);
        Assert.Equal("+380501112233", c.Phone);
        Assert.Equal("manual_chat", c.PhoneSource);
        Assert.NotNull(c.CallConsentAt);
        Assert.Null(c.PhonePending);
        await using var ctx = fx.Db.CreateContext(t.Id);
        Assert.Null((await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == conv)).AwaitingStaffSince);
    }

    [SkippableFact]
    public async Task save_phone_without_consent_leaves_consent_empty_and_same_number_later_fills_it_once()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", null);
        Assert.Equal(PhoneSaveOutcome.Saved, (await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "+380501112233", PhoneSources.TelegramContact, false))).Outcome);
        Assert.Null((await ClientAsync(t.Id, id)).CallConsentAt);
        Assert.Equal(PhoneSaveOutcome.Unchanged, (await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "0501112233", PhoneSources.ManualChat, true))).Outcome);
        var first = (await ClientAsync(t.Id, id)).CallConsentAt;
        Assert.NotNull(first);
        await Task.Delay(20);
        Assert.Equal(PhoneSaveOutcome.Unchanged, (await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "+38 (050) 111-22-33", PhoneSources.ManualChat, true))).Outcome);
        var c = await ClientAsync(t.Id, id);
        Assert.Equal(first, c.CallConsentAt); // згода не зсувається
        Assert.Equal("telegram_contact", c.PhoneSource); // джерело первинного номера
    }

    [SkippableFact]
    public async Task save_phone_same_digits_as_legacy_unnormalized_number_is_unchanged_and_does_not_rewrite_it()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", "050-111-22-33", c => c.PhoneSource = null);
        var res = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "+380501112233", PhoneSources.TelegramContact, true));
        Assert.Equal(PhoneSaveOutcome.Unchanged, res.Outcome);
        var c = await ClientAsync(t.Id, id);
        Assert.Equal("050-111-22-33", c.Phone);
        Assert.Null(c.PhoneSource);
        Assert.NotNull(c.CallConsentAt);
        Assert.Null(c.PhonePending);
    }

    [SkippableFact]
    public async Task save_phone_different_number_goes_to_pending_never_overwrites_and_flags_conversation_for_staff()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", "+380671112233");
        var conv = await AddConversationAsync(t.Id, id);
        var res = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "0501112233", PhoneSources.TelegramContact, true, conv));
        Assert.Equal(PhoneSaveOutcome.PendingConflict, res.Outcome);
        var c = await ClientAsync(t.Id, id);
        Assert.Equal("+380671112233", c.Phone);
        Assert.Equal("web_form", c.PhoneSource);
        Assert.Equal("+380501112233", c.PhonePending);
        Assert.Equal("telegram_contact", c.PhonePendingSource);
        Assert.NotNull(c.PhonePendingAt);
        await using (var ctx = fx.Db.CreateContext(t.Id))
            Assert.NotNull((await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == conv)).AwaitingStaffSince);

        // другий інший номер замінює pending (наявний телефон усе ще не змінено)
        await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, "+380931112233", PhoneSources.ManualChat, true, conv));
        c = await ClientAsync(t.Id, id);
        Assert.Equal("+380671112233", c.Phone);
        Assert.Equal("+380931112233", c.PhonePending);
        Assert.Equal("manual_chat", c.PhonePendingSource);

        // і адміністратор його приймає
        var r = await Call(e, HttpMethod.Post, $"/clients/{id}/phone-pending", t.Owner, new { action = "accept" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("+380931112233", (await ClientAsync(t.Id, id)).Phone);
    }

    [SkippableFact]
    public async Task save_phone_rejects_invalid_number_source_foreign_client_foreign_conversation_and_wrong_tenant()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var other = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", null);
        var otherClient = await AddClientAsync(t.Id, "B", null);
        var foreignClient = await AddClientAsync(other.Id, "F", null);
        var deleted = await AddClientAsync(t.Id, "D", null, c => c.DeletedAt = DateTimeOffset.UtcNow);
        var convOfOther = await AddConversationAsync(t.Id, otherClient);
        var foreignConv = await AddConversationAsync(other.Id, foreignClient);

        Task<PhoneSaveResult> Save(Guid client, string? phone, string source = PhoneSources.ManualChat, Guid? conv = null) =>
            WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, client, phone, source, true, conv));

        Assert.Equal(PhoneSaveOutcome.InvalidPhone, (await Save(id, "12345")).Outcome);
        Assert.Equal(PhoneSaveOutcome.InvalidPhone, (await Save(id, "+38050111223")).Outcome);
        Assert.Equal(PhoneSaveOutcome.InvalidPhone, (await Save(id, null)).Outcome);
        Assert.Equal(PhoneSaveOutcome.InvalidSource, (await Save(id, "0501112233", "admin")).Outcome);
        Assert.Equal(PhoneSaveOutcome.InvalidSource, (await Save(id, "0501112233", PhoneSources.Staff)).Outcome);
        Assert.Equal(PhoneSaveOutcome.ClientNotFound, (await Save(foreignClient, "0501112233")).Outcome); // RLS: чужий клієнт невидимий
        Assert.Equal(PhoneSaveOutcome.ClientNotFound, (await Save(deleted, "0501112233")).Outcome);
        Assert.Equal(PhoneSaveOutcome.ClientNotFound, (await Save(Guid.NewGuid(), "0501112233")).Outcome);
        Assert.Equal(PhoneSaveOutcome.ConversationMismatch, (await Save(id, "0501112233", conv: convOfOther)).Outcome);
        Assert.Equal(PhoneSaveOutcome.ConversationMismatch, (await Save(id, "0501112233", conv: foreignConv)).Outcome);
        Assert.Equal(PhoneSaveOutcome.ConversationMismatch, (await Save(id, "0501112233", conv: Guid.NewGuid())).Outcome);
        Assert.Null((await ClientAsync(t.Id, id)).Phone);
        Assert.Null((await ClientAsync(other.Id, foreignClient)).Phone);

        // tenant-параметр мусить збігатися зі scope
        await Assert.ThrowsAnyAsync<Exception>(() => WithPhoneService(e, t.Id, s => s.SavePhoneAsync(other.Id, foreignClient, "0501112233", PhoneSources.ManualChat, true)));
        Assert.Null((await ClientAsync(other.Id, foreignClient)).Phone);
    }

    [SkippableFact]
    public async Task save_phone_parallel_different_numbers_save_one_and_queue_the_rest_as_pending_and_logs_have_no_numbers()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "A", null);
        var numbers = new[] { "+380501110001", "+380501110002", "+380501110003", "+380501110004", "+380501110005", "+380501110001" };
        var results = await Task.WhenAll(numbers.Select(n =>
            Task.Run(() => WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, id, n, PhoneSources.TelegramContact, true)))));
        Assert.Equal(1, results.Count(r => r.Outcome == PhoneSaveOutcome.Saved));
        Assert.All(results.Where(r => r.Outcome != PhoneSaveOutcome.Saved), r => Assert.True(r.Outcome is PhoneSaveOutcome.PendingConflict or PhoneSaveOutcome.Unchanged));
        var c = await ClientAsync(t.Id, id);
        Assert.NotNull(c.Phone);
        Assert.NotEqual(c.Phone, c.PhonePending);
        Assert.All(new[] { c.Phone, c.PhonePending }.Where(p => p is not null), p => Assert.Contains(p!, numbers));
        foreach (var n in numbers) Assert.DoesNotContain(n[3..], e.Logs.All);
    }

    // ---------- дублікати ----------

    [SkippableFact]
    public async Task save_phone_links_exactly_one_other_client_as_possible_duplicate_without_merging()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var web = await AddClientAsync(t.Id, "Web", "+380501112233");
        var chat = await AddClientAsync(t.Id, "Chat", null);
        var res = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, chat, "050 111 22 33", PhoneSources.TelegramContact, true));
        Assert.Equal(PhoneSaveOutcome.Saved, res.Outcome);
        Assert.True(res.DuplicateLinked);
        Assert.Equal(web, (await ClientAsync(t.Id, chat)).DuplicateOfClientId);
        Assert.Null((await ClientAsync(t.Id, web)).DuplicateOfClientId);

        // спадковий формат 0671112233 теж збігається за цифрами -> рівно один збіг -> зв'язок
        var web2 = await AddClientAsync(t.Id, "Web2", "0671112233");
        var chat2 = await AddClientAsync(t.Id, "Chat2", null);
        var res2 = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, chat2, "+380671112233", PhoneSources.ManualChat, true));
        Assert.Equal(PhoneSaveOutcome.Saved, res2.Outcome);
        Assert.True(res2.DuplicateLinked);
        Assert.Equal(web2, (await ClientAsync(t.Id, chat2)).DuplicateOfClientId);
    }

    [SkippableFact]
    public async Task duplicate_link_rules_one_match_links_two_matches_do_not_and_deleted_clients_are_ignored()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();

        var a = await AddClientAsync(t.Id, "A", "+380991110001");
        var b = await AddClientAsync(t.Id, "B", null);
        Assert.True((await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, b, "+380991110001", PhoneSources.ManualChat, true))).DuplicateLinked);
        Assert.Equal(a, (await ClientAsync(t.Id, b)).DuplicateOfClientId);

        var c = await AddClientAsync(t.Id, "C", null); // тепер двоє мають цей номер -> без зв'язку
        var res = await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, c, "0991110001", PhoneSources.ManualChat, true));
        Assert.Equal(PhoneSaveOutcome.Saved, res.Outcome);
        Assert.False(res.DuplicateLinked);
        Assert.Null((await ClientAsync(t.Id, c)).DuplicateOfClientId);

        var x = await AddClientAsync(t.Id, "X", "+380991110002", cl => cl.DeletedAt = DateTimeOffset.UtcNow);
        var y = await AddClientAsync(t.Id, "Y", null);
        Assert.False((await WithPhoneService(e, t.Id, s => s.SavePhoneAsync(t.Id, y, "+380991110002", PhoneSources.ManualChat, true))).DuplicateLinked);
        _ = x;
    }

    [SkippableFact]
    public async Task accept_recomputes_duplicate_link_without_creating_a_mutual_reference()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var original = await AddClientAsync(t.Id, "Orig", "+380503330000");
        var holder = await AddClientAsync(t.Id, "Holder", "+380674440000", c =>
        {
            c.PhonePending = "+380503330000"; c.PhonePendingSource = "manual_chat"; c.PhonePendingAt = DateTimeOffset.UtcNow;
        });
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Post, $"/clients/{holder}/phone-pending", t.Owner, new { action = "accept" })).StatusCode);
        Assert.Equal(original, (await ClientAsync(t.Id, holder)).DuplicateOfClientId);

        // оригінал отримує номер холдера як pending і приймає: зворотного посилання на того, хто вже вказує на нього, немає
        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Clients.Where(x => x.Id == original).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PhonePending, "+380503330000").SetProperty(x => x.PhonePendingSource, "manual_chat").SetProperty(x => x.PhonePendingAt, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await Call(e, HttpMethod.Post, $"/clients/{original}/phone-pending", t.Owner, new { action = "accept" })).StatusCode);
        Assert.Null((await ClientAsync(t.Id, original)).DuplicateOfClientId);
    }

    [SkippableFact]
    public async Task resolve_client_finds_legacy_formats_by_digits_and_prefers_the_original_then_the_oldest()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var newer = await AddClientAsync(t.Id, "Newer", "050 111 22 33");
        await Task.Delay(30);
        var older = await AddClientAsync(t.Id, "Later-created-dup", "+380501112233", c => c.DuplicateOfClientId = newer);
        await Task.Delay(30);
        var third = await AddClientAsync(t.Id, "Third", "380501112233");

        async Task<Guid?> Resolve(string phone)
        {
            using var scope = e.Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(t.Id);
            return await scope.ServiceProvider.GetRequiredService<IBookingStore>().ResolveClientAsync(new ClientInput(null, "Нова", phone, null), default);
        }

        // Newer - оригінал (без duplicate_of), найстарший серед оригіналів; Later-created-dup (має посилання) і Third (оригінал, новіший) не виграють
        foreach (var p in new[] { "+380501112233", "0501112233", "(050) 111-22-33", "380501112233" })
            Assert.Equal(newer, await Resolve(p));

        await using (var ctx = fx.Db.CreateContext(t.Id))
            await ctx.Clients.Where(x => x.Id == newer).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow));
        Assert.Equal(third, await Resolve("0501112233")); // оригінал (без посилання) переважає клієнта з duplicate_of
        _ = older;
    }

    [SkippableFact]
    public async Task resolve_client_creates_new_client_with_source_by_origin_and_never_changes_an_existing_client()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var existing = await AddClientAsync(t.Id, "Існує", "+380501112233", c => c.PhoneSource = PhoneSources.TelegramContact);

        async Task<Guid?> Resolve(string phone, string? source)
        {
            using var scope = e.Factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(t.Id);
            var store = scope.ServiceProvider.GetRequiredService<IBookingStore>();
            var id = await store.ResolveClientAsync(new ClientInput(null, "Нова", phone, null, false, source), default);
            // нового клієнта зберігає AddAppointmentAsync разом із записом; тут зберігаємо контекст напряму
            await scope.ServiceProvider.GetRequiredService<BeautyDbContext>().SaveChangesAsync();
            return id;
        }

        Assert.Equal(existing, await Resolve("050 111 22 33", PhoneSources.WebForm));
        Assert.Equal(PhoneSources.TelegramContact, (await ClientAsync(t.Id, existing)).PhoneSource); // наявний не змінено

        var web = (await Resolve("+380931110000", PhoneSources.WebForm))!.Value;
        var staff = (await Resolve("+380931110001", PhoneSources.Staff))!.Value;
        var unspecified = (await Resolve("+380931110002", null))!.Value;
        Assert.Equal(PhoneSources.WebForm, (await ClientAsync(t.Id, web)).PhoneSource);
        Assert.Equal(PhoneSources.Staff, (await ClientAsync(t.Id, staff)).PhoneSource);
        Assert.Equal(PhoneSources.Staff, (await ClientAsync(t.Id, unspecified)).PhoneSource);
        Assert.Null((await ClientAsync(t.Id, web)).CallConsentAt);
    }

    // ---------- cooldown ----------

    [SkippableFact]
    public async Task request_state_reflects_phone_presence_and_the_30_day_cooldown_and_mark_requested_starts_it()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var has = await AddClientAsync(t.Id, "H", "+380501112233");
        var none = await AddClientAsync(t.Id, "N", null);
        var recent = await AddClientAsync(t.Id, "R", null, c => c.PhoneRequestedAt = DateTimeOffset.UtcNow.AddDays(-10));
        var old = await AddClientAsync(t.Id, "O", null, c => c.PhoneRequestedAt = DateTimeOffset.UtcNow.AddDays(-31));

        Task<PhoneRequestState> State(Guid id) => WithPhoneService(e, t.Id, s => s.GetRequestStateAsync(t.Id, id));
        Assert.Equal(PhoneRequestState.HasPhone, await State(has));
        Assert.Equal(PhoneRequestState.Allowed, await State(none));
        Assert.Equal(PhoneRequestState.Cooldown, await State(recent));
        Assert.Equal(PhoneRequestState.Allowed, await State(old));
        Assert.Equal(PhoneRequestState.ClientNotFound, await State(Guid.NewGuid()));

        Assert.True(await WithPhoneService(e, t.Id, s => s.MarkPhoneRequestedAsync(t.Id, none)));
        Assert.Equal(PhoneRequestState.Cooldown, await State(none));
        Assert.NotNull((await ClientAsync(t.Id, none)).PhoneRequestedAt);
        Assert.False(await WithPhoneService(e, t.Id, s => s.MarkPhoneRequestedAsync(t.Id, Guid.NewGuid())));
    }

    // ---------- Inbox / «Дзвінки» ----------

    [SkippableFact]
    public async Task inbox_returns_only_masked_phone_and_pending_flag_while_call_tasks_return_full_pending_to_owner_and_admin()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        var id = await AddClientAsync(t.Id, "Анна", "+380501112233", c =>
        {
            c.PhonePending = "+380671119988"; c.PhonePendingSource = "telegram_contact"; c.PhonePendingAt = DateTimeOffset.UtcNow;
        });
        var plain = await AddClientAsync(t.Id, "Без номера", null);
        var conv = await AddConversationAsync(t.Id, id, awaiting: DateTimeOffset.UtcNow);
        var convPlain = await AddConversationAsync(t.Id, plain);
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            ctx.Add(new Message { ConversationId = conv, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Body = "привіт" });
            await ctx.SaveChangesAsync();
        }

        var listRes = await Call(e, HttpMethod.Get, "/conversations?filter=all", t.Admin);
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var raw = await listRes.Content.ReadAsStringAsync();
        foreach (var leak in new[] { "380501112233", "0501112233", "380671119988", "671119988" }) Assert.DoesNotContain(leak, raw);
        var items = JsonDocument.Parse(raw).RootElement.GetProperty("items").EnumerateArray().ToList();
        var main = items.Single(i => i.GetProperty("id").GetGuid() == conv).GetProperty("client");
        Assert.Equal("+••••••••••33", main.GetProperty("phoneMasked").GetString());
        Assert.True(main.GetProperty("phonePending").GetBoolean());
        var empty = items.Single(i => i.GetProperty("id").GetGuid() == convPlain).GetProperty("client");
        Assert.False(empty.TryGetProperty("phoneMasked", out _));
        Assert.False(empty.GetProperty("phonePending").GetBoolean());

        var detailRes = await Call(e, HttpMethod.Get, $"/conversations/{conv}", t.Owner);
        var detailRaw = await detailRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain("380501112233", detailRaw);
        Assert.DoesNotContain("380671119988", detailRaw);
        var dc = JsonDocument.Parse(detailRaw).RootElement.GetProperty("conversation").GetProperty("client");
        Assert.Equal("+••••••••••33", dc.GetProperty("phoneMasked").GetString());
        Assert.True(dc.GetProperty("phonePending").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(e, HttpMethod.Get, "/conversations?filter=all", t.Spec)).StatusCode);

        // call-tasks: повний phone і pending (лише owner/admin)
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            await ctx.Appointments.Where(a => a.Id == t.Beauty.AppointmentA).ExecuteUpdateAsync(s => s.SetProperty(a => a.ClientId, id));
            ctx.Add(new CallTask
            {
                AppointmentId = t.Beauty.AppointmentA, ClientId = id, LocationId = t.Beauty.LocationId, Reason = "reminder", OffsetMinutes = 1440,
                DueAt = DateTimeOffset.UtcNow, Status = "open",
            });
            await ctx.SaveChangesAsync();
        }
        foreach (var token in new[] { t.Owner, t.Admin })
        {
            var r = await Call(e, HttpMethod.Get, "/call-tasks?day=today&status=all", token);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            AssertNoStore(r);
            var task = (await J(r)).GetProperty("items").EnumerateArray().Single().GetProperty("client");
            Assert.Equal("+380501112233", task.GetProperty("phone").GetString());
            Assert.Equal("+380671119988", task.GetProperty("phonePending").GetString());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(e, HttpMethod.Get, "/call-tasks?day=today&status=all", t.Spec)).StatusCode);

        // номери не потрапили в логи жодного з викликів
        foreach (var leak in new[] { "380501112233", "380671119988", "0501112233", "671119988" }) Assert.DoesNotContain(leak, e.Logs.All);
    }

    [SkippableFact]
    public async Task call_task_client_without_pending_omits_phone_pending()
    {
        NeedDb();
        var e = NewEnv();
        var t = await NewTenantAsync();
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            ctx.Add(new CallTask
            {
                AppointmentId = t.Beauty.AppointmentA, ClientId = t.Beauty.ClientId, LocationId = t.Beauty.LocationId, Reason = "reminder", OffsetMinutes = 1440,
                DueAt = DateTimeOffset.UtcNow, Status = "open",
            });
            await ctx.SaveChangesAsync();
        }
        var r = await Call(e, HttpMethod.Get, "/call-tasks?day=today&status=all", t.Owner);
        var client = (await J(r)).GetProperty("items").EnumerateArray().Single().GetProperty("client");
        Assert.False(client.TryGetProperty("phonePending", out _));
        Assert.Equal("+380000000000", client.GetProperty("phone").GetString());
    }
}
