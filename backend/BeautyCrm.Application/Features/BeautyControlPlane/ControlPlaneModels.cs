namespace BeautyCrm.Application.Features.BeautyControlPlane;

// ADR-018, beauty-contracts.md §27: керування бізнесами з ShelfGuard (/api/control/v1/*).

/// <summary>Конфіг <c>ControlPlane:*</c>. PublicAppUrl обов'язковий для видачі запрошень (без нього create/owner-invite падають до запису в БД).</summary>
public sealed class ControlPlaneOptions
{
    public string? PublicAppUrl { get; set; }
    public int InviteTtlHours { get; set; } = 72;
    /// <summary>
    /// TASK-826 (L2): http-схема для PublicAppUrl лише в Development (виставляє Api за IHostEnvironment, не з конфігу).
    /// Інакше токен запрошення у посиланні пішов би відкритим каналом.
    /// </summary>
    public bool AllowHttp { get; set; }
}

// ---------- запити (усі поля nullable: валідація й коди помилок - у сервісі, не в model binding) ----------

public sealed record CpActorDto(string? Id, string? DisplayName);

public sealed record CpOwnerDto(string? Email, string? FullName, string? Password);

public sealed record CpCreateRequest(
    string? ExternalRef, string? Name, string? Slug, IReadOnlyList<string>? Modules, CpOwnerDto? Owner, CpActorDto? Actor);

public sealed record CpSuspendRequest(string? Reason, CpActorDto? Actor);

public sealed record CpActorRequest(CpActorDto? Actor);

public sealed record CpModulesRequest(IReadOnlyList<string>? Modules, CpActorDto? Actor);

// ---------- відповіді (час - ISO 8601 UTC до секунд, рядком) ----------

public sealed record CpOwnerView(Guid UserId, string Email, string State);

public sealed record CpInviteView(string Url, string ExpiresAt);

public sealed record CpCreateResponse(
    Guid TenantId, string ExternalRef, string Slug, string Status, IReadOnlyList<string> Modules, CpOwnerView Owner, CpInviteView? Invite);

/// <summary>Результат create: <see cref="Replayed"/> = ідемпотентний повтор (200 + Idempotent-Replayed), інакше 201.</summary>
public sealed record CpCreateResult(CpCreateResponse Body, bool Replayed);

public sealed record CpOwnerDetails(Guid UserId, string Email, string State, string? InviteExpiresAt);

public sealed record CpTenantResponse(
    Guid TenantId, string ExternalRef, string Slug, string Name, string Status, IReadOnlyList<string> Modules,
    string? SuspendedAt, CpOwnerDetails Owner, string CreatedAt);

public sealed record CpSuspendResponse(string Status, string SuspendedAt);

public sealed record CpResumeResponse(string Status);

public sealed record CpModulesResponse(IReadOnlyList<string> Modules);

public sealed record CpOwnerInviteResponse(string Url, string ExpiresAt);

// ---------- контекст виклику (з HttpContext.Items після ControlPlaneHmac) ----------

/// <summary>Хто викликав на рівні транспорту: kid ключа, X-Request-Id, IP. Актор ShelfGuard - у тілі запиту.</summary>
public sealed record CpCaller(string KeyId, string? RequestId, string? Ip);

// ---------- порт даних ----------

public sealed record CpTenant(
    Guid Id, string ExternalRef, string Slug, string Name, string Status, IReadOnlyList<string> Modules,
    DateTimeOffset? SuspendedAt, DateTimeOffset CreatedAt)
{
    public bool IsSuspended => Status == "suspended";
}

public sealed record CpOwner(Guid UserId, string Email, string State);

public sealed record CpAuditEntry(
    string Action, string ActorType, string? ActorId, string? ActorDisplay, string? KeyId, string? RequestId, string? Ip,
    IReadOnlyDictionary<string, object?> Details);

public sealed record CpNewTenant(Guid Id, string ExternalRef, string Name, string Slug, IReadOnlyList<string> Modules, DateTimeOffset Now);

public sealed record CpNewOwner(string Email, string FullName);

/// <summary>Сесія в межах ОДНІЄЇ транзакції БД, уже прив'язаної до одного tenant (app.tenant_id, set_config(..., true)).</summary>
public interface ICpSession
{
    /// <summary>Стан tenant на момент початку сесії (після блокування рядка).</summary>
    CpTenant Tenant { get; }

    Task<CpOwner?> GetOwnerAsync(CancellationToken ct);

    /// <summary>Кінець дії активного (не використаного, не відкликаного й не простроченого) запрошення власника.</summary>
    Task<DateTimeOffset?> GetActiveInviteExpiryAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>true, якщо стан змінено (active -> suspended, refresh-токени відкликано). false = уже призупинено (noop).</summary>
    Task<bool> SuspendAsync(string reason, DateTimeOffset now, CancellationToken ct);

    /// <summary>true, якщо стан змінено (suspended -> active, suspended_* очищено). false = noop.</summary>
    Task<bool> ResumeAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>true, якщо набір модулів змінився (порівняння як множин). false = noop.</summary>
    Task<bool> SetModulesAsync(IReadOnlyList<string> modules, DateTimeOffset now, CancellationToken ct);

    /// <summary>Відкликає активні запрошення користувача; повертає id відкликаних.</summary>
    Task<IReadOnlyList<Guid>> RevokeActiveInvitesAsync(Guid userId, DateTimeOffset now, CancellationToken ct);

    /// <summary>У БД лише sha256 (32 байти) токена; сам токен порт не бачить.</summary>
    Task<Guid> AddInviteAsync(Guid userId, byte[] tokenHash, DateTimeOffset expiresAt, string createdBy, DateTimeOffset now, CancellationToken ct);

    Task AddAuditAsync(CpAuditEntry entry, CancellationToken ct);
}

public interface IControlPlaneStore
{
    /// <summary>
    /// Транзакція: lookup за external_ref (вузька політика tenants_control_lookup) -> app.tenant_id -> блокування рядка tenant ->
    /// <paramref name="work"/> -> commit. Tenant не знайдено = (false, default); <paramref name="work"/> не викликається.
    /// </summary>
    Task<(bool Found, T? Value)> WithTenantAsync<T>(string externalRef, Func<ICpSession, Task<T>> work, CancellationToken ct);

    /// <summary>
    /// Створює tenant + власника (state=invited, без пароля) і викликає <paramref name="inSameTransaction"/> (запрошення, аудит) в тій
    /// самій транзакції. false = порушення унікальності (external_ref або slug): нічого не записано, потрібен повторний lookup.
    /// </summary>
    Task<bool> TryCreateAsync(CpNewTenant tenant, CpNewOwner owner, Func<ICpSession, Task> inSameTransaction, CancellationToken ct);
}
