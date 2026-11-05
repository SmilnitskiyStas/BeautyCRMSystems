using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyControlPlane;

/// <summary>
/// Керування бізнесами з ShelfGuard (ADR-018, beauty-contracts.md §27.3-§27.7). Порядок перевірок: існування (404) -> тіло (422) ->
/// стан (409). Кожна зміна пише рядок у platform_audit_log у ТІЙ САМІЙ транзакції. Токен запрошення існує лише в пам'яті цього
/// сервісу до моменту формування відповіді: у БД іде sha256, у лог і аудит - ніколи.
/// </summary>
public sealed partial class ControlPlaneService(IControlPlaneStore store, ControlPlaneOptions options, TimeProvider clock)
{
    private const string ActorType = "shelfguard_admin";

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ExternalRefPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailPattern();

    private static readonly AuthError NotFound = AuthError.NotFound("tenant_not_found", "Tenant not found.");
    private static readonly AuthError ActorRequired = AuthError.Validation("actor_required", "actor.id (1-64) and actor.displayName (1-120) are required.");

    // ---------- create (§27.3) ----------

    public async Task<AuthResult<CpCreateResult>> CreateAsync(CpCreateRequest req, CpCaller caller, CancellationToken ct)
    {
        var externalRef = req.ExternalRef?.Trim();
        if (externalRef is null || !ExternalRefPattern().IsMatch(externalRef))
            return AuthError.Validation("invalid_external_ref", "externalRef must match [A-Za-z0-9_-]{1,64}.");
        if (ParseActor(req.Actor) is not { } actor) return ActorRequired;
        if (req.Owner?.Password is not null) return AuthError.Validation("password_not_allowed", "The owner password is set by the owner via the invite link.");
        var slug = TenantRules.NormalizeSlug(req.Slug);
        if (slug is null) return AuthError.Validation("invalid_slug", "Slug: 3-64 chars, a-z, 0-9, hyphen.");
        var modules = req.Modules is null ? BeautyModules.All : TenantRules.NormalizeModules(req.Modules);
        if (modules is null) return AuthError.Validation("unknown_module", "Unknown module name.");
        var email = req.Owner?.Email?.Trim().ToLowerInvariant();
        if (email is null || email.Length > 320 || !EmailPattern().IsMatch(email))
            return AuthError.Validation("invalid_email", "owner.email must be a valid email.");
        var ownerName = req.Owner?.FullName?.Trim();
        if (string.IsNullOrEmpty(ownerName) || ownerName.Length > 200) return AuthError.Validation("invalid_name", "owner.fullName is required (1-200).");
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return AuthError.Validation("invalid_name", "name is required (1-200).");
        var inviteBase = InviteBaseUrl();

        // Швидкий шлях: tenant з таким externalRef уже є -> replay або розбіжність.
        var existing = await ReplayOrConflictAsync(externalRef, slug, email, actor, caller, ct);
        if (existing is not null) return existing;

        var now = Now();
        var tenantId = Guid.NewGuid();
        var invite = NewInvite(now);
        CpOwnerView? ownerView = null;
        var created = await store.TryCreateAsync(new CpNewTenant(tenantId, externalRef, name, slug, modules, now), new CpNewOwner(email, ownerName),
            async session =>
            {
                var owner = (await session.GetOwnerAsync(ct))!;
                ownerView = new CpOwnerView(owner.UserId, owner.Email, owner.State);
                var inviteId = await session.AddInviteAsync(owner.UserId, invite.Hash, invite.ExpiresAt, CreatedBy(actor), now, ct);
                await session.AddAuditAsync(Audit(
                    "tenant.created", tenantId, actor, caller,
                    new() { ["external_ref"] = externalRef, ["slug"] = slug, ["modules"] = modules, ["owner_user_id"] = owner.UserId }), ct);
                await session.AddAuditAsync(Audit(
                    "owner_invite.issued", tenantId, actor, caller,
                    new() { ["user_id"] = owner.UserId, ["invite_id"] = inviteId, ["expires_at"] = Iso(invite.ExpiresAt) }), ct);
            }, ct);

        if (created)
            return new CpCreateResult(
                new CpCreateResponse(tenantId, externalRef, slug, "active", modules, ownerView!, new CpInviteView(InviteUrl(inviteBase, invite.Token), Iso(invite.ExpiresAt))),
                Replayed: false);

        // Програли гонку (унікальний індекс external_ref або slug): перечитуємо стан.
        var raced = await ReplayOrConflictAsync(externalRef, slug, email, actor, caller, ct);
        return raced ?? AuthError.Conflict("tenant_slug_taken", "A tenant with this slug already exists.");
    }

    /// <summary>null = tenant з таким externalRef немає.</summary>
    private async Task<AuthResult<CpCreateResult>?> ReplayOrConflictAsync(
        string externalRef, string slug, string email, SgActor actor, CpCaller caller, CancellationToken ct)
    {
        var (found, result) = await store.WithTenantAsync<AuthResult<CpCreateResult>>(externalRef, async session =>
        {
            var owner = await session.GetOwnerAsync(ct);
            if (session.Tenant.Slug != slug || owner is null || !string.Equals(owner.Email, email, StringComparison.OrdinalIgnoreCase))
                return AuthError.Conflict("external_ref_conflict", "externalRef is already bound to a different tenant.");
            await session.AddAuditAsync(Audit("tenant.create_replayed", session.Tenant.Id, actor, caller, new()), ct);
            var t = session.Tenant;
            return new CpCreateResult(
                new CpCreateResponse(t.Id, t.ExternalRef, t.Slug, t.Status, t.Modules, new CpOwnerView(owner.UserId, owner.Email, owner.State), null),
                Replayed: true);
        }, ct);
        return found ? result : null;
    }

    // ---------- get (§27.4) ----------

    public async Task<AuthResult<CpTenantResponse>> GetAsync(string externalRef, CancellationToken ct)
    {
        if (!ExternalRefPattern().IsMatch(externalRef)) return NotFound;
        var now = Now();
        var (found, result) = await store.WithTenantAsync<AuthResult<CpTenantResponse>>(externalRef, async s =>
        {
            var t = s.Tenant;
            var owner = await s.GetOwnerAsync(ct);
            if (owner is null) return NotFound;
            string? inviteExpires = null;
            if (owner.State == "invited" && await s.GetActiveInviteExpiryAsync(owner.UserId, now, ct) is { } exp) inviteExpires = Iso(exp);
            return new CpTenantResponse(
                t.Id, t.ExternalRef, t.Slug, t.Name, t.Status, t.Modules, t.IsSuspended && t.SuspendedAt is { } at ? Iso(at) : null,
                new CpOwnerDetails(owner.UserId, owner.Email, owner.State, inviteExpires), Iso(t.CreatedAt));
        }, ct);
        return found ? result : NotFound;
    }

    // ---------- suspend / resume (§27.5) ----------

    public Task<AuthResult<CpSuspendResponse>> SuspendAsync(string externalRef, CpSuspendRequest? req, CpCaller caller, CancellationToken ct) =>
        InTenantAsync<CpSuspendResponse>(externalRef, req?.Actor, async (s, actor) =>
        {
            var reason = req?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > 500) return AuthError.Validation("reason_required", "reason is required (1-500).");
            var now = Now();
            var changed = await s.SuspendAsync(reason, now, ct);
            var at = changed ? now : s.Tenant.SuspendedAt ?? now;
            await s.AddAuditAsync(Audit("tenant.suspended", s.Tenant.Id, actor, caller,
                changed ? new() { ["reason"] = reason } : new() { ["reason"] = reason, ["noop"] = true }), ct);
            return new CpSuspendResponse("suspended", Iso(at));
        }, ct);

    public Task<AuthResult<CpResumeResponse>> ResumeAsync(string externalRef, CpActorRequest? req, CpCaller caller, CancellationToken ct) =>
        InTenantAsync<CpResumeResponse>(externalRef, req?.Actor, async (s, actor) =>
        {
            var changed = await s.ResumeAsync(Now(), ct);
            await s.AddAuditAsync(Audit("tenant.resumed", s.Tenant.Id, actor, caller, changed ? new() : new() { ["noop"] = true }), ct);
            return new CpResumeResponse("active");
        }, ct);

    // ---------- modules (§27.6, TASK-831) ----------

    public Task<AuthResult<CpModulesResponse>> SetModulesAsync(string externalRef, CpModulesRequest? req, CpCaller caller, CancellationToken ct) =>
        InTenantAsync<CpModulesResponse>(externalRef, req?.Actor, async (s, actor) =>
        {
            if (req?.Modules is not { Count: > 0 }) return AuthError.Validation("modules_required", "modules must be a non-empty array.");
            var modules = TenantRules.NormalizeModules(req.Modules);
            if (modules is null) return AuthError.Validation("unknown_module", "Unknown module name.");
            var before = s.Tenant.Modules;
            var changed = await s.SetModulesAsync(modules, Now(), ct);
            await s.AddAuditAsync(Audit("tenant.modules_changed", s.Tenant.Id, actor, caller,
                changed ? new() { ["before"] = before, ["after"] = modules } : new() { ["noop"] = true, ["before"] = before, ["after"] = before }), ct);
            return new CpModulesResponse(changed ? modules : before);
        }, ct);

    // ---------- owner-invite (§27.7) ----------

    public async Task<AuthResult<CpOwnerInviteResponse>> ReissueOwnerInviteAsync(string externalRef, CpActorRequest? req, CpCaller caller, CancellationToken ct)
    {
        if (!ExternalRefPattern().IsMatch(externalRef)) return NotFound;
        var inviteBase = InviteBaseUrl();
        var now = Now();
        var invite = NewInvite(now);
        var (found, result) = await store.WithTenantAsync<AuthResult<CpOwnerInviteResponse>>(externalRef, async s =>
        {
            if (ParseActor(req?.Actor) is not { } actor) return ActorRequired;
            var owner = await s.GetOwnerAsync(ct);
            if (owner is null) return NotFound;
            if (owner.State != "invited") return AuthError.Conflict("owner_already_active", "The owner has already set a password.");
            if (s.Tenant.IsSuspended) return AuthError.Conflict("tenant_suspended", "The tenant is suspended.");

            foreach (var revokedId in await s.RevokeActiveInvitesAsync(owner.UserId, now, ct))
                await s.AddAuditAsync(Audit("owner_invite.revoked", s.Tenant.Id, actor, caller,
                    new() { ["user_id"] = owner.UserId, ["invite_id"] = revokedId }), ct);
            var inviteId = await s.AddInviteAsync(owner.UserId, invite.Hash, invite.ExpiresAt, CreatedBy(actor), now, ct);
            await s.AddAuditAsync(Audit("owner_invite.issued", s.Tenant.Id, actor, caller,
                new() { ["user_id"] = owner.UserId, ["invite_id"] = inviteId, ["expires_at"] = Iso(invite.ExpiresAt) }), ct);
            return new CpOwnerInviteResponse(InviteUrl(inviteBase, invite.Token), Iso(invite.ExpiresAt));
        }, ct);
        return found ? result : NotFound;
    }

    // ---------- helpers ----------

    /// <summary>404 (без БД для невалідного формату) -> tenant у транзакції -> 422 actor_required -> work.</summary>
    private async Task<AuthResult<T>> InTenantAsync<T>(
        string externalRef, CpActorDto? actorDto, Func<ICpSession, SgActor, Task<AuthResult<T>>> work, CancellationToken ct)
    {
        if (!ExternalRefPattern().IsMatch(externalRef)) return NotFound;
        var (found, result) = await store.WithTenantAsync<AuthResult<T>>(externalRef, async s =>
            ParseActor(actorDto) is { } actor ? await work(s, actor) : ActorRequired, ct);
        return found ? result : NotFound;
    }

    private sealed record SgActor(string Id, string DisplayName);

    private static SgActor? ParseActor(CpActorDto? a)
    {
        var id = a?.Id?.Trim();
        var display = a?.DisplayName?.Trim();
        return string.IsNullOrEmpty(id) || id.Length > 64 || string.IsNullOrEmpty(display) || display.Length > 120 ? null : new SgActor(id, display);
    }

    private static string CreatedBy(SgActor actor) => $"shelfguard:{actor.Id}";

    private static CpAuditEntry Audit(string action, Guid tenantId, SgActor actor, CpCaller caller, Dictionary<string, object?> details) =>
        new(action, ActorType, actor.Id, actor.DisplayName, caller.KeyId, caller.RequestId, caller.Ip, details);

    /// <summary>Час до секунд: відповіді й БД узгоджені (ISO 8601 UTC без дробів).</summary>
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());

    internal static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record NewInviteToken(string Token, byte[] Hash, DateTimeOffset ExpiresAt);

    /// <summary>32 байти CSPRNG, base64url без padding; TTL з конфігу. Токен не зберігається нікуди, окрім відповіді.</summary>
    private NewInviteToken NewInvite(DateTimeOffset now)
    {
        var raw = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new NewInviteToken(token, HashToken(token), now.AddHours(Math.Clamp(options.InviteTtlHours, 1, 24 * 30)));
    }

    /// <summary>Хеш рахується від рядка токена, як його отримає /api/auth/invite/* (TASK-819): sha256(ASCII(token)).</summary>
    public static byte[] HashToken(string token) => SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));

    private string InviteBaseUrl()
    {
        var url = options.PublicAppUrl?.Trim().TrimEnd('/');
        if (!IsAllowedPublicAppUrl(url, options.AllowHttp))
            throw new InvalidOperationException(options.AllowHttp
                ? "ControlPlane:PublicAppUrl must be an absolute http(s) URL to issue owner invites."
                : "ControlPlane:PublicAppUrl must be an absolute https URL to issue owner invites (http is allowed only in Development).");
        return url!;
    }

    /// <summary>L2: https завжди; http лише коли <paramref name="allowHttp"/> (Development).</summary>
    public static bool IsAllowedPublicAppUrl(string? url, bool allowHttp) =>
        !string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || (allowHttp && u.Scheme == Uri.UriSchemeHttp));

    private static string InviteUrl(string baseUrl, string token) => $"{baseUrl}/invite#t={token}";
}
