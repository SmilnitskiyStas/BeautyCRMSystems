using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>
/// Tenant у виклику не збігається з tenant автентифікованого scope. НЕ є <see cref="InvalidOperationException"/>:
/// <c>AiToolExecutor</c> не перехоплює його як <c>invalid_arguments</c>, запуск переривається (ADR-010 §2.7).
/// Повідомлення фіксоване й не містить id.
/// </summary>
public sealed class TenantMismatchException(Guid scopeTenantId, Guid requestedTenantId)
    : Exception("Tenant mismatch between request scope and AI call.")
{
    public Guid ScopeTenantId { get; } = scopeTenantId;
    public Guid RequestedTenantId { get; } = requestedTenantId;
}

/// <summary>
/// Звернення до даних AI без встановленого tenant scope (L3): помилка програмування шляху, не стан tenant. Не є
/// <see cref="InvalidOperationException"/> і <see cref="AiUnavailableException"/>: ні <c>AiToolExecutor</c>, ні
/// <c>BeautyAssistant</c> її не маскують (ні invalid_arguments, ні передача менеджеру); у HTTP це 500.
/// </summary>
public sealed class TenantScopeMissingException() : Exception("AI data access without an established tenant scope.");

/// <summary>ШІ недоступний для tenant (стан контракту §20.3). <see cref="Reason"/> — код причини.</summary>
public class AiUnavailableException(string reason) : Exception($"AI unavailable: {reason}")
{
    public string Reason { get; } = reason;
}

/// <summary>Вичерпано місячний ліміт (429 ai_quota_exceeded, Retry-After до 1-го числа UTC).</summary>
public sealed class AiQuotaExceededException(DateTimeOffset retryAfter) : AiUnavailableException("quota_exceeded")
{
    public DateTimeOffset RetryAfter { get; } = retryAfter;
}

/// <summary>Перевищено частоту викликів на tenant (429 ai_rate_limited).</summary>
public sealed class AiRateLimitedException() : AiUnavailableException("rate_limited");

public enum AiProviderError
{
    KeyRejected,
    InsufficientPermissions,
    RateLimited,
    Unavailable,
    ModelUnavailable,
    BadRequest,
    BadResponse,
}

/// <summary>Помилка провайдера. Без тіла відповіді, без ключа, без InnerException: лише код і HTTP-статус.</summary>
public sealed class AiProviderException(AiProviderError error, int? httpStatus = null)
    : Exception($"AI provider call failed: {error}{(httpStatus is { } s ? $" (HTTP {s})" : "")}.")
{
    public AiProviderError Error { get; } = error;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>
/// Секрет провайдера (ключ API). <c>ToString()</c> = "***"; публічних властивостей немає, тож серіалізація нічого не віддає.
/// Значення читає лише код адаптерів цієї збірки.
/// </summary>
public sealed class AiSecret
{
    private readonly string _value;

    public AiSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Secret must not be empty.", nameof(value));
        _value = value.Trim();
    }

    internal string Reveal() => _value;

    /// <summary>Префікс SHA-256 (8 hex): для mock-провайдера в тестах ізоляції. Не логувати.</summary>
    internal string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_value)))[..8].ToLowerInvariant();

    public override string ToString() => "***";
}
