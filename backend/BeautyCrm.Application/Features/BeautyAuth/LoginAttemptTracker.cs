using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Лічильник невдалих входів за ключем slug+email для НЕіснуючих облікових записів (аудит M3): після N спроб
/// невідомий email отримує ту саму 423 account_locked, що й справжній заблокований акаунт, тож блокування
/// не розкриває існування акаунта. Для наявних користувачів лічильник живе в БД (users.failed_login_count).
/// Пам'ять процесу: у multi-instance кожна інстанція рахує окремо (прийнятний компроміс; спільний лічильник —
/// окрема задача); ключ — SHA-256, відкритий email не зберігається.
/// </summary>
public sealed class LoginAttemptTracker(AuthOptions options)
{
    private const int MaxEntries = 50_000;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? LockedUntil;
    }

    public static string KeyOf(string slug, string normalizedEmail) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{slug}\n{normalizedEmail}")));

    public bool IsLocked(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            if (e.LockedUntil is { } until && until > now) return true;
            if (e.LockedUntil is not null || now - e.LastFailure > TimeSpan.FromMinutes(options.LockoutMinutes))
                _entries.Remove(key); // блокування/вікно спливло: починаємо заново
            return false;
        }
    }

    public void RegisterFailure(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries) Evict(now);
            if (!_entries.TryGetValue(key, out var e)) _entries[key] = e = new Entry();
            e.Failures++;
            e.LastFailure = now;
            if (e.Failures >= options.MaxFailedAttempts) e.LockedUntil = now.AddMinutes(options.LockoutMinutes);
        }
    }

    private void Evict(DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(options.LockoutMinutes);
        foreach (var k in _entries.Where(kv => now - kv.Value.LastFailure > window && (kv.Value.LockedUntil ?? now) <= now)
                     .Select(kv => kv.Key).ToList())
            _entries.Remove(k);
        if (_entries.Count < MaxEntries) return;
        foreach (var k in _entries.OrderBy(kv => kv.Value.LastFailure).Take(MaxEntries / 2).Select(kv => kv.Key).ToList())
            _entries.Remove(k);
    }
}
