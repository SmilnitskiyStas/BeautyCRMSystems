using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Ключ блокування входу (§31.1, TASK-860 F-2): <c>HMAC-SHA256(pepper, email_norm)</c>, 32 байти. Обчислює лише API; пошта й
/// сам ключ у логи не потрапляють. Ротація pepper скидає всі лічильники й блокування (прийнятно).
/// </summary>
public static class LoginKey
{
    public const int MinPepperBytes = 32;

    public static byte[] Compute(string pepper, string normalizedEmail) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes(normalizedEmail));

    public static bool IsValidPepper(string? pepper) => pepper is not null && Encoding.UTF8.GetByteCount(pepper) >= MinPepperBytes;
}

/// <summary>
/// Фіктивний bcrypt-хеш для невідомої або pending пошти (F-13): створюється ТИМ САМИМ <see cref="IPasswordHasher"/> з поточним cost
/// на старті (eager), тож перевірка на цьому шляху коштує стільки ж, скільки справжня, і не додає хешування в запит.
/// </summary>
public sealed class LoginDummyHash(IPasswordHasher hasher)
{
    public string Value { get; } = hasher.Hash(Guid.NewGuid().ToString("N"));
}

/// <summary>Метрики §31.12 без ПІІ: лише bounded-теги <c>outcome</c>. Пошта, slug і ідентифікатори сюди не потрапляють.</summary>
public static class AuthMetrics
{
    public const string MeterName = "BeautyCrm.Auth";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Login = Meter.CreateCounter<long>("auth_login");
    private static readonly Counter<long> LegacyTenant = Meter.CreateCounter<long>("auth_login_legacy_tenant");
    private static readonly Counter<long> Select = Meter.CreateCounter<long>("auth_login_select");
    private static readonly Counter<long> Switch = Meter.CreateCounter<long>("auth_switch");
    private static readonly Counter<long> Lockout = Meter.CreateCounter<long>("auth_lockout");
    private static readonly Counter<long> IdentityCreatedCounter = Meter.CreateCounter<long>("identity_created");
    private static readonly Counter<long> InviteEmailInUseCounter = Meter.CreateCounter<long>("invite_email_in_use");
    private static readonly Counter<long> PasswordForgotCounter = Meter.CreateCounter<long>("auth_password_forgot");

    /// <summary>§31.12: outcome = requested | suppressed (ліміт пошти) | dropped (переповнена черга). Чи існує пошта, метрика не показує.</summary>
    public static void PasswordForgot(string outcome) => PasswordForgotCounter.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>§31.12: нова ідентичність; via = staff_invite | owner_provisioning | platform. Алерт: staff_invite на tenant &gt; 10/добу (tenant-тег не додається: ПІІ немає, але кардинальність обмежуємо; розріз по tenant - з platform_audit_log).</summary>
    public static void IdentityCreated(string via) => IdentityCreatedCounter.Add(1, new KeyValuePair<string, object?>("via", via));

    /// <summary>§31.8.1: запрошення відхилено, бо пошта зайнята (сплеск - алерт devops).</summary>
    public static void InviteEmailInUse() => InviteEmailInUseCounter.Add(1);

    /// <summary>outcome = token | selection | invalid | locked | throttled.</summary>
    public static void LoginOutcome(string outcome) => Login.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    public static void LegacyTenantUsed() => LegacyTenant.Add(1);
    /// <summary>outcome = token | invalid.</summary>
    public static void SelectOutcome(string outcome) => Select.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    /// <summary>outcome = token | same_tenant | invalid_token | not_found.</summary>
    public static void SwitchOutcome(string outcome) => Switch.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
    /// <summary>Кожне досягнення порогу, без розрізнення наявної й невідомої пошти (F-12).</summary>
    public static void LockoutReached() => Lockout.Add(1);
}

/// <summary>
/// §31.9: ліміт перевірок зайнятості пошти при створенні запрошень - 30 / год на tenant (<c>Auth:InviteEmailCheck:PerTenantPerHour</c>),
/// in-memory на інстанс (як OwnerInviteThrottle). Фіксоване вікно; кожен виклик, що дійшов до перевірки, витрачає місце незалежно від результату.
/// Пошта в лімітері не зберігається (ключ - tenant).
/// </summary>
public sealed class InviteEmailCheckThrottle(int permit, TimeSpan window, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (DateTimeOffset Start, int Count)> _windows = [];

    /// <summary>Acquired = false -> перевищено; RetryAfter у секундах (для заголовка Retry-After).</summary>
    public (bool Acquired, int RetryAfter) TryAcquire(Guid tenantId)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_windows.Count > 4096)
                foreach (var k in _windows.Where(w => now - w.Value.Start >= window).Select(w => w.Key).ToList()) _windows.Remove(k);
            if (!_windows.TryGetValue(tenantId, out var w) || now - w.Start >= window) w = (now, 0);
            if (w.Count >= Math.Max(1, permit))
            {
                _windows[tenantId] = w;
                return (false, Math.Max(1, (int)Math.Ceiling((w.Start + window - now).TotalSeconds)));
            }
            _windows[tenantId] = (w.Start, w.Count + 1);
            return (true, 0);
        }
    }
}
