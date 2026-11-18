using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Порт скидання пароля (§31.7, §31.16): тонкі виклики <c>auth_password_reset_issue</c> / <c>auth_password_reset_consume</c>.
/// Жодного методу, що повертає токен, хеш чи пошту: токен генерує воркер, API бачить лише сирий токен від користувача при reset.
/// </summary>
public interface IPasswordResetStore
{
    /// <summary><c>void</c>: для невідомої чи pending пошти нічого не робить (F-3).</summary>
    Task IssueAsync(string normalizedEmail, byte[] loginKey, DateTimeOffset expiresAt, CancellationToken ct);
    /// <summary>identity_id або null (усі причини непридатного токена).</summary>
    Task<Guid?> ConsumeAsync(byte[] tokenHash, string newPasswordHash, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// Обмежена in-process черга запитів <c>forgot</c> (F-3): синхронний шлях запиту лише кладе нормалізовану пошту й повертає 202,
/// не торкаючись БД. Переповнення = запит відкидається (метрика dropped). Втрата черги при рестарті прийнятна.
/// </summary>
public sealed class PasswordForgotQueue
{
    private readonly Channel<string> _channel;

    public PasswordForgotQueue(int capacity) =>
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
        });

    public bool TryEnqueue(string normalizedEmail) => _channel.Writer.TryWrite(normalizedEmail);
    public ChannelReader<string> Reader => _channel.Reader;
}

/// <summary>§31.9: 3 / год на sha256(пошти), in-memory. Сирої пошти в пам'яті лімітера немає. Фіксоване вікно.</summary>
public sealed class PasswordForgotEmailThrottle(int permit, TimeSpan window, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> _windows = [];

    public bool TryAcquire(string normalizedEmail)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedEmail)));
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_windows.Count > 8192)
                foreach (var k in _windows.Where(w => now - w.Value.Start >= window).Select(w => w.Key).ToList()) _windows.Remove(k);
            if (!_windows.TryGetValue(key, out var w) || now - w.Start >= window) w = (now, 0);
            if (w.Count >= Math.Max(1, permit))
            {
                _windows[key] = w;
                return false;
            }
            _windows[key] = (w.Start, w.Count + 1);
            return true;
        }
    }
}

/// <summary>
/// Скидання пароля (§31.7). <see cref="Forgot"/> синхронний і не має жодної залежності від БД: це те, що гарантує однаковий час
/// відповіді для наявної й невідомої пошти. Нічого не логує (пошта, токен, пароль).
/// </summary>
public sealed class PasswordResetService(
    IPasswordResetStore store, PasswordForgotQueue queue, PasswordForgotEmailThrottle emailThrottle, IPasswordHasher hasher,
    AuthOptions options, TimeProvider clock)
{
    private static readonly AuthError ResetInvalid = AuthError.Gone("reset_invalid", "Reset link is invalid or expired.");
    private static readonly AuthError InvalidEmail = AuthError.Validation("invalid_email", "Email is invalid.");

    /// <summary>Ok = завжди 202 (наявна, невідома, ліміт пошти, переповнена черга); помилка лише для синтаксично невалідної пошти.</summary>
    public AuthResult<bool> Forgot(string? email)
    {
        if (email is null || email.Length > 320 || !new EmailAddressAttribute().IsValid(email.Trim())) return InvalidEmail;
        var normalized = AuthService.NormalizeEmail(email);
        if (!emailThrottle.TryAcquire(normalized))
        {
            AuthMetrics.PasswordForgot("suppressed");
            return true;
        }
        AuthMetrics.PasswordForgot(queue.TryEnqueue(normalized) ? "requested" : "dropped");
        return true;
    }

    /// <summary>§31.7.2: weak_password (токен не споживається) -> формат токена -> consume. Усі причини непридатного токена = 410.</summary>
    public async Task<AuthResult<bool>> ResetAsync(ResetPasswordRequest req, CancellationToken ct)
    {
        if (PasswordRules.Check(req.Password, options.MinPasswordLength) is { } weak) return weak;
        if (AuthService.DecodeTicket(req.Token) is not { } raw) return ResetInvalid;
        var identity = await store.ConsumeAsync(SHA256.HashData(raw), hasher.Hash(req.Password!), clock.GetUtcNow(), ct);
        return identity is null ? ResetInvalid : true;
    }
}

/// <summary>Обробник черги forgot: один виклик <c>auth_password_reset_issue</c> на запит. Помилки логуються без пошти.</summary>
public sealed class PasswordForgotProcessor(IPasswordResetStore store, AuthOptions options, TimeProvider clock, ILogger<PasswordForgotProcessor> logger)
{
    public async Task ProcessAsync(string normalizedEmail, CancellationToken ct)
    {
        try
        {
            var key = LoginKey.Compute(options.LoginAttemptKeyPepper ?? throw new InvalidOperationException("Auth:LoginAttemptKeyPepper is not configured."), normalizedEmail);
            var minutes = Math.Clamp(options.PasswordResetMinutes, 10, 120);
            await store.IssueAsync(normalizedEmail, key, clock.GetUtcNow().AddMinutes(minutes), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError("auth_password_forgot_issue_failed {ErrorType}", ex.GetType().Name); // без пошти й тексту винятку
        }
    }
}
