using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Api.Tenancy;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Marketing;

/// <summary>
/// TASK-777 (§26.4, §26.11.2, §26.14 сц. 8-9, ADR-015): сервіс і API маркетингової згоди. Реальний PostgreSQL + RLS:
/// рівні й append-only історія, «Стоп», правило адміна, ігнор публічного поля, tenant, ролі/IDOR, no-store, відсутність ПД у логах, гонки.
/// </summary>
public sealed class MarketingConsentTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private const string ClientName = "Оксана Приватна";
    private const string Phone = "+380671234567";
    private const string SecretNote = "секретна-підстава-згоди-клієнтки";
    private const string V1 = "mc-v1";
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed class CapturingProvider : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }

    private sealed record Env(HttpClient Client, CapturingProvider Logs);
    private sealed record Tn(TenantSeed Seed, string Owner, string Admin, string Spec, AuthApiFixture.BeautySeed B)
    {
        public Guid Id => Seed.TenantId;
        public Guid ClientId => B.ClientId;
    }

    private Env NewEnv(bool adminGrant = false)
    {
        var logs = new CapturingProvider();
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (adminGrant) b.UseSetting("Marketing:AdminGrantEnabled", "true");
            b.ConfigureTestServices(s => s.AddLogging(l => l.SetMinimumLevel(LogLevel.Information).AddProvider(logs)));
        });
        _disposables.Add(f);
        return new Env(f.CreateClient(), logs);
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync();
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var c = await ctx.Clients.SingleAsync(x => x.Id == seed.ClientId);
            c.FullName = ClientName;
            c.Phone = Phone;
            await ctx.SaveChangesAsync();
        }
        return new Tn(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, seed);
    }

    private static HttpRequestMessage Req(HttpMethod m, string url, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);
    private static async Task<string> Code(HttpResponseMessage r) => (await J(r)).GetProperty("code").GetString()!;
    private static string Url(Guid clientId) => $"/api/beauty/clients/{clientId}/marketing-consent";

    private sealed class Svc(BeautyDbContext db, MarketingConsentService service) : IAsyncDisposable
    {
        public BeautyDbContext Db { get; } = db;
        public MarketingConsentService S { get; } = service;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>Сервіс поверх контексту app-ролі з RLS tenant (як у воркері/конвеєрі).</summary>
    private Svc NewSvc(Guid tenantId, bool adminGrant = false, ILogger<MarketingConsentService>? log = null)
    {
        var tenant = new TenantContext(tenantId);
        var options = new DbContextOptionsBuilder<BeautyDbContext>();
        options.UseBeautyNpgsql(fx.Db.AppConnectionString).AddInterceptors(new TenantConnectionInterceptor(tenant));
        var db = new BeautyDbContext(options.Options, tenant);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Marketing:AdminGrantEnabled"] = adminGrant ? "true" : "false" }).Build();
        return new Svc(db, new MarketingConsentService(db, tenant, config, TimeProvider.System, log ?? NullLogger<MarketingConsentService>.Instance));
    }

    private static SetConsentCommand Chat(Guid clientId, string level, string? version = V1, string channel = "telegram") =>
        new(clientId, channel, level, "chat", "button", level == "none" ? null : version);

    private async Task<Guid> NewClientAsync(Guid tenantId, bool unsubscribed = false)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        var c = new Client { FullName = "Клієнт " + Guid.NewGuid().ToString("N")[..6], Unsubscribed = unsubscribed };
        ctx.Add(c);
        await ctx.SaveChangesAsync();
        return c.Id;
    }

    private async Task<(Client Client, List<MarketingConsent> Rows, List<MarketingConsentEvent> Events)> StateAsync(Guid tenantId, Guid clientId)
    {
        await using var ctx = fx.Db.CreateContext(tenantId);
        return (await ctx.Clients.AsNoTracking().SingleAsync(c => c.Id == clientId),
            await ctx.MarketingConsents.AsNoTracking().Where(m => m.ClientId == clientId).ToListAsync(),
            (await ctx.MarketingConsentEvents.AsNoTracking().Where(m => m.ClientId == clientId).ToListAsync()).OrderBy(e => e.CreatedAt).ToList());
    }

    // ------------------------------------------------------------------ рівні, історія, похідний прапорець

    [SkippableFact]
    public async Task setConsent_levels_write_state_and_append_only_events_and_sync_the_derived_flag()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var s = NewSvc(t.Id);

        var full = await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default);
        Assert.True(full.IsOk);
        Assert.Equal(new ConsentChange(true, null, "full"), full.Value);
        var st = await StateAsync(t.Id, t.ClientId);
        Assert.True(st.Client.MarketingConsent);
        var row = Assert.Single(st.Rows);
        Assert.Equal(("telegram", "full", "chat", V1), (row.Channel, row.Level, row.Source, row.TextVersion));
        Assert.Null(row.UpdatedByUserId);

        Assert.True((await s.S.SetConsentAsync(Chat(t.ClientId, "selective"), default)).Value!.Changed);
        Assert.True((await StateAsync(t.Id, t.ClientId)).Client.MarketingConsent); // selective теж дає прапорець
        Assert.True((await s.S.SetConsentAsync(Chat(t.ClientId, "none"), default)).Value!.Changed);

        st = await StateAsync(t.Id, t.ClientId);
        Assert.False(st.Client.MarketingConsent);
        Assert.Equal("none", Assert.Single(st.Rows).Level);
        Assert.Equal([(null, "full"), ("full", "selective"), ("selective", "none")], st.Events.Select(e => (e.LevelFrom, e.LevelTo)).ToArray());
        Assert.All(st.Events, e => Assert.Equal(("telegram", "chat", "button"), (e.Channel, e.Source, e.Via)));
        Assert.Equal([V1, V1, null], st.Events.Select(e => e.TextVersion).ToArray());
    }

    [SkippableFact]
    public async Task setConsent_same_level_is_idempotent_no_event()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var s = NewSvc(t.Id);
        Assert.True((await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default)).Value!.Changed);
        var again = await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default);
        Assert.False(again.Value!.Changed);
        Assert.Single((await StateAsync(t.Id, t.ClientId)).Events);
    }

    [SkippableFact]
    public async Task chat_consent_requires_text_version_and_valid_inputs_and_unknown_client_is_404()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var s = NewSvc(t.Id);
        Assert.Equal("text_version_required", (await s.S.SetConsentAsync(Chat(t.ClientId, "full", version: null), default)).Error!.Code);
        Assert.Equal("invalid_level", (await s.S.SetConsentAsync(Chat(t.ClientId, "everything"), default)).Error!.Code);
        Assert.Equal("channel_unsupported", (await s.S.SetConsentAsync(Chat(t.ClientId, "full", channel: "sms"), default)).Error!.Code);
        Assert.Equal("client_not_found", (await s.S.SetConsentAsync(Chat(Guid.NewGuid(), "full"), default)).Error!.Code);
        Assert.Empty((await StateAsync(t.Id, t.ClientId)).Events);
        // видалений клієнт - так само 404
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            var c = await ctx.Clients.SingleAsync(x => x.Id == t.ClientId);
            c.DeletedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }
        Assert.Equal("client_not_found", (await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default)).Error!.Code);
    }

    [SkippableFact]
    public async Task explicit_chat_raise_resets_unsubscribed_but_lowering_and_admin_do_not()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var cid = await NewClientAsync(t.Id, unsubscribed: true);
        await using var s = NewSvc(t.Id);
        Assert.True((await s.S.SetConsentAsync(Chat(cid, "none"), default)).Value!.Changed); // none -> none з рядка немає: запис для історії
        Assert.True((await StateAsync(t.Id, cid)).Client.Unsubscribed);
        Assert.True((await s.S.SetConsentAsync(Chat(cid, "selective"), default)).IsOk);
        var st = await StateAsync(t.Id, cid);
        Assert.False(st.Client.Unsubscribed); // остання явна воля клієнта
        Assert.True(st.Client.MarketingConsent);
    }

    // ------------------------------------------------------------------ «Стоп»

    [SkippableFact]
    public async Task stop_over_marketing_sets_none_on_all_channels_and_unsubscribes_idempotently()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var s = NewSvc(t.Id);
        await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default);
        await s.S.SetConsentAsync(Chat(t.ClientId, "selective", channel: "push"), default);

        var stop = await s.S.StopAsync(t.ClientId, "command", ct: default);
        Assert.True(stop.IsOk);
        Assert.Equal(2, stop.Value!.Count(c => c.Changed));
        var st = await StateAsync(t.Id, t.ClientId);
        Assert.False(st.Client.MarketingConsent);
        Assert.True(st.Client.Unsubscribed);
        Assert.All(st.Rows, r => Assert.Equal(("none", "stop"), (r.Level, r.Source)));
        Assert.Equal(2, st.Rows.Count);
        Assert.Equal(2, st.Events.Count(e => e.Source == "stop" && e.LevelTo == "none" && e.Via == "command" && e.LevelFrom != null));

        var again = await s.S.StopAsync(t.ClientId, "command", ct: default); // повторний «Стоп»: нових подій немає
        Assert.All(again.Value!, c => Assert.False(c.Changed));
        Assert.Equal(4, (await StateAsync(t.Id, t.ClientId)).Events.Count);
    }

    [SkippableFact]
    public async Task stop_without_any_row_writes_none_for_history_and_then_chat_raise_restores_marketing_only()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var cid = await NewClientAsync(t.Id);
        await using var s = NewSvc(t.Id);
        Assert.True((await s.S.StopAsync(cid, "text", ct: default)).IsOk);
        var st = await StateAsync(t.Id, cid);
        var ev = Assert.Single(st.Events);
        Assert.Equal((null, "none", "stop", "text"), (ev.LevelFrom, ev.LevelTo, ev.Source, ev.Via));
        Assert.True(st.Client.Unsubscribed);
        Assert.Equal("telegram", Assert.Single(st.Rows).Channel);

        Assert.True((await s.S.SetConsentAsync(Chat(cid, "full"), default)).IsOk); // після «Стоп» клієнт може явно повернутись
        st = await StateAsync(t.Id, cid);
        Assert.True(st.Client.MarketingConsent);
        Assert.False(st.Client.Unsubscribed);
    }

    // ------------------------------------------------------------------ правило адміна (сервіс)

    [SkippableFact]
    public async Task admin_lowering_always_ok_raising_needs_basis_then_flag()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var off = NewSvc(t.Id, adminGrant: false);
        await off.S.SetConsentAsync(Chat(t.ClientId, "full"), default);
        SetConsentCommand Admin(string level, string? basis = null, string? note = null) =>
            new(t.ClientId, "telegram", level, "admin", "api", ActorUserId: t.Seed.OwnerId, Basis: basis, BasisNote: note);

        Assert.True((await off.S.SetConsentAsync(Admin("selective"), default)).IsOk); // full -> selective без підстави
        var row = Assert.Single((await StateAsync(t.Id, t.ClientId)).Rows);
        Assert.Equal(("selective", "admin", t.Seed.OwnerId), (row.Level, row.Source, row.UpdatedByUserId));

        Assert.Equal("basis_required", (await off.S.SetConsentAsync(Admin("full"), default)).Error!.Code);
        var disabled = await off.S.SetConsentAsync(Admin("full", "written", "підписаний бланк"), default);
        Assert.Equal(("admin_grant_disabled", ErrorKindName(disabled.Error!)), ("admin_grant_disabled", "Conflict"));
        Assert.Equal("selective", Assert.Single((await StateAsync(t.Id, t.ClientId)).Rows).Level); // нічого не змінилось

        Assert.True((await off.S.SetConsentAsync(Admin("none"), default)).IsOk); // відкликання - завжди
        Assert.Equal("admin_grant_disabled", (await off.S.SetConsentAsync(Admin("selective", "in_person", "усно"), default)).Error!.Code);

        await using var on = NewSvc(t.Id, adminGrant: true);
        Assert.Equal("invalid_basis", (await on.S.SetConsentAsync(Admin("full", "phone", "x"), default)).Error!.Code);
        Assert.Equal("invalid_basis_note", (await on.S.SetConsentAsync(Admin("full", "written", "  "), default)).Error!.Code);
        Assert.Equal("invalid_basis_note", (await on.S.SetConsentAsync(Admin("full", "written", new string('я', 201)), default)).Error!.Code);
        Assert.True((await on.S.SetConsentAsync(Admin("full", "written", "підписаний бланк"), default)).IsOk);
        var st = await StateAsync(t.Id, t.ClientId);
        var last = st.Events.Last();
        Assert.Equal(("none", "full", "admin", "written", "підписаний бланк", t.Seed.OwnerId), (last.LevelFrom, last.LevelTo, last.Source, last.Basis, last.BasisNote, last.ActorUserId));
        Assert.True(st.Client.MarketingConsent);
        Assert.False(st.Client.Unsubscribed); // admin не змінює unsubscribed
    }

    private static string ErrorKindName(BeautyCrm.Application.Features.BeautyCommon.Error e) => e.Kind.ToString();

    // ------------------------------------------------------------------ tenant / RLS

    [SkippableFact]
    public async Task tenant_isolation_foreign_client_is_not_found_and_rows_are_invisible_to_other_tenant()
    {
        NeedDb();
        var a = await NewTenantAsync(); var b = await NewTenantAsync();
        await using var sa = NewSvc(a.Id);
        await using var sb = NewSvc(b.Id);
        await sa.S.SetConsentAsync(Chat(a.ClientId, "full"), default);

        Assert.Equal("client_not_found", (await sb.S.SetConsentAsync(Chat(a.ClientId, "none"), default)).Error!.Code);
        Assert.Equal("client_not_found", (await sb.S.StopAsync(a.ClientId, "command", ct: default)).Error!.Code);
        Assert.Equal("client_not_found", (await sb.S.GetConsentAsync(a.ClientId, true, default)).Error!.Code);
        Assert.False(await sb.S.MarkOfferedAsync(a.ClientId, default));
        var foreign = await StateAsync(a.Id, a.ClientId);
        Assert.Equal("full", Assert.Single(foreign.Rows).Level);
        Assert.Single(foreign.Events);
        Assert.Null(foreign.Client.MarketingOfferedAt);
        await using var ctxB = fx.Db.CreateContext(b.Id);
        Assert.Empty(await ctxB.MarketingConsents.ToListAsync());
        Assert.Empty(await ctxB.MarketingConsentEvents.ToListAsync());
    }

    [SkippableFact]
    public async Task markOffered_sets_timestamp_and_unknown_or_deleted_returns_false()
    {
        NeedDb();
        var t = await NewTenantAsync();
        await using var s = NewSvc(t.Id);
        Assert.True(await s.S.MarkOfferedAsync(t.ClientId, default));
        Assert.NotNull((await StateAsync(t.Id, t.ClientId)).Client.MarketingOfferedAt);
        Assert.False(await s.S.MarkOfferedAsync(Guid.NewGuid(), default));
        var dto = (await s.S.GetConsentAsync(t.ClientId, true, default)).Value!;
        Assert.NotNull(dto.OfferedAt);
    }

    // ------------------------------------------------------------------ гонка

    [SkippableFact]
    public async Task concurrent_changes_are_serialized_by_the_client_advisory_lock_and_form_a_consistent_chain()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var levels = new[] { "full", "selective", "none", "full", "selective", "none", "selective", "full" };
        var results = await Task.WhenAll(levels.Select(async level =>
        {
            await using var s = NewSvc(t.Id);
            return await s.S.SetConsentAsync(Chat(t.ClientId, level), default);
        }));
        Assert.All(results, r => Assert.True(r.IsOk, r.Error?.Code));

        var st = await StateAsync(t.Id, t.ClientId);
        var row = Assert.Single(st.Rows); // жодного порушення PK при першому записі
        Assert.Single(st.Events, e => e.LevelFrom is null);
        for (var i = 1; i < st.Events.Count; i++) Assert.Equal(st.Events[i - 1].LevelTo, st.Events[i].LevelFrom);
        Assert.Equal(st.Events[^1].LevelTo, row.Level);
        Assert.Equal(row.Level != "none", st.Client.MarketingConsent);
    }

    [SkippableFact]
    public async Task concurrent_stop_and_chat_raise_end_in_a_consistent_state()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var cid = await NewClientAsync(t.Id);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(async i =>
        {
            await using var s = NewSvc(t.Id);
            if (i % 2 == 0) Assert.True((await s.S.StopAsync(cid, "text", ct: default)).IsOk);
            else Assert.True((await s.S.SetConsentAsync(Chat(cid, "full"), default)).IsOk);
        }));
        var st = await StateAsync(t.Id, cid);
        var row = Assert.Single(st.Rows);
        for (var i = 1; i < st.Events.Count; i++) Assert.Equal(st.Events[i - 1].LevelTo, st.Events[i].LevelFrom);
        Assert.Equal(row.Level, st.Events[^1].LevelTo);
        Assert.Equal(row.Level != "none", st.Client.MarketingConsent);
    }

    // ------------------------------------------------------------------ API: ролі, no-store, IDOR, модуль

    [SkippableFact]
    public async Task api_anonymous_401_specialist_403_forbidden_role_admin_and_owner_ok_with_no_store()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var calls = new (HttpMethod M, object? Body)[]
        {
            (HttpMethod.Get, null),
            (HttpMethod.Put, new { channel = "telegram", level = "none" }),
        };
        foreach (var (m, body) in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await e.Client.SendAsync(Req(m, Url(t.ClientId), null, body))).StatusCode);
            var spec = await e.Client.SendAsync(Req(m, Url(t.ClientId), t.Spec, body));
            Assert.Equal(HttpStatusCode.Forbidden, spec.StatusCode);
            Assert.Equal("forbidden_role", await Code(spec));
            Assert.Equal("no-store", spec.Headers.CacheControl?.ToString());
            foreach (var token in new[] { t.Admin, t.Owner })
            {
                var ok = await e.Client.SendAsync(Req(m, Url(t.ClientId), token, body));
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
                Assert.Equal("no-store", ok.Headers.CacheControl?.ToString());
            }
        }
        // specialist зі зламаним тілом - теж 403 (не 400/422)
        var broken = Req(HttpMethod.Put, Url(t.ClientId), t.Spec);
        broken.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await e.Client.SendAsync(broken)).StatusCode);
        Assert.Single(typeof(BeautyCrm.Api.Controllers.MarketingConsentController).GetMethod("Put")!
            .GetCustomAttributes(typeof(RequestSizeLimitAttribute), false));
    }

    [SkippableFact]
    public async Task api_idor_foreign_and_deleted_client_404_and_disabled_clients_module_403()
    {
        NeedDb();
        var e = NewEnv(); var a = await NewTenantAsync(); var b = await NewTenantAsync();
        var get = await e.Client.SendAsync(Req(HttpMethod.Get, Url(b.ClientId), a.Owner));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("client_not_found", await Code(get));
        Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
        var put = await e.Client.SendAsync(Req(HttpMethod.Put, Url(b.ClientId), a.Owner, new { channel = "telegram", level = "none" }));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal((await StateAsync(b.Id, b.ClientId)).Events.Count, 0);
        Assert.Equal(HttpStatusCode.NotFound, (await e.Client.SendAsync(Req(HttpMethod.Get, Url(Guid.NewGuid()), a.Owner))).StatusCode);

        var disabled = await fx.CreateTenantAsync(["beauty_booking"]);
        var owner = await fx.LoginAsync(disabled.Slug, disabled.OwnerEmail);
        var res = await e.Client.SendAsync(Req(HttpMethod.Get, Url(Guid.NewGuid()), owner.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("module_disabled", await Code(res));
    }

    [SkippableFact]
    public async Task api_get_unset_then_chat_history_and_basis_note_visible_only_to_owner()
    {
        NeedDb();
        var e = NewEnv(adminGrant: true); var t = await NewTenantAsync();
        var unset = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Url(t.ClientId), t.Admin)));
        var ch = unset.GetProperty("channels")[0];
        Assert.Equal(("telegram", "none", "unset"), (ch.GetProperty("channel").GetString(), ch.GetProperty("level").GetString(), ch.GetProperty("state").GetString()));
        Assert.False(ch.TryGetProperty("source", out _));
        Assert.False(unset.TryGetProperty("offeredAt", out _));
        Assert.False(unset.GetProperty("telegramAvailable").GetBoolean());
        Assert.Equal(0, unset.GetProperty("history").GetArrayLength());

        await using (var s = NewSvc(t.Id)) await s.S.SetConsentAsync(Chat(t.ClientId, "selective"), default);
        var lowerNone = await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, new { channel = "telegram", level = "none" }));
        Assert.Equal(HttpStatusCode.OK, lowerNone.StatusCode);
        var raise = await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, new { channel = "telegram", level = "full", basis = "written", basisNote = SecretNote }));
        Assert.Equal(HttpStatusCode.OK, raise.StatusCode);
        var asAdmin = await J(raise);
        var asOwner = await J(await e.Client.SendAsync(Req(HttpMethod.Get, Url(t.ClientId), t.Owner)));

        Assert.Equal("full", asAdmin.GetProperty("channels")[0].GetProperty("level").GetString());
        Assert.Equal("admin", asAdmin.GetProperty("channels")[0].GetProperty("source").GetString());
        Assert.False(string.IsNullOrEmpty(asAdmin.GetProperty("channels")[0].GetProperty("updatedBy").GetProperty("name").GetString()));
        var hist = asAdmin.GetProperty("history");
        Assert.Equal(3, hist.GetArrayLength());
        Assert.Equal("full", hist[0].GetProperty("levelTo").GetString()); // нові першими
        Assert.Equal("written", hist[0].GetProperty("basis").GetString());
        Assert.False(hist[0].TryGetProperty("basisNote", out _)); // admin не бачить нотатки
        Assert.Equal(SecretNote, asOwner.GetProperty("history")[0].GetProperty("basisNote").GetString());
        Assert.Equal("chat", hist[2].GetProperty("source").GetString());
        Assert.Equal("button", hist[2].GetProperty("via").GetString());
    }

    [SkippableFact]
    public async Task api_put_error_codes_and_admin_grant_default_off_is_409()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        async Task Expect(object body, HttpStatusCode status, string code)
        {
            var r = await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, body));
            Assert.Equal(status, r.StatusCode);
            Assert.Equal(code, await Code(r));
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        }
        await Expect(new { channel = "push", level = "full" }, HttpStatusCode.UnprocessableEntity, "channel_unsupported");
        await Expect(new { level = "full" }, HttpStatusCode.UnprocessableEntity, "channel_unsupported");
        await Expect(new { channel = "telegram", level = "all" }, HttpStatusCode.UnprocessableEntity, "invalid_level");
        await Expect(new { channel = "telegram" }, HttpStatusCode.UnprocessableEntity, "invalid_level");
        await Expect(new { channel = "telegram", level = "full" }, HttpStatusCode.UnprocessableEntity, "basis_required");
        await Expect(new { channel = "telegram", level = "full", basis = "email", basisNote = "x" }, HttpStatusCode.UnprocessableEntity, "invalid_basis");
        await Expect(new { channel = "telegram", level = "full", basis = "other" }, HttpStatusCode.UnprocessableEntity, "invalid_basis_note");
        await Expect(new { channel = "telegram", level = "full", basis = "written", basisNote = "бланк" }, HttpStatusCode.Conflict, "admin_grant_disabled");
        Assert.Empty((await StateAsync(t.Id, t.ClientId)).Events);
        // none для клієнта без рядка: запис «відкликано» для історії, рівень не підвищується -> дозволено
        var none = await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, new { channel = "telegram", level = "none" }));
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal("none", (await StateAsync(t.Id, t.ClientId)).Rows.Single().Level);
    }

    [SkippableFact]
    public async Task client_card_exposes_marketing_level_and_staff_create_or_public_form_cannot_set_consent()
    {
        NeedDb();
        var e = NewEnv(); var t = await NewTenantAsync();
        var card0 = await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"/api/beauty/clients/{t.ClientId}", t.Admin)));
        Assert.Equal("none", card0.GetProperty("client").GetProperty("marketing").GetProperty("level").GetString());
        await using (var s = NewSvc(t.Id)) await s.S.SetConsentAsync(Chat(t.ClientId, "full"), default);
        var card = (await J(await e.Client.SendAsync(Req(HttpMethod.Get, $"/api/beauty/clients/{t.ClientId}", t.Admin)))).GetProperty("client");
        Assert.Equal("full", card.GetProperty("marketing").GetProperty("level").GetString());
        Assert.True(card.GetProperty("marketing").TryGetProperty("updatedAt", out _));
        Assert.True(card.GetProperty("marketingConsent").GetBoolean());

        var created = await e.Client.SendAsync(Req(HttpMethod.Post, "/api/beauty/clients", t.Admin, new { fullName = "Нова", marketingConsent = true }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await J(created)).GetProperty("id").GetGuid();
        Assert.False((await StateAsync(t.Id, id)).Client.MarketingConsent); // згода зі staff-форми не береться
        Assert.Empty((await StateAsync(t.Id, id)).Rows);
    }

    // ------------------------------------------------------------------ ПД у логах і аудиті

    [SkippableFact]
    public async Task logs_carry_ids_and_codes_only_never_name_phone_or_basis_note()
    {
        NeedDb();
        var e = NewEnv(adminGrant: true); var t = await NewTenantAsync();
        await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, new { channel = "telegram", level = "full", basis = "written", basisNote = SecretNote }));
        await e.Client.SendAsync(Req(HttpMethod.Put, Url(t.ClientId), t.Admin, new { channel = "telegram", level = "none" }));
        await e.Client.SendAsync(Req(HttpMethod.Get, Url(t.ClientId), t.Owner));

        var lines = e.Logs.Lines.ToArray();
        var changed = lines.Where(l => l.Contains("marketing_consent_changed")).ToArray();
        Assert.Equal(2, changed.Length);
        Assert.Contains(changed, l => l.Contains($"client={t.ClientId}") && l.Contains("from=-") && l.Contains("to=full") && l.Contains("source=admin") && l.Contains("via=api"));
        Assert.DoesNotContain(lines, l => l.Contains(SecretNote) || l.Contains("секретна") || l.Contains(ClientName) || l.Contains("Оксана") || l.Contains(Phone) || l.Contains("+38067"));
    }

    // ------------------------------------------------------------------ прапорець Marketing:Enabled і модуль beauty_marketing

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void requireMarketingEnabled_blocks_with_403_marketing_disabled_unless_flag_is_true(string? value, bool allowed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { ["Marketing:Enabled"] = value }).Build();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(config).BuildServiceProvider() };
        var ctx = new ActionExecutingContext(new ActionContext(http, new RouteData(), new ActionDescriptor()), [], new Dictionary<string, object?>(), new object());

        new RequireMarketingEnabledAttribute().OnActionExecuting(ctx);

        if (allowed) { Assert.Null(ctx.Result); return; }
        var res = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(403, res.StatusCode);
        Assert.Equal("marketing_disabled", ((BeautyCrm.Api.Infrastructure.ApiError)res.Value!).Code);
    }

    [SkippableFact]
    public async Task marketing_module_is_assignable_but_not_in_defaults()
    {
        NeedDb();
        Assert.DoesNotContain(BeautyModules.Marketing, BeautyModules.All);
        Assert.Contains(BeautyModules.Marketing, BeautyModules.Known);
        var t = await fx.CreateTenantAsync(["beauty_clients", BeautyModules.Marketing]); // UpdateTenant/CreateTenant приймають beauty_marketing
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
    }
}
