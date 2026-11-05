using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;

namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>
/// Запис <c>control_auth.failed</c> у platform_audit_log (TASK-826, M2) з агрегацією: не частіше 1 запису на хвилину
/// на пару (IP, причина); пропущені в межах вікна невдачі додаються лічильником <c>suppressed</c> до наступного запису.
/// Details: лише причина, kid (якщо формат валідний і відомий) і лічильник; жодних підписів, секретів, тіла, nonce.
/// Пам'ять обмежена (<see cref="MaxKeys"/> ключів): при переповненні нові ключі не записуються (атака не роздуває ні RAM, ні таблицю).
/// </summary>
public sealed class ControlPlaneFailureAudit(TimeProvider time)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public const int MaxKeys = 10_000;

    private sealed class Slot { public DateTimeOffset LastWritten; public int Suppressed; }
    private readonly ConcurrentDictionary<(string Ip, string Reason), Slot> _slots = new();

    /// <summary>true - запис потрібен зараз; suppressed - скільки невдач пропущено з попереднього запису.</summary>
    public bool TryAcquire(IPAddress? ip, string reason, out int suppressed)
    {
        var now = time.GetUtcNow();
        var key = (ip?.ToString() ?? "unknown", reason);
        suppressed = 0;
        if (_slots.Count >= MaxKeys) Evict(now);
        if (_slots.Count >= MaxKeys && !_slots.ContainsKey(key)) return false;

        var slot = _slots.GetOrAdd(key, _ => new Slot { LastWritten = DateTimeOffset.MinValue });
        lock (slot)
        {
            if (now - slot.LastWritten < Window) { slot.Suppressed++; return false; }
            suppressed = slot.Suppressed;
            slot.Suppressed = 0;
            slot.LastWritten = now;
            return true;
        }
    }

    private void Evict(DateTimeOffset now)
    {
        foreach (var (k, s) in _slots)
            if (now - s.LastWritten >= Window && s.Suppressed == 0) _slots.TryRemove(k, out _);
    }

    public async Task RecordAsync(BeautyDbContext db, IPAddress? ip, string reason, string? kid, int suppressed, CancellationToken ct)
    {
        var details = new Dictionary<string, object> { ["reason"] = reason };
        if (kid is not null) details["kid"] = kid;
        if (suppressed > 0) details["suppressed"] = suppressed;
        db.PlatformAuditLog.Add(new PlatformAuditRow
        {
            Action = "control_auth.failed", ActorType = "system", KeyId = kid,
            Ip = ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip,
            Details = JsonSerializer.Serialize(details),
        });
        await db.SaveChangesAsync(ct);
    }
}
