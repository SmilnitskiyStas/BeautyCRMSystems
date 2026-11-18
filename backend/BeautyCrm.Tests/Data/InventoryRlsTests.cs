using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-876 (§32.3, ADR-025): 9 таблиць складу. FORCE RLS + політики, ізоляція tenant, порожній вихід без контексту,
/// WITH CHECK на чужий tenant_id, композитні FK (крос-tenant), унікальність штрихкоду й назви постачальника, відображення сутностей EF.
/// Без реального годинника: усі часові значення задаються тестом.
/// </summary>
public sealed class InventoryRlsTests : IClassFixture<PostgresRlsFixture>
{
    private static readonly string[] Tables =
    [
        "beauty_suppliers", "beauty_products", "beauty_product_location_settings", "beauty_stock_levels", "beauty_stock_operations",
        "beauty_stock_batches", "beauty_stock_movements", "beauty_stocktakes", "beauty_stocktake_lines",
    ];

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresRlsFixture _db;

    public InventoryRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId, Guid LocationA, Guid LocationB, Guid SupplierId, Guid ProductId,
        Guid OperationId, Guid BatchId, Guid StocktakeId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $@"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'inv-{tenantId:N}');
                   INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                   VALUES ('{userId}', '{tenantId}', 'u@{tenantId:N}.test', 'Owner', 'x', 'owner');", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var ctx = _db.CreateContext(tenantId);
        var a = new Location { Name = "A", Timezone = "Europe/Kyiv" };
        var b = new Location { Name = "B", Timezone = "Europe/Kyiv" };
        var supplier = new Supplier { Name = "Постачальник", Email = "s@example.test" };
        ctx.AddRange(a, b, supplier);
        await ctx.SaveChangesAsync();

        var product = new Product
        {
            Name = "Шампунь", Category = "Догляд", Unit = ProductUnit.Ml, Barcode = "4820000000012", Purpose = ProductPurpose.SalonUse,
            SupplierId = supplier.Id, PurchasePrice = 123.45m, MinStock = 500.5m, TrackExpiry = true, UpdatedByUserId = userId,
        };
        ctx.Add(product);
        await ctx.SaveChangesAsync();

        var op = new StockOperation
        {
            Kind = StockOperationKind.Receipt, LocationId = a.Id, SupplierId = supplier.Id, DocumentNumber = "НК-1",
            ReceivedOn = new DateOnly(2026, 11, 17), CreatedByUserId = userId, CreatedAt = T0,
            IdempotencyKeyHash = new string('a', 64), IdempotencyRequestHash = new string('b', 64),
        };
        ctx.Add(op);
        await ctx.SaveChangesAsync();

        var batch = new StockBatch
        {
            LocationId = a.Id, ProductId = product.Id, OperationId = op.Id, SupplierId = supplier.Id, ReceivedAt = T0,
            ExpiresOn = new DateOnly(2027, 5, 1), LotNumber = "L1", InitialQuantity = 1000.125m, Quantity = 1000.125m, UnitCost = 0.12m,
        };
        ctx.Add(batch);
        await ctx.SaveChangesAsync();

        var stocktake = new Stocktake { LocationId = a.Id, CreatedByUserId = userId, CreatedAt = T0 };
        ctx.Add(stocktake);
        await ctx.SaveChangesAsync();

        ctx.AddRange(
            new StockMovement
            {
                OperationId = op.Id, LocationId = a.Id, ProductId = product.Id, BatchId = batch.Id, Type = StockMovementType.Receipt,
                Quantity = 1000.125m, UnitCost = 0.12m, CreatedByUserId = userId, OccurredAt = T0,
            },
            new StockLevel { LocationId = a.Id, ProductId = product.Id, Quantity = 1000.125m },
            new ProductLocationSetting { ProductId = product.Id, LocationId = a.Id, MinStock = 0m, UpdatedByUserId = userId },
            new StocktakeLine { StocktakeId = stocktake.Id, ProductId = product.Id, CountedQuantity = 990m, SurplusExpiresOn = new DateOnly(2027, 6, 1) });
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, userId, a.Id, b.Id, supplier.Id, product.Id, op.Id, batch.Id, stocktake.Id);
    }

    private async Task Rejects(Guid? tenant, string sql, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(tenant, sql));
        Assert.Equal(state, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
    }

    private async Task<int> Run(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(Guid? tenant, string table)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        return await cmd.ExecuteScalarAsync();
    }

    [SkippableFact]
    public async Task force_rls_and_tenant_isolation_policies_exist_on_all_nine_tables()
    {
        SkipIfNoDb();
        foreach (var table in Tables)
        {
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation'
                AND qual IS NOT NULL AND with_check IS NOT NULL"));
        }
    }

    [SkippableFact]
    public async Task entities_round_trip_through_ef_with_enums_dates_and_decimals()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(s.TenantId);

        var p = await ctx.Products.SingleAsync(x => x.Id == s.ProductId);
        Assert.Equal(ProductUnit.Ml, p.Unit);
        Assert.Equal(ProductPurpose.SalonUse, p.Purpose);
        Assert.Equal(123.45m, p.PurchasePrice);
        Assert.Equal(500.5m, p.MinStock);
        Assert.True(p.TrackExpiry);
        Assert.True(p.IsActive);

        var op = await ctx.StockOperations.SingleAsync(x => x.Id == s.OperationId);
        Assert.Equal(StockOperationKind.Receipt, op.Kind);
        Assert.Null(op.Reason);
        Assert.Equal(new DateOnly(2026, 11, 17), op.ReceivedOn);
        Assert.Equal(T0, op.CreatedAt);

        var batch = await ctx.StockBatches.SingleAsync(x => x.Id == s.BatchId);
        Assert.Equal(new DateOnly(2027, 5, 1), batch.ExpiresOn);
        Assert.Equal(1000.125m, batch.Quantity);

        var mv = await ctx.StockMovements.SingleAsync(x => x.OperationId == s.OperationId);
        Assert.Equal(StockMovementType.Receipt, mv.Type);
        Assert.NotEqual(default, mv.OccurredAt);

        var st = await ctx.Stocktakes.SingleAsync(x => x.Id == s.StocktakeId);
        Assert.Equal(StocktakeStatus.Draft, st.Status);
        Assert.Equal(1, st.Version);

        // write_off з причиною: enum <-> текст
        ctx.Add(new StockOperation { Kind = StockOperationKind.WriteOff, LocationId = s.LocationA, Reason = WriteOffReason.InternalUse, CreatedByUserId = s.UserId });
        await ctx.SaveChangesAsync();
        Assert.Equal(1L, (long)(await Scalar($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{s.TenantId}' AND kind = 'write_off' AND reason = 'internal_use'"))!);
    }

    [SkippableFact]
    public async Task tenants_are_isolated_and_nothing_is_visible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        foreach (var table in Tables)
        {
            Assert.Equal(1L, await Count(a.TenantId, table));
            Assert.Equal(1L, await Count(b.TenantId, table));
            Assert.Equal(0L, await Count(null, table));
            Assert.Equal(0L, await Count(Guid.NewGuid(), table));
        }
        // чужий рядок не можна змінити/видалити
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_products SET name = 'x' WHERE id = '{a.ProductId}'"));
        Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM beauty_stock_levels WHERE tenant_id = '{a.TenantId}'"));
    }

    [SkippableFact]
    public async Task with_check_rejects_rows_for_another_tenant_on_every_table()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        // tenant B пише рядки з tenant_id A (FK на сутності A ідуть через композитний ключ A, тож WITH CHECK спрацьовує першим/окремо)
        await Rejects(b.TenantId, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{a.TenantId}', 'X')", "42501");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id)
            VALUES ('{a.TenantId}', 'X', 'pcs', 'sale', '{a.UserId}')", "42501");
        await Rejects(b.TenantId, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{a.TenantId}', '{a.LocationB}', '{a.ProductId}')", "42501");
        await Rejects(b.TenantId, $"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, created_by_user_id) VALUES ('{a.TenantId}', 'receipt', '{a.LocationA}', '{a.UserId}')", "42501");
        await Rejects(b.TenantId, $"INSERT INTO beauty_stocktakes (tenant_id, location_id, created_by_user_id) VALUES ('{a.TenantId}', '{a.LocationB}', '{a.UserId}')", "42501");
        // без контексту запис неможливий
        await Rejects(null, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{a.TenantId}', 'X')", "42501");
        // свій tenant - ок
        Assert.Equal(1, await Run(b.TenantId, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{b.TenantId}', 'Other')"));
    }

    [SkippableFact]
    public async Task composite_foreign_keys_block_cross_tenant_references()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        // tenant B посилається на товар / заклад / операцію / партію / інвентаризацію tenant A
        await Rejects(b.TenantId, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{b.TenantId}', '{b.LocationA}', '{a.ProductId}')", "23503");
        await Rejects(b.TenantId, $"INSERT INTO beauty_stock_levels (tenant_id, location_id, product_id) VALUES ('{b.TenantId}', '{a.LocationA}', '{b.ProductId}')", "23503");
        await Rejects(b.TenantId, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, supplier_id, updated_by_user_id) VALUES ('{b.TenantId}', 'X', 'pcs', 'sale', '{a.SupplierId}', '{b.UserId}')", "23503");
        await Rejects(b.TenantId, $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{b.TenantId}', 'X', 'pcs', 'sale', '{a.UserId}')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_stock_batches (tenant_id, location_id, product_id, operation_id, received_at, initial_quantity, quantity)
            VALUES ('{b.TenantId}', '{b.LocationA}', '{b.ProductId}', '{a.OperationId}', now(), 1, 1)", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_stock_movements (tenant_id, operation_id, location_id, product_id, batch_id, type, quantity)
            VALUES ('{b.TenantId}', '{b.OperationId}', '{b.LocationA}', '{b.ProductId}', '{a.BatchId}', 'receipt', 1)", "23503");
        await Rejects(b.TenantId, $"INSERT INTO beauty_stocktake_lines (tenant_id, stocktake_id, product_id) VALUES ('{b.TenantId}', '{a.StocktakeId}', '{b.ProductId}')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, to_location_id, created_by_user_id)
            VALUES ('{b.TenantId}', 'transfer', '{b.LocationA}', '{a.LocationB}', '{b.UserId}')", "23503");
        // RESTRICT: товар з рівнем/партією не видаляється; налаштування закладу зникають разом з товаром лише без решти історії
        await Rejects(a.TenantId, $"DELETE FROM beauty_products WHERE id = '{a.ProductId}'", "23503");
        await Rejects(a.TenantId, $"DELETE FROM beauty_suppliers WHERE id = '{a.SupplierId}'", "23503");
        // CASCADE: рядки інвентаризації видаляються разом з чернеткою
        Assert.Equal(1, await Run(a.TenantId, $"DELETE FROM beauty_stocktakes WHERE id = '{a.StocktakeId}'"));
        Assert.Equal(0L, await Count(a.TenantId, "beauty_stocktake_lines"));
    }

    [SkippableFact]
    public async Task barcode_is_unique_within_tenant_only_when_present_and_supplier_name_is_case_insensitive()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        string Product(Seed s, string? barcode) =>
            $"INSERT INTO beauty_products (tenant_id, name, unit, purpose, barcode, updated_by_user_id) VALUES ('{s.TenantId}', 'P', 'pcs', 'both', {(barcode is null ? "NULL" : $"'{barcode}'")}, '{s.UserId}')";

        // той самий штрихкод в межах tenant (навіть для неактивного) - конфлікт
        await Rejects(a.TenantId, Product(a, "4820000000012"), "23505", "ux_beauty_products_barcode");
        await Run(a.TenantId, $"UPDATE beauty_products SET is_active = false WHERE id = '{a.ProductId}'");
        await Rejects(a.TenantId, Product(a, "4820000000012"), "23505", "ux_beauty_products_barcode");
        // інший tenant - можна; NULL штрихкоди не конфліктують
        Assert.Equal(1, await Run(b.TenantId, Product(b, "4820000000099")));
        await Run(a.TenantId, Product(a, null));
        await Run(a.TenantId, Product(a, null));
        // формат штрихкоду
        await Rejects(a.TenantId, Product(a, "bad barcode"), "23514", "ck_beauty_products_barcode");

        await Rejects(a.TenantId, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{a.TenantId}', 'ПОСТАЧАЛЬНИК')", "23505", "ux_beauty_suppliers_name");
        Assert.Equal(1, await Run(a.TenantId, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{a.TenantId}', 'Унікальний')"));
        Assert.Equal(1, await Run(b.TenantId, $"INSERT INTO beauty_suppliers (tenant_id, name) VALUES ('{b.TenantId}', 'УНІКАЛЬНИЙ')")); // інший tenant - можна
    }
}
