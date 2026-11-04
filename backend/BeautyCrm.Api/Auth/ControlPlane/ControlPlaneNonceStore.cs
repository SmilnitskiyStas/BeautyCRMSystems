using BeautyCrm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>Реєстр використаних nonce (control_plane_nonces, ADR-018 §4). Викликається ЛИШЕ після валідного підпису.</summary>
public interface IControlPlaneNonceStore
{
    /// <summary>true — nonce новий і записаний; false — replay (вже є для цього kid).</summary>
    Task<bool> TryRegisterAsync(string kid, string nonce, CancellationToken ct);
}

public sealed class EfControlPlaneNonceStore(BeautyDbContext db) : IControlPlaneNonceStore
{
    public async Task<bool> TryRegisterAsync(string kid, string nonce, CancellationToken ct)
    {
        // seen_at = now() (default колонки). Очищення записів старших за 15 хв робить worker (TASK-821); вікно підпису 300 с
        // гарантує, що nonce, який уже видалено, не пройде перевірку timestamp.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO control_plane_nonces (key_id, nonce) VALUES ({kid}, {nonce}) ON CONFLICT DO NOTHING", ct);
        return rows == 1;
    }
}
