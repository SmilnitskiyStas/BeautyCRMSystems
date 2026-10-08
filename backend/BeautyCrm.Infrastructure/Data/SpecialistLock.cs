using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.Data;

/// <summary>
/// Транзакційний advisory-lock на пару (tenant, specialist) — TASK-696. Серіалізує записи, що можуть «розминутися»:
/// створення/перенос запису проти створення/затвердження відсутності (інакше запис, доданий між перевіркою відсутності й
/// її комітом, не потрапив би в conflicts[]), а також видачу запрошень і деактивацію того самого профілю.
/// Тримається до COMMIT/ROLLBACK охоплюючої транзакції (pg_advisory_xact_lock), тож викликати лише всередині неї.
/// Tenant береться з app.tenant_id сесії (RLS), тому ключі різних tenant не перетинаються.
/// </summary>
internal static class SpecialistLock
{
    /// <summary>Блокування запису/відсутностей майстра.</summary>
    public static Task AcquireAsync(BeautyDbContext db, Guid specialistId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(coalesce(current_setting('app.tenant_id', true), '') || ':specialist:' || {specialistId.ToString()}, 0))",
            ct);

    /// <summary>Окремий простір ключів для запрошень майстра (не блокує бронювання).</summary>
    public static Task AcquireForInvitesAsync(BeautyDbContext db, Guid specialistId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended(coalesce(current_setting('app.tenant_id', true), '') || ':invite:' || {specialistId.ToString()}, 0))",
            ct);
}
