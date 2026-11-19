using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

// Автосписання матеріалів при завершенні візиту (§32.18, TASK-885). Викликається з CompleteWithCostSnapshotAsync у ТІЙ САМІЙ транзакції
// після умовного UPDATE статусу й ClosePlanAsync. Нестача НІКОЛИ не блокує завершення; будь-яка помилка кроку відкочує лише SAVEPOINT.
public sealed partial class EfBeautyStore
{
    public const string AutoConsumptionKey = "Inventory:AutoConsumptionEnabled";
    private const string ConsumptionSavepoint = "inventory_consumption";

    /// <summary>Security-review L1: порожнє/відсутнє = увімкнено (за замовчуванням); будь-яке інше значення, крім <c>true</c> (без регістру), вимикає (fail-safe для 0/off/no).</summary>
    private bool AutoConsumptionEnabled => IsAutoConsumptionOn(config?[AutoConsumptionKey]);

    public static bool IsAutoConsumptionOn(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Security-review M1: найдовше очікування блокування рядка (рівень/товар) у складському кроці; далі 55P03 поглинає SAVEPOINT.</summary>
    private const string ConsumptionLockTimeout = "2000ms";

    private void LogConsumptionSkipped(Guid appointmentId, string reason) =>
        log?.LogInformation("inventory_consumption_skipped appointment={Appointment} reason={Reason}", appointmentId, reason);

    // ІНВАРІАНТ (security-review I5): складський крок працює лише через raw SQL / ExecuteUpdate та власні tracked-сутності кроку. До його початку
    // у ChangeTracker не має бути незбережених змін: SaveChangesAsync кроку зберіг би їх, а ChangeTracker.Clear() у catch - відкинув би.
    // CancellationToken: крок і його SAVEPOINT-команди виконуються з CancellationToken.None - після умовного UPDATE статусу скасування HTTP-запиту
    // не повинно відкочувати completed + знімок ADR-014 (§32.18 «візит завершується завжди»). Тривалість обмежена lock_timeout (+ CommandTimeout).
    private async Task ConsumeMaterialsAsync(IDbContextTransaction tx, Guid appointmentId)
    {
        var ct = CancellationToken.None;
        if (!AutoConsumptionEnabled) { LogConsumptionSkipped(appointmentId, "disabled"); return; }

        // SAVEPOINT береться ДО читань кроку: помилка будь-якого запиту (транзакція Postgres стає aborted) відкочує лише складський крок
        await tx.CreateSavepointAsync(ConsumptionSavepoint, ct);
        try
        {
            // одразу після SAVEPOINT: SET LOCAL відкочується разом із ним; 55P03 (lock_not_available) -> лог Error, візит лишається completed
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('lock_timeout', {ConsumptionLockTimeout}, true)", ct);
            await ConsumeMaterialsCoreAsync(appointmentId, ct);
            await tx.ReleaseSavepointAsync(ConsumptionSavepoint, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            await tx.RollbackToSavepointAsync(ConsumptionSavepoint, ct);
            var sqlState = (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState ?? "-";
            log?.LogError(ex, "inventory_consumption_failed appointment={Appointment} sqlstate={SqlState}", appointmentId, sqlState);
        }
    }

    private static bool Eligible(Product p) => p.IsActive && p.Purpose is ProductPurpose.SalonUse or ProductPurpose.Both;

    private async Task ConsumeMaterialsCoreAsync(Guid appointmentId, CancellationToken ct)
    {
        // 2. модуль вимкнено для tenant -> нічого (читання в межах транзакції; tenants видно лише власний рядок через RLS)
        if (!await InventoryModuleGate.IsEnabledAsync(db, ct))
        { LogConsumptionSkipped(appointmentId, "module_off"); return; }

        var appt = await db.Appointments.AsNoTracking().Where(a => a.Id == appointmentId).Select(a => new { a.ServiceId, a.LocationId }).SingleAsync(ct);

        // 3. ефективний набір: заклад замінює мережу ЦІЛКОМ (порожній override = нічого не списувати)
        var sets = await db.ServiceMaterialSets.AsNoTracking()
            .Where(x => x.ServiceId == appt.ServiceId && (x.LocationId == appt.LocationId || x.LocationId == null)).ToListAsync(ct);
        var set = sets.FirstOrDefault(x => x.LocationId == appt.LocationId) ?? sets.FirstOrDefault(x => x.LocationId == null);
        var norms = set is null ? [] : await db.ServiceMaterials.AsNoTracking().Where(m => m.SetId == set.Id).ToListAsync(ct);
        if (norms.Count == 0) { LogConsumptionSkipped(appointmentId, "no_norms"); return; }

        // 4. попередній відбір (без блокувань): активний товар для салону, не вимкнений у закладі
        var pids = norms.Select(n => n.ProductId).ToList();
        var candidates = await db.Products.AsNoTracking().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var disabled0 = await db.ProductLocationSettings.AsNoTracking()
            .Where(s => s.LocationId == appt.LocationId && pids.Contains(s.ProductId) && !s.IsEnabled).Select(s => s.ProductId).ToListAsync(ct);
        var wanted = norms.Where(n => candidates.TryGetValue(n.ProductId, out var p) && Eligible(p) && !disabled0.Contains(p.Id)).OrderBy(n => n.ProductId).ToList();
        if (wanted.Count == 0) { LogConsumptionSkipped(appointmentId, "no_eligible_products"); return; }

        // 6 (швидка перевірка). Списання цього візиту вже було (статус повернули й повторили completed) -> нічого; авторитетна перевірка - INSERT ON CONFLICT нижче
        if (await db.StockOperations.AsNoTracking().AnyAsync(o => o.AppointmentId == appointmentId, ct)) { LogConsumptionSkipped(appointmentId, "already_consumed"); return; }

        // 7. блокування як у етапі 1: рівні в порядку (location, product), далі товари FOR SHARE; перевірка під блокуванням
        var levels = await LockLevelsAsync(wanted.Select(n => (appt.LocationId, n.ProductId)), ct);
        var created = (clock ?? TimeProvider.System).GetUtcNow();
        var now = LockedNow(created);
        var products = await LockProductsAsync(wanted.Select(n => n.ProductId), ct);
        var enabled = await LockEnabledAsync(appt.LocationId, wanted.Select(n => n.ProductId), ct);
        var lines = new List<StockLine>();
        foreach (var n in wanted)
            if (products.TryGetValue(n.ProductId, out var p) && Eligible(p) && (!enabled.TryGetValue(p.Id, out var en) || en))
                lines.Add(new StockLine(lines.Count, p.Id, n.Quantity, null));
        if (lines.Count == 0) { LogConsumptionSkipped(appointmentId, "no_eligible_products"); return; }

        // 6. заголовок операції (система = created_by NULL); UNIQUE (tenant, appointment) = ідемпотентність
        var opId = Guid.NewGuid();
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO beauty_stock_operations (id, tenant_id, kind, location_id, appointment_id, created_by_user_id, created_at)
            VALUES ({opId}, NULLIF(current_setting('app.tenant_id', true), '')::uuid, 'service_consumption', {appt.LocationId}, {appointmentId}, NULL, {now})
            ON CONFLICT (tenant_id, appointment_id) WHERE appointment_id IS NOT NULL DO NOTHING", ct);
        if (inserted == 0) { LogConsumptionSkipped(appointmentId, "already_consumed"); return; }

        // 8. FEFO без прострочених за ЛОКАЛЬНОЮ датою закладу на момент завершення; ядро розподілу - FefoPlanner (як списання етапу 1)
        var tz = await db.Locations.AsNoTracking().Where(l => l.Id == appt.LocationId).Select(l => l.Timezone).SingleAsync(ct);
        var today = SlotCalculator.LocalDate(now, tz);
        var lineProducts = lines.Select(l => l.ProductId).ToList();
        var batches = await db.StockBatches.Where(b => b.LocationId == appt.LocationId && lineProducts.Contains(b.ProductId) && b.Quantity > 0).ToDictionaryAsync(b => b.Id, ct);
        var plan = FefoPlanner.Allocate(lines, [.. batches.Values.Select(ToRow)], skipExpired: true, today, allowPartial: true);

        var op = new StockOperation { Id = opId }; // лише ідентифікатор для рухів (рядок уже вставлено)
        foreach (var t in plan.Takes)
        {
            batches[t.BatchId].Quantity -= t.Quantity;
            levels[(appt.LocationId, t.ProductId)].Quantity -= t.Quantity;
            Move(op, appt.LocationId, t.ProductId, t.BatchId, StockMovementType.ServiceConsumption, -t.Quantity, t.UnitCost, null, now);
        }
        // 9. непокрита частина -> рух нестачі без партії, shortage_quantity, алерт (dedupe shortage:{op}:{product})
        foreach (var s in plan.Shortages)
        {
            var u = s.Requested - s.Available;
            if (u <= 0) continue;
            Move(op, appt.LocationId, s.ProductId, null, StockMovementType.ServiceConsumption, -u, null, null, now, isShortage: true);
            levels[(appt.LocationId, s.ProductId)].ShortageQuantity += u;
            db.StaffAlerts.Add(new StaffAlert
            {
                Id = Guid.NewGuid(), Kind = "shortage", LocationId = appt.LocationId, ProductId = s.ProductId, OperationId = opId, Quantity = u,
                DedupeKey = $"shortage:{opId}:{s.ProductId}", Status = "open", CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        log?.LogInformation("inventory_consumption appointment={Appointment} op={Operation} location={Location} products={Products} shortages={Shortages}",
            appointmentId, opId, appt.LocationId, lines.Count, plan.Shortages.Count);
    }
}
