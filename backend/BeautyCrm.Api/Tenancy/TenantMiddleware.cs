using BeautyCrm.Infrastructure.Data.Tenancy;

namespace BeautyCrm.Api.Tenancy;

/// <summary>
/// Встановлює TenantContext до першого звернення до БД у запиті (app.tenant_id для RLS).
/// Джерела: claim "tenant_id" автентифікованого користувача; заголовок X-Tenant-Id ЛИШЕ в середовищі Development
/// (і якщо Beauty:AllowTenantHeader не вимкнено), бо заголовок не автентифікований. Поза Development ігнорується
/// завжди, незалежно від конфігурації. Захищені ендпоінти все одно вимагають JWT з роллю.
/// Немає tenant — запит проходить далі, але RLS нічого не поверне, а [RequireModule] відповість 401 (fail closed).
/// Webhook-и tenant не мають: його визначає WebhookIngestService за id каналу.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next, IConfiguration config, IHostEnvironment env)
{
    public const string ClaimType = "tenant_id";
    public const string HeaderName = "X-Tenant-Id";

    public async Task InvokeAsync(HttpContext http, TenantContext tenant)
    {
        var raw = http.User.FindFirst(ClaimType)?.Value;
        if (raw is null && env.IsDevelopment() && config.GetValue("Beauty:AllowTenantHeader", true))
            raw = http.Request.Headers[HeaderName].FirstOrDefault();

        if (Guid.TryParse(raw, out var tenantId) && tenantId != Guid.Empty)
            tenant.SetTenant(tenantId);

        await next(http);
    }
}
