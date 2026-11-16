using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-853 (ADR-023, §30.6.6-30.6.11, §30.13): група B на реальному API + PostgreSQL/RLS - клієнти (scope own, IDOR 404 на всіх
/// /clients/{id}/**), маскування контактів, ШІ (mode=auto лише owner, право tool), маркетинг, нагадування, inbox.
/// </summary>
public sealed class AccessGroupBTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private async Task<(HttpResponseMessage R, JsonElement B)> Call(HttpMethod m, string url, string token, object? body = null)
    {
        var r = await fx.Send(m, url, body, token);
        var text = await r.Content.ReadAsStringAsync();
        return (r, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string? Code(JsonElement e) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Dictionary<string, string> Perms(string role, params (string Key, string Level)[] over)
    {
        var d = new Dictionary<string, string>(PermissionPresets.For(role));
        foreach (var (k, l) in over) d[k] = l;
        return d;
    }

    private sealed record World(TenantSeed T, BeautySeed S, string Owner, Guid ForeignClient);

    /// <summary>Клієнт з seed має записи в обох майстрів; <c>ForeignClient</c> - лише у майстра B.</summary>
    private async Task<World> WorldAsync()
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var s = await fx.SeedBeautyAsync(t.TenantId);
        Guid foreign;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var c = new Client { FullName = "Foreign Client", Phone = "+380671112233", Email = "foreign@example.com" };
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            ctx.Add(new Appointment
            {
                LocationId = s.LocationId, SpecialistId = s.SpecialistB, ServiceId = s.ServiceId, ClientId = c.Id,
                StartsAt = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(3).AddHours(10), TimeSpan.Zero), DurationMinutes = 60,
                Status = AppointmentStatus.Confirmed, Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m,
            });
            await ctx.SaveChangesAsync();
            foreign = c.Id;
        }
        return new World(t, s, (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken, foreign);
    }

    private async Task<string> TokenAsync(World w, string role, Guid? specialist, params (string, string)[] over) =>
        (await fx.CreateUserAsync(w.T, role, specialist, Perms(role, over))).Tokens.AccessToken;

    // ---------- клієнти: IDOR own ----------

    [SkippableFact]
    public async Task Own_clients_scope_hides_foreign_client_with_404_on_every_client_subroute()
    {
        NeedDb();
        var w = await WorldAsync();
        var token = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA,
            (PermissionKeys.ClientsView, "own"), (PermissionKeys.ClientsEdit, "own"), (PermissionKeys.ClientsContacts, "full"));

        var (lr, list) = await Call(HttpMethod.Get, "/api/beauty/clients", token);
        Assert.Equal(HttpStatusCode.OK, lr.StatusCode);
        Assert.Equal([w.S.ClientId.ToString()], list.EnumerateArray().Select(x => x.GetProperty("id").GetString()));

        var own = $"/api/beauty/clients/{w.S.ClientId}";
        var foreign = $"/api/beauty/clients/{w.ForeignClient}";
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, own, token)).R.StatusCode);
        // історія own - лише записи власного профілю
        var detail = (await Call(HttpMethod.Get, own, token)).B;
        Assert.Equal([w.S.AppointmentA.ToString()], detail.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("id").GetString()));

        foreach (var (m, suffix, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, "", null),
                     (HttpMethod.Post, "/notes", new { body = "x" }),
                     (HttpMethod.Get, "/contact-preferences", null),
                     (HttpMethod.Put, "/contact-preferences", new { method = "telegram", offsetsMinutes = new[] { 60 } }),
                     (HttpMethod.Get, "/marketing-consent", null),
                     (HttpMethod.Put, "/marketing-consent", new { channel = "telegram", level = "none" }),
                     (HttpMethod.Post, "/phone-pending", new { action = "reject" }),
                     (HttpMethod.Get, "/coupons", null),
                 })
        {
            var (r, b) = await Call(m, foreign + suffix, token, body);
            Assert.True(r.StatusCode == HttpStatusCode.NotFound, $"{m} {suffix}: {(int)r.StatusCode}");
            Assert.Equal("client_not_found", Code(b));
        }
    }

    [SkippableFact]
    public async Task Clients_view_none_is_forbidden_and_edit_all_is_required_to_create()
    {
        NeedDb();
        var w = await WorldAsync();
        var none = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.ClientsView, "none"), (PermissionKeys.ClientsEdit, "none"));
        var (r, b) = await Call(HttpMethod.Get, "/api/beauty/clients", none);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("forbidden_role", Code(b));
        Assert.Equal(PermissionKeys.ClientsView, b.GetProperty("permission").GetString());

        var ownOnly = await TokenAsync(w, Roles.Specialist, w.S.SpecialistB, (PermissionKeys.ClientsView, "own"), (PermissionKeys.ClientsEdit, "own"));
        var (cr, cb) = await Call(HttpMethod.Post, "/api/beauty/clients", ownOnly, new { fullName = "New" });
        Assert.Equal(HttpStatusCode.Forbidden, cr.StatusCode);
        Assert.Equal(PermissionKeys.ClientsEdit, cb.GetProperty("permission").GetString());
        Assert.Equal("all", cb.GetProperty("required").GetString());

        // own: власний клієнт - нотатка проходить
        Assert.Equal(HttpStatusCode.Created, (await Call(HttpMethod.Post, $"/api/beauty/clients/{w.S.ClientId}/notes", ownOnly, new { body = "ok" })).R.StatusCode);
    }

    // ---------- маскування контактів: таблиця DTO x clients.contacts ----------

    [SkippableFact]
    public async Task Contacts_are_masked_in_every_client_dto_unless_contacts_full()
    {
        NeedDb();
        var w = await WorldAsync();
        var masked = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.ClientsContacts, "masked"));
        var full = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.ClientsContacts, "full"));

        // список
        var mList = (await Call(HttpMethod.Get, "/api/beauty/clients", masked)).B.EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == w.ForeignClient.ToString());
        Assert.False(mList.TryGetProperty("phone", out _));
        Assert.False(mList.TryGetProperty("email", out _));
        Assert.Equal("+••••••••••33", mList.GetProperty("phoneMasked").GetString());
        Assert.Equal("f•••@example.com", mList.GetProperty("emailMasked").GetString());

        // картка
        var mCard = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.ForeignClient}", masked)).B.GetProperty("client");
        Assert.False(mCard.TryGetProperty("phone", out _));
        Assert.False(mCard.TryGetProperty("email", out _));
        Assert.Equal("+••••••••••33", mCard.GetProperty("phoneMasked").GetString());

        // повний доступ
        var fCard = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.ForeignClient}", full)).B.GetProperty("client");
        Assert.Equal("+380671112233", fCard.GetProperty("phone").GetString());
        Assert.Equal("foreign@example.com", fCard.GetProperty("email").GetString());
        Assert.False(fCard.TryGetProperty("phoneMasked", out _));

        // пошук за телефоном без повного доступу не працює (оракул контактів), за іменем - працює
        Assert.Empty((await Call(HttpMethod.Get, "/api/beauty/clients?search=671112233", masked)).B.EnumerateArray());
        Assert.Single((await Call(HttpMethod.Get, "/api/beauty/clients?search=671112233", full)).B.EnumerateArray());
        Assert.Single((await Call(HttpMethod.Get, "/api/beauty/clients?search=Foreign", masked)).B.EnumerateArray());

        // phone-pending потребує contacts = full
        var (r, b) = await Call(HttpMethod.Post, $"/api/beauty/clients/{w.ForeignClient}/phone-pending", masked, new { action = "reject" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(PermissionKeys.ClientsContacts, b.GetProperty("permission").GetString());
    }

    // ---------- ШІ, маркетинг, нагадування, inbox ----------

    [SkippableFact]
    public async Task Ai_auto_mode_is_owner_only_and_ai_use_gates_actions()
    {
        NeedDb();
        var w = await WorldAsync();
        var admin = await TokenAsync(w, Roles.Admin, null);
        var (r, b) = await Call(HttpMethod.Put, "/api/beauty/ai/settings", admin, new { chat = new { provider = "anthropic", model = "claude-haiku-5-5" }, analytics = new { provider = "anthropic", model = "claude-sonnet-5-5" }, mode = "auto", acknowledgeAutoMode = true, limits = new { monthlyTokenLimit = (long?)null, monthlyRequestLimit = (long?)null, maxOutputTokensPerRequest = 1024 } });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("forbidden_role", Code(b));

        var noAi = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.AiUse, "none"));
        var (ar, ab) = await Call(HttpMethod.Get, "/api/beauty/ai/actions", noAi);
        Assert.Equal(HttpStatusCode.Forbidden, ar.StatusCode);
        Assert.Equal(PermissionKeys.AiUse, ab.GetProperty("permission").GetString());

        // tool без права: 403 {permission, required}
        var limited = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.MarketingCampaigns, "none"));
        var (tr, tb) = await Call(HttpMethod.Post, "/api/beauty/ai/actions", limited, new { tool = "suggest_promotion", input = new { } });
        Assert.Equal(HttpStatusCode.Forbidden, tr.StatusCode);
        Assert.Equal(PermissionKeys.MarketingCampaigns, tb.GetProperty("permission").GetString());
        Assert.Equal("edit", tb.GetProperty("required").GetString());
    }

    [SkippableFact]
    public async Task Marketing_reminders_inbox_catalog_and_channels_are_gated_by_permissions()
    {
        NeedDb();
        var w = await WorldAsync();
        var deputy = await TokenAsync(w, Roles.Deputy, null); // пресет: marketing none, channels.settings none, settings.general none, inbox reply
        foreach (var (m, url, perm) in new (HttpMethod, string, string)[]
                 {
                     (HttpMethod.Get, "/api/beauty/campaigns", PermissionKeys.MarketingCampaigns),
                     (HttpMethod.Get, "/api/beauty/channels", PermissionKeys.ChannelsSettings),
                     (HttpMethod.Get, "/api/beauty/settings/reminders", PermissionKeys.SettingsGeneral),
                     (HttpMethod.Post, "/api/beauty/services", PermissionKeys.ServicesCatalog),
                 })
        {
            var (r, b) = await Call(m, url, deputy, m == HttpMethod.Post ? new { } : null);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("forbidden_role", Code(b));
            Assert.Equal(perm, b.GetProperty("permission").GetString());
        }
        // inbox: reply-право є -> доходить до дії (404 неіснуюча розмова); без права - 403 з no-store
        Assert.Equal(HttpStatusCode.NotFound, (await Call(HttpMethod.Get, $"/api/beauty/conversations/{Guid.NewGuid()}", deputy)).R.StatusCode);
        var viewOnly = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.ChannelsInbox, "view"));
        var (rr, rb) = await Call(HttpMethod.Post, $"/api/beauty/conversations/{Guid.NewGuid()}/resolve", viewOnly);
        Assert.Equal(HttpStatusCode.Forbidden, rr.StatusCode);
        Assert.Equal("reply", rb.GetProperty("required").GetString());
        Assert.Contains("no-store", rr.Headers.CacheControl?.ToString());

        // call-tasks: clients.calls = all
        var noCalls = await TokenAsync(w, Roles.Deputy, null, (PermissionKeys.ClientsCalls, "none"));
        Assert.Equal(HttpStatusCode.Forbidden, (await Call(HttpMethod.Get, "/api/beauty/call-tasks", noCalls)).R.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Call(HttpMethod.Get, "/api/beauty/call-tasks", w.Owner)).R.StatusCode);
    }

    // ---------- TASK-857 security review ----------

    [SkippableFact]
    public async Task Client_card_history_hides_prices_of_foreign_appointments_without_revenue_view_all()
    {
        NeedDb();
        var w = await WorldAsync();
        var deputy = await TokenAsync(w, Roles.Deputy, null); // revenue.view = none
        var history = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.ForeignClient}", deputy)).B.GetProperty("history");
        Assert.NotEqual(0, history.GetArrayLength());
        foreach (var h in history.EnumerateArray())
        {
            Assert.False(h.TryGetProperty("priceOriginal", out _));
            Assert.False(h.TryGetProperty("priceFinal", out _));
        }
        var admin = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.ForeignClient}", w.Owner)).B.GetProperty("history");
        Assert.True(admin[0].TryGetProperty("priceFinal", out _));
    }

    [SkippableFact]
    public async Task Possible_duplicate_outside_own_scope_is_not_disclosed()
    {
        NeedDb();
        var w = await WorldAsync();
        await using (var ctx = fx.Db.CreateContext(w.T.TenantId))
        {
            var c = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(ctx.Clients, x => x.Id == w.S.ClientId);
            c.DuplicateOfClientId = w.ForeignClient;
            await ctx.SaveChangesAsync();
        }
        var spec = await TokenAsync(w, Roles.Specialist, w.S.SpecialistA, (PermissionKeys.ClientsView, "own"));
        var own = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.S.ClientId}", spec)).B.GetProperty("client");
        Assert.False(own.TryGetProperty("possibleDuplicate", out _));
        var all = (await Call(HttpMethod.Get, $"/api/beauty/clients/{w.S.ClientId}", w.Owner)).B.GetProperty("client");
        Assert.Equal(w.ForeignClient.ToString(), all.GetProperty("possibleDuplicate").GetProperty("id").GetString());
    }

    [SkippableFact]
    public async Task Refresh_is_rejected_for_a_user_that_is_no_longer_active_state()
    {
        NeedDb();
        var w = await WorldAsync();
        var u = await fx.CreateUserAsync(w.T, Roles.Deputy);
        Assert.Equal(HttpStatusCode.OK, (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = u.Tokens.RefreshToken })).StatusCode);
        var again = (await fx.LoginAsync(w.T.Slug, u.Email)).RefreshToken;
        await fx.MutateUserAsync(w.T.TenantId, u.UserId, x => { x.State = "invited"; x.PasswordHash = null; });
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/auth/refresh", new { refreshToken = again })).StatusCode);
    }
}
