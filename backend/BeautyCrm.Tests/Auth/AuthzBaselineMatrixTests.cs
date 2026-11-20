using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Api.Auth;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-847 (ADR-023 §2.11) ARTIFACT-AUTHZ-BASELINE-V1: БАЗОВА матриця авторизації ПОТОЧНОЇ (рольової) поведінки backend.
/// Кожен tenant-ендпоінт (політики Staff/Management/Owner) x {без токена, owner, admin, specialist} -> очікуваний статус.
/// Після переведення на права (TASK-852/853) та сама матриця на пресетах має збігатися без змін очікувань;
/// будь-яка зміна рядка - свідоме рішення, позначене в контракті §30.
///
/// Семантика очікування (рядок "status" або "status:code"):
///  - "403"                 - відмова політикою (Authorize(Policy)): 403 з ПОРОЖНІМ тілом (без поля code);
///  - "403:forbidden_role"  - відмова фільтром ролі ({code: forbidden_role});
///  - інший статус          - запит пройшов авторизацію і дійшов до дії. Запити стандартизовані (неіснуючі GUID, тіло {}),
///                            тому 404/400/422 тут означає "доступ є"; код у відповіді перевіряється лише якщо вказаний в очікуванні.
///  - "401"                 - без токена (JWT fallback policy).
/// Новий ендпоінт без рядка в матриці (або видалений ендпоінт, що лишився в ній) валить <see cref="Matrix_covers_every_tenant_endpoint"/>.
/// Реальний PostgreSQL + RLS (WebApplicationFactory), як і решта API-тестів.
/// </summary>
public sealed class AuthzBaselineMatrixTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly Guid FakeId = Guid.Parse("0f0f0f0f-0f0f-4f0f-8f0f-0f0f0f0f0f0f");

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    // ------------------------------------------------------------------ таблиця-дані

    /// <summary>Очікування по ролях: Owner / Admin / Specialist (+ без токена завжди 401).</summary>
    public sealed record Row(string Method, string Route, string Owner, string Admin, string Specialist);

    private const string PolicyDeny = "403";
    private const string RoleDeny = "403:forbidden_role";

    /// <summary>Політика Staff: усі три ролі проходять авторизацію.</summary>
    private static Row Open(string m, string route, string s) => new(m, route, s, s, s);

    /// <summary>Staff + фільтр ролі ([ReminderRoles(Owner, Admin)] / InboxManagementOnly): specialist - 403 forbidden_role.</summary>
    private static Row MgmtFilter(string m, string route, string s) => new(m, route, s, s, RoleDeny);

    /// <summary>Лише owner політикою (Owner): admin/specialist - 403 без тіла.</summary>
    private static Row OwnerPolicy(string m, string route, string s) => new(m, route, s, PolicyDeny, PolicyDeny);

    /// <summary>Лише owner фільтром ([ReminderRoles(Owner)]): admin/specialist - 403 forbidden_role.</summary>
    private static Row OwnerFilter(string m, string route, string s) => new(m, route, s, RoleDeny, RoleDeny);

    /// <summary>Management-політика контролера + [AiOwnerOnly] на методі: admin - 403 forbidden_role, specialist - 403 без тіла.</summary>
    private static Row AiKey(string m, string route, string s) => new(m, route, s, RoleDeny, RoleDeny); // D3: тіло 403 несе forbidden_role

    public static readonly IReadOnlyList<Row> Matrix =
    [
        // ---- auth / команда
        Open("GET", "api/auth/me", "200"),
        Open("GET", "api/access/catalog", "200"), // ADR-023 (TASK-850): каталог прав, Staff-політика, однаковий для всіх ролей
        MgmtFilter("GET", "api/users", "200"),
        MgmtFilter("PATCH", "api/users/{id:guid}/status", "404"),
        MgmtFilter("POST", "api/invites", "422"),
        MgmtFilter("GET", "api/invites", "200"),
        MgmtFilter("DELETE", "api/invites/{id:guid}", "404"),
        // ADR-023 (TASK-851): доступ користувача (team.manage); ціль неіснуюча -> 404 user_not_found
        MgmtFilter("GET", "api/users/{id:guid}/access", "404"),
        MgmtFilter("PUT", "api/users/{id:guid}/access", "404"),
        MgmtFilter("POST", "api/users/{id:guid}/access/reset", "404"),
        OwnerPolicy("GET", "api/users/access-audit", "200"), // журнал змін доступу: лише owner (Q7)

        // ---- ШІ (beauty_ai)
        MgmtFilter("GET", "api/beauty/ai/settings", "200"),
        MgmtFilter("PUT", "api/beauty/ai/settings", "422"),
        AiKey("PUT", "api/beauty/ai/settings/key-source", "422"),
        AiKey("PUT", "api/beauty/ai/settings/keys/{provider}", "422"),
        AiKey("DELETE", "api/beauty/ai/settings/keys/{provider}", "204"),
        AiKey("POST", "api/beauty/ai/settings/keys/{provider}/test", "422"),
        MgmtFilter("GET", "api/beauty/ai/models", "200"),
        MgmtFilter("GET", "api/beauty/ai/usage", "200"),
        MgmtFilter("GET", "api/beauty/ai/actions", "200"),
        MgmtFilter("POST", "api/beauty/ai/actions", "422"),
        MgmtFilter("POST", "api/beauty/ai/actions/{id:guid}/approve", "409"),
        MgmtFilter("POST", "api/beauty/ai/actions/{id:guid}/reject", "409"),
        MgmtFilter("POST", "api/beauty/ai/actions/{id:guid}/revert", "409"),

        // ---- слоти й записи (beauty_booking)
        Open("GET", "api/beauty/slots", "404"),
        Open("GET", "api/beauty/appointments", "200"),
        Open("GET", "api/beauty/appointments/{id:guid}", "404"),
        Open("POST", "api/beauty/appointments", "422"),
        // specialist: неіснуючий/чужий запис = 404 (GuardAsync) ще до валідації; керівник доходить до валідації тіла ({} -> 422 nothing_to_change)
        new("PATCH", "api/beauty/appointments/{id:guid}", "422", "422", "404"),
        Open("POST", "api/beauty/appointments/{id:guid}/cancel", "404"),

        // ---- кампанії (beauty_marketing; Marketing:Enabled=true у тестовому хості)
        MgmtFilter("POST", "api/beauty/marketing/audience/preview", "422"),
        MgmtFilter("GET", "api/beauty/campaigns", "200"),
        MgmtFilter("POST", "api/beauty/campaigns", "422"),
        MgmtFilter("GET", "api/beauty/campaigns/{id:guid}", "404"),
        MgmtFilter("PUT", "api/beauty/campaigns/{id:guid}", "404"),
        MgmtFilter("DELETE", "api/beauty/campaigns/{id:guid}", "404"),
        MgmtFilter("GET", "api/beauty/campaigns/{id:guid}/preview", "404"),
        MgmtFilter("POST", "api/beauty/campaigns/{id:guid}/preview", "404"),
        MgmtFilter("POST", "api/beauty/campaigns/{id:guid}/confirm", "400"),
        MgmtFilter("POST", "api/beauty/campaigns/{id:guid}/cancel", "404"),
        MgmtFilter("GET", "api/beauty/campaigns/{id:guid}/stats", "404"),
        MgmtFilter("GET", "api/beauty/campaigns/{id:guid}/coupons/summary", "404"),

        // ---- політика скасування
        Open("GET", "api/beauty/settings/cancellation", "200"),
        OwnerPolicy("PUT", "api/beauty/settings/cancellation", "422"),

        // ---- каталог (читання - всі, зміни - Management)
        Open("GET", "api/beauty/services", "200"),
        MgmtFilter("POST", "api/beauty/services", "422"),
        MgmtFilter("PUT", "api/beauty/services/{id:guid}", "422"),
        Open("GET", "api/beauty/services/{id:guid}/prices", "404"),
        MgmtFilter("PUT", "api/beauty/services/{id:guid}/prices", "404"),
        Open("GET", "api/beauty/promotions", "200"),
        MgmtFilter("POST", "api/beauty/promotions", "422"),
        MgmtFilter("PUT", "api/beauty/promotions/{id:guid}", "422"),
        Open("GET", "api/beauty/promotions/preview", "404"),

        // ---- канали (beauty_channels; Management-політика)
        MgmtFilter("GET", "api/beauty/channels", "200"),
        MgmtFilter("PUT", "api/beauty/channels/{id:guid}", "422"),
        MgmtFilter("POST", "api/beauty/channels/{id:guid}/telegram/sync-commands", "404"),

        // ---- клієнти (Management-політика) і підресурси клієнта ([ReminderRoles])
        MgmtFilter("GET", "api/beauty/clients", "200"),
        MgmtFilter("GET", "api/beauty/clients/{id:guid}", "404"),
        MgmtFilter("POST", "api/beauty/clients", "422"),
        MgmtFilter("POST", "api/beauty/clients/{id:guid}/notes", "422"),
        MgmtFilter("POST", "api/beauty/clients/{clientId:guid}/phone-pending", "422"),
        MgmtFilter("GET", "api/beauty/clients/{clientId:guid}/contact-preferences", "404"),
        MgmtFilter("PUT", "api/beauty/clients/{clientId:guid}/contact-preferences", "404"),
        MgmtFilter("GET", "api/beauty/clients/{clientId:guid}/marketing-consent", "404"),
        MgmtFilter("PUT", "api/beauty/clients/{clientId:guid}/marketing-consent", "404"),
        MgmtFilter("GET", "api/beauty/clients/{clientId:guid}/coupons", "404"),

        // ---- аналітика (Management-політика)
        MgmtFilter("GET", "api/beauty/analytics/network/details", "422"),
        MgmtFilter("GET", "api/beauty/analytics/network", "200"),
        MgmtFilter("GET", "api/beauty/analytics/network/series", "200"),
        MgmtFilter("GET", "api/beauty/analytics/locations", "200"),
        MgmtFilter("GET", "api/beauty/analytics/locations/{locationId:guid}/breakdown", "404"),
        MgmtFilter("GET", "api/beauty/analytics/promotions", "200"),

        // ---- купони
        MgmtFilter("GET", "api/beauty/coupons", "200"),
        MgmtFilter("POST", "api/beauty/coupons", "422"),
        MgmtFilter("PATCH", "api/beauty/coupons/{id:guid}", "422"),
        Open("POST", "api/beauty/coupons/check", "422"),

        // ---- розмови (inbox; InboxManagementOnly)
        MgmtFilter("GET", "api/beauty/conversations", "200"),
        MgmtFilter("GET", "api/beauty/conversations/{id:guid}", "404"),
        MgmtFilter("POST", "api/beauty/conversations/{id:guid}/reply", "400"),
        MgmtFilter("POST", "api/beauty/conversations/{id:guid}/resume-ai", "404"),
        MgmtFilter("POST", "api/beauty/conversations/{id:guid}/resolve", "404"),
        MgmtFilter("POST", "api/beauty/conversations/{id:guid}/read", "404"),

        // ---- заклади (читання - всі, зміни - Management)
        Open("GET", "api/beauty/locations", "200"),
        MgmtFilter("POST", "api/beauty/locations", "422"),
        MgmtFilter("PUT", "api/beauty/locations/{id:guid}", "422"),
        MgmtFilter("PUT", "api/beauty/locations/{id:guid}/closed-weekdays", "422"),
        MgmtFilter("PUT", "api/beauty/locations/{id:guid}/working-hours", "422"),
        Open("GET", "api/beauty/locations/{id:guid}/closures", "404"),
        MgmtFilter("POST", "api/beauty/locations/{id:guid}/closures", "404"),
        MgmtFilter("DELETE", "api/beauty/locations/{id:guid}/closures/{closureId:guid}", "404"),

        // ---- огляд, фідбек, власний виторг (TASK-852: revenue.view >= own; без параметрів періоду - 422 invalid_range)
        new("GET", "api/beauty/me/revenue", "422", "422", RoleDeny),
        MgmtFilter("GET", "api/beauty/overview", "200"),
        Open("POST", "api/beauty/feedback", "422"),

        // ---- нагадування, задачі «зателефонувати»
        MgmtFilter("GET", "api/beauty/call-tasks", "200"),
        MgmtFilter("PATCH", "api/beauty/call-tasks/{id:guid}", "422"),
        MgmtFilter("GET", "api/beauty/settings/reminders", "200"),
        OwnerFilter("PUT", "api/beauty/settings/reminders", "422"),

        // ---- собівартість послуг
        MgmtFilter("GET", "api/beauty/service-costs", "200"),
        MgmtFilter("GET", "api/beauty/services/{id:guid}/costs", "404"),
        MgmtFilter("PUT", "api/beauty/services/{id:guid}/costs", "422"),
        MgmtFilter("DELETE", "api/beauty/services/{id:guid}/costs", "404"),

        // ---- склад (beauty_inventory, ADR-025 §32.5; модуль увімкнено; specialist-пресет: view=own, manage=none, costs=none)
        Open("GET", "api/beauty/inventory/scope", "200"),
        Open("GET", "api/beauty/inventory/products", "200"),
        Open("GET", "api/beauty/inventory/products/by-barcode", "404"),
        Open("GET", "api/beauty/inventory/products/{id:guid}", "404"),
        Open("GET", "api/beauty/inventory/categories", "200"),
        Open("GET", "api/beauty/inventory/products/categories", "200"),
        MgmtFilter("POST", "api/beauty/inventory/categories", "422"),
        MgmtFilter("PUT", "api/beauty/inventory/categories/{id:guid}", "404"),
        MgmtFilter("PUT", "api/beauty/inventory/categories/{id:guid}/move", "404"),
        MgmtFilter("POST", "api/beauty/inventory/categories/{id:guid}/archive", "404"),
        MgmtFilter("POST", "api/beauty/inventory/categories/{id:guid}/unarchive", "404"),
        MgmtFilter("DELETE", "api/beauty/inventory/categories/{id:guid}", "404"),
        MgmtFilter("POST", "api/beauty/inventory/products", "422"),
        MgmtFilter("PUT", "api/beauty/inventory/products/{id:guid}", "404"),
        MgmtFilter("PUT", "api/beauty/inventory/products/{id:guid}/locations/{locationId:guid}", "404"),
        MgmtFilter("DELETE", "api/beauty/inventory/products/{id:guid}/locations/{locationId:guid}", "404"),
        MgmtFilter("GET", "api/beauty/inventory/suppliers", "200"),
        MgmtFilter("POST", "api/beauty/inventory/suppliers", "422"),
        MgmtFilter("PUT", "api/beauty/inventory/suppliers/{id:guid}", "404"),
        Open("GET", "api/beauty/inventory/stock", "422"),
        Open("GET", "api/beauty/inventory/stock/summary", "200"),
        Open("GET", "api/beauty/inventory/batches", "200"),
        Open("GET", "api/beauty/inventory/movements", "200"),
        Open("GET", "api/beauty/inventory/operations", "200"),
        Open("GET", "api/beauty/inventory/operations/{id:guid}", "404"),
        // ---- замовлення товарів клієнтами (ADR-026 §33.13, TASK-903): перегляд view=own, дії manage=own (specialist: manage=none)
        Open("GET", "api/beauty/inventory/product-orders", "200"),
        Open("GET", "api/beauty/inventory/product-orders/counts", "200"),
        Open("GET", "api/beauty/inventory/product-orders/{id:guid}", "404"),
        MgmtFilter("POST", "api/beauty/inventory/product-orders/{id:guid}/fulfill", "404"),
        MgmtFilter("POST", "api/beauty/inventory/product-orders/{id:guid}/cancel", "404"),
        MgmtFilter("POST", "api/beauty/inventory/product-orders/{id:guid}/reconcile-payment", "404"),
        MgmtFilter("POST", "api/beauty/inventory/receipts", "422"),
        MgmtFilter("POST", "api/beauty/inventory/write-offs", "422"),
        MgmtFilter("POST", "api/beauty/inventory/transfers", "422"),
        Open("GET", "api/beauty/inventory/stocktakes", "200"),
        MgmtFilter("POST", "api/beauty/inventory/stocktakes", "404"),
        Open("GET", "api/beauty/inventory/stocktakes/{id:guid}", "404"),
        MgmtFilter("PUT", "api/beauty/inventory/stocktakes/{id:guid}/lines", "422"),
        MgmtFilter("POST", "api/beauty/inventory/stocktakes/{id:guid}/post", "422"),
        MgmtFilter("POST", "api/beauty/inventory/stocktakes/{id:guid}/cancel", "422"),
        // ---- склад, етап 2a (TASK-884/886): норми матеріалів (читання - view>=own, зміна - owner/admin + manage=all) і сповіщення персоналу
        Open("GET", "api/beauty/inventory/service-materials", "200"),
        Open("GET", "api/beauty/inventory/services/{serviceId:guid}/materials", "404"),
        MgmtFilter("PUT", "api/beauty/inventory/services/{serviceId:guid}/materials", "422"),
        MgmtFilter("DELETE", "api/beauty/inventory/services/{serviceId:guid}/materials", "404"),
        Open("GET", "api/beauty/inventory/alerts", "200"),
        Open("GET", "api/beauty/inventory/alerts/unread-count", "200"),
        Open("POST", "api/beauty/inventory/alerts/{id:guid}/read", "404"),
        Open("POST", "api/beauty/inventory/alerts/read-all", "200"),
        // ---- склад, етап 2b (TASK-890, §32.24.8): прив'язка Telegram власника - лише owner (фільтр до прив'язки тіла); прапор у тестовому хості вимкнено
        new("GET", "api/beauty/inventory/telegram/link", "200", RoleDeny, RoleDeny),
        new("POST", "api/beauty/inventory/telegram/link", "403:telegram_alerts_disabled", RoleDeny, RoleDeny),
        new("DELETE", "api/beauty/inventory/telegram/link", "204", RoleDeny, RoleDeny),

        // ---- спеціалісти (читання - всі, зміни - Management)
        Open("GET", "api/beauty/specialists", "200"),
        Open("GET", "api/beauty/specialists/{id:guid}", "404"),
        MgmtFilter("POST", "api/beauty/specialists", "422"),
        MgmtFilter("PUT", "api/beauty/specialists/{id:guid}", "422"),
        MgmtFilter("PUT", "api/beauty/specialists/{id:guid}/services", "422"),
        MgmtFilter("PUT", "api/beauty/specialists/{id:guid}/schedule", "404"),
        MgmtFilter("POST", "api/beauty/specialists/{id:guid}/locations", "404"),
        MgmtFilter("DELETE", "api/beauty/specialists/{id:guid}/locations/{locationId:guid}", "404"),
        MgmtFilter("POST", "api/beauty/specialists/{id:guid}/invite", "422"),

        // ---- відсутності (створення: specialist лише собі - чужий/неіснуючий профіль = 403 forbidden; скоуп - окремий тест)
        Open("GET", "api/beauty/absences", "200"),
        new("POST", "api/beauty/specialists/{id:guid}/absences", "422", "422", "422"),
        MgmtFilter("POST", "api/beauty/absences/{id:guid}/approve", "404"),
        MgmtFilter("POST", "api/beauty/absences/{id:guid}/reject", "404"),
        Open("POST", "api/beauty/absences/{id:guid}/cancel", "404"),
    ];

    private static string Key(string method, string route) => method + " /" + route;

    // ------------------------------------------------------------------ середовище

    private sealed record Env(HttpClient Client, string OwnerToken, string AdminToken, string SpecToken, BeautySeed Seed, WebApplicationFactory<Program> Factory);

    private async Task<Env> NewEnvAsync()
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Marketing:Enabled", "true");
            b.UseSetting("Marketing:LegalGateApproved", "true"); // legal gate §26.16 поза Development
        });
        _disposables.Add(f);
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]); // усі модулі, включно з beauty_marketing
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        return new Env(f.CreateClient(), owner.AccessToken, admin.AccessToken, spec.AccessToken, seed, f);
    }

    private static string Materialize(string route)
    {
        var url = "/" + System.Text.RegularExpressions.Regex.Replace(route, @"\{[A-Za-z]+:guid\}", FakeId.ToString());
        return url.Replace("{provider}", "openai");
    }

    private sealed record Observed(int Status, string? Code)
    {
        public override string ToString() => Code is null ? Status.ToString() : $"{Status}:{Code}";
    }

    private static async Task<Observed> CallAsync(HttpClient client, string method, string route, string? token)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), Materialize(route));
        if (method is "POST" or "PUT" or "PATCH")
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await client.SendAsync(req);
        string? code = null;
        var body = await resp.Content.ReadAsStringAsync();
        if (body.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("code", out var c)
                    && c.ValueKind == JsonValueKind.String) code = c.GetString();
            }
            catch (JsonException)
            {
                // не-JSON тіло: коду немає
            }
        }
        return new Observed((int)resp.StatusCode, code);
    }

    private static bool Matches(string expected, Observed actual)
    {
        var parts = expected.Split(':', 2);
        if (!int.TryParse(parts[0], out var status) || status != actual.Status) return false;
        if (parts.Length == 2) return parts[1] == actual.Code;
        // "403" без коду = політика: порожнє тіло; для решти кодів код не фіксується
        return status != 403 || actual.Code is null;
    }

    // ------------------------------------------------------------------ тести

    [SkippableFact]
    public async Task Matrix_covers_every_tenant_endpoint()
    {
        NeedDb();
        var env = await NewEnvAsync();
        var source = env.Factory.Services.GetRequiredService<EndpointDataSource>();
        string[] tenantPolicies = [ AuthPolicies.Staff, AuthPolicies.Owner];

        var actual = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ep in source.Endpoints.OfType<RouteEndpoint>())
        {
            if (ep.Metadata.GetMetadata<IAllowAnonymous>() is not null) continue;
            if (!ep.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy is { } p && tenantPolicies.Contains(p))) continue;
            var methods = ep.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var m in methods) actual.Add(Key(m, ep.RoutePattern.RawText!.TrimStart('/')));
        }

        var declared = Matrix.Select(r => Key(r.Method, r.Route)).ToList();
        Assert.Equal(declared.Count, declared.Distinct().Count()); // дублікатів у таблиці немає

        var missing = actual.Except(declared).ToList();
        var stale = declared.Except(actual).ToList();
        Assert.True(missing.Count == 0 && stale.Count == 0,
            "Матриця не збігається з реальними tenant-ендпоінтами.\n  Немає рядка в матриці (новий ендпоінт): "
            + string.Join("; ", missing) + "\n  Рядок без ендпоінта (видалений/перейменований): " + string.Join("; ", stale));
    }

    [SkippableFact]
    public async Task Anonymous_gets_401_on_every_endpoint()
    {
        NeedDb();
        var env = await NewEnvAsync();
        var failures = new List<string>();
        foreach (var row in Matrix)
        {
            var got = await CallAsync(env.Client, row.Method, row.Route, token: null);
            if (got.Status != 401) failures.Add($"{row.Method} {row.Route}: expected 401, got {got}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [SkippableTheory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("specialist")]
    public async Task Role_matrix_matches_baseline(string role)
    {
        NeedDb();
        var env = await NewEnvAsync();
        var token = role switch { "owner" => env.OwnerToken, "admin" => env.AdminToken, _ => env.SpecToken };
        var failures = new List<string>();
        foreach (var row in Matrix)
        {
            var expected = role switch { "owner" => row.Owner, "admin" => row.Admin, _ => row.Specialist };
            var got = await CallAsync(env.Client, row.Method, row.Route, token);
            if (!Matches(expected, got)) failures.Add($"{role,-10} {row.Method,-6} {row.Route}: expected {expected}, got {got}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} з {Matrix.Count} розбіжностей:\n" + string.Join("\n", failures));
    }

    // ------------------------------------------------------------------ скоуп specialist: власні/чужі записи (ADR-008)

    private HttpRequestMessage Req(HttpMethod m, string url, string token, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    private static async Task<JsonElement> J(HttpResponseMessage r) => await Read<JsonElement>(r);

    private static IEnumerable<Guid> Ids(JsonElement list) => list.EnumerateArray().Select(a => a.GetProperty("id").GetGuid());

    private static string Range() =>
        $"from={DateTime.UtcNow.Date:yyyy-MM-dd}T00:00:00Z&to={DateTime.UtcNow.Date.AddDays(10):yyyy-MM-dd}T00:00:00Z";

    [SkippableFact]
    public async Task Specialist_sees_and_changes_only_own_appointments_staff_sees_all()
    {
        NeedDb();
        var env = await NewEnvAsync();
        var c = env.Client;
        var s = env.Seed;
        var url = $"/api/beauty/appointments?{Range()}";

        // календар: owner/admin бачать обох майстрів, specialist - лише свій запис (навіть із фільтром на чужого)
        foreach (var token in new[] { env.OwnerToken, env.AdminToken })
        {
            var all = await c.SendAsync(Req(HttpMethod.Get, url, token));
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            var ids = Ids(await J(all)).ToList();
            Assert.Contains(s.AppointmentA, ids);
            Assert.Contains(s.AppointmentB, ids);
        }
        var own = await c.SendAsync(Req(HttpMethod.Get, url, env.SpecToken));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal([s.AppointmentA], Ids(await J(own)).ToList());
        var spoof = await c.SendAsync(Req(HttpMethod.Get, url + $"&specialistId={s.SpecialistB}", env.SpecToken));
        Assert.Equal(HttpStatusCode.OK, spoof.StatusCode);
        Assert.Equal([s.AppointmentA], Ids(await J(spoof)).ToList());

        // одиничний запис: свій 200, чужий 404 appointment_not_found; керівники бачать будь-який
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, $"/api/beauty/appointments/{s.AppointmentA}", env.SpecToken))).StatusCode);
        var foreign = await c.SendAsync(Req(HttpMethod.Get, $"/api/beauty/appointments/{s.AppointmentB}", env.SpecToken));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("appointment_not_found", (await J(foreign)).GetProperty("code").GetString());
        foreach (var token in new[] { env.OwnerToken, env.AdminToken })
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Get, $"/api/beauty/appointments/{s.AppointmentB}", token))).StatusCode);

        // зміна й скасування чужого запису: 404 (існування не розкривається); запис лишається незайманим
        var patch = await c.SendAsync(Req(new HttpMethod("PATCH"), $"/api/beauty/appointments/{s.AppointmentB}", env.SpecToken, new { status = "completed" }));
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        var cancel = await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/appointments/{s.AppointmentB}/cancel", env.SpecToken, new { reason = "x" }));
        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        var still = await c.SendAsync(Req(HttpMethod.Get, $"/api/beauty/appointments/{s.AppointmentB}", env.OwnerToken));
        Assert.Equal("confirmed", (await J(still)).GetProperty("status").GetString());

        // зміна власного запису дозволена (авторизація пройдена)
        var ownPatch = await c.SendAsync(Req(new HttpMethod("PATCH"), $"/api/beauty/appointments/{s.AppointmentA}", env.SpecToken, new { status = "completed" }));
        Assert.Equal(HttpStatusCode.OK, ownPatch.StatusCode);

        // створення: specialist лише для себе (403 forbidden), керівник - для будь-кого (авторизація пройдена)
        object Create(Guid specialistId) => new
        {
            locationId = s.LocationId, specialistId, serviceId = s.ServiceId,
            startsAt = DateTimeOffset.UtcNow.Date.AddDays(5).AddHours(9),
            client = new { id = s.ClientId }, paymentMethod = "cash",
        };
        var forOther = await c.SendAsync(Req(HttpMethod.Post, "/api/beauty/appointments", env.SpecToken, Create(s.SpecialistB)));
        Assert.Equal(HttpStatusCode.Forbidden, forOther.StatusCode);
        Assert.Equal("forbidden", (await J(forOther)).GetProperty("code").GetString());
        var byAdmin = await c.SendAsync(Req(HttpMethod.Post, "/api/beauty/appointments", env.AdminToken, Create(s.SpecialistB)));
        Assert.NotEqual(HttpStatusCode.Forbidden, byAdmin.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, byAdmin.StatusCode);
    }

    [SkippableFact]
    public async Task Specialist_absences_only_for_own_profile_and_cannot_decide()
    {
        NeedDb();
        var env = await NewEnvAsync();
        var c = env.Client;
        var s = env.Seed;
        var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(30));
        var body = new { type = "day_off", dateFrom = day, dateTo = day, note = "n" };

        // чужий профіль: 403 forbidden_role не з фільтра, а з сервісу - фіксуємо лише статус
        var other = await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/specialists/{s.SpecialistB}/absences", env.SpecToken, body));
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        // власний профіль: 201 (requested)
        var mine = await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/specialists/{s.SpecialistA}/absences", env.SpecToken, body));
        Assert.Equal(HttpStatusCode.Created, mine.StatusCode);
        var absenceId = (await J(mine)).GetProperty("id").GetGuid();

        // рішення щодо відсутності - лише Management (політика, порожнє тіло)
        foreach (var action in new[] { "approve", "reject" })
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/absences/{absenceId}/{action}", env.SpecToken))).StatusCode);

        // керівник створює будь-кому (approved) і приймає рішення
        var day2 = day.AddDays(3);
        var byAdmin = await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/specialists/{s.SpecialistB}/absences", env.AdminToken,
            new { type = "day_off", dateFrom = day2, dateTo = day2, note = "n" }));
        Assert.Equal(HttpStatusCode.Created, byAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Req(HttpMethod.Post, $"/api/beauty/absences/{absenceId}/approve", env.AdminToken))).StatusCode);
    }
}
