using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-883 (§32.16-§32.19, ADR-025 §11): нові таблиці етапу 2a під FORCE RLS (норми, рядки норм, сповіщення), ізоляція tenant,
/// WITH CHECK, композитні FK між tenant (зокрема операція -> запис і сповіщення -> партія/операція), відображення нових полів EF.
/// Без реального годинника: усі часові значення задаються тестом.
/// </summary>
public sealed class InventoryStage2aRlsTests : IClassFixture<PostgresRlsFixture>
{
    private static readonly string[] Tables = ["beauty_service_material_sets", "beauty_service_materials", "beauty_staff_alerts"];

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresRlsFixture _db;

    public InventoryStage2aRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId, Guid Location, Guid Service, Guid Appointment, Guid Product, Guid Operation,
        Guid Batch, Guid ConsumptionOperation, Guid SetId, Guid AlertId);

    private async Task<Seed> SeedTenantAsync()
    {
        var t = Guid.NewGuid();
        var user = Guid.NewGuid();
        Guid loc, spec, svc, client, appt, product, op;
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            async Task Exec(string sql) { await using var c = new NpgsqlCommand(sql, admin); await c.ExecuteNonQueryAsync(); }
            async Task<Guid> Id(string sql) { await using var c = new NpgsqlCommand(sql, admin); return (Guid)(await c.ExecuteScalarAsync())!; }
            await Exec($@"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'inv2a-{t:N}');
                INSERT INTO users (id, tenant_id, email, full_name, password_hash, role) VALUES ('{user}', '{t}', 'u@{t:N}.test', 'Owner', 'x', 'owner');");
            loc = await Id($"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{t}', 'A', 'Europe/Kyiv', true) RETURNING id");
            spec = await Id($"INSERT INTO beauty_specialists (tenant_id, full_name, is_active) VALUES ('{t}', 'S', true) RETURNING id");
            svc = await Id($"INSERT INTO beauty_services (tenant_id, name, duration_minutes, is_active) VALUES ('{t}', 'Svc', 60, true) RETURNING id");
            client = await Id($"INSERT INTO beauty_clients (tenant_id, full_name) VALUES ('{t}', 'C') RETURNING id");
            appt = await Id($@"INSERT INTO beauty_appointments (tenant_id, location_id, specialist_id, service_id, client_id, starts_at, duration_minutes, status, source, price_original, price_final)
                VALUES ('{t}', '{loc}', '{spec}', '{svc}', '{client}', TIMESTAMPTZ '2026-11-20 09:00+00', 60, 'pending', 'admin', 200, 150) RETURNING id");
            product = await Id($"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{t}', 'Шампунь', 'ml', 'salon_use', '{user}') RETURNING id");
            op = await Id($"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, created_by_user_id) VALUES ('{t}', 'receipt', '{loc}', '{user}') RETURNING id");
        }

        await using var ctx = _db.CreateContext(t);
        var batch = new StockBatch
        {
            LocationId = loc, ProductId = product, OperationId = op, ReceivedAt = T0, ExpiresOn = new DateOnly(2027, 1, 31),
            InitialQuantity = 10m, Quantity = 10m,
        };
        var consumption = new StockOperation { Kind = StockOperationKind.ServiceConsumption, LocationId = loc, AppointmentId = appt, CreatedAt = T0 };
        var set = new ServiceMaterialSet { ServiceId = svc, UpdatedByUserId = user };
        ctx.AddRange(batch, consumption, set);
        await ctx.SaveChangesAsync();

        var alert = new StaffAlert
        {
            Kind = "shortage", LocationId = loc, ProductId = product, OperationId = consumption.Id, Quantity = 2.5m,
            DedupeKey = $"shortage:{consumption.Id}:{product}",
        };
        ctx.AddRange(
            alert,
            new ServiceMaterial { SetId = set.Id, ProductId = product, Quantity = 12.5m },
            new StockLevel { LocationId = loc, ProductId = product, Quantity = 10m, ShortageQuantity = 2.5m },
            new StockMovement
            {
                OperationId = consumption.Id, LocationId = loc, ProductId = product, BatchId = null, IsShortage = true,
                Type = StockMovementType.ServiceConsumption, Quantity = -2.5m, OccurredAt = T0,
            });
        await ctx.SaveChangesAsync();
        return new Seed(t, user, loc, svc, appt, product, op, batch.Id, consumption.Id, set.Id, alert.Id);
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
    public async Task force_rls_and_tenant_isolation_policies_exist_on_the_three_new_tables()
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
    public async Task new_entities_and_columns_round_trip_through_ef()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(s.TenantId);

        var level = await ctx.StockLevels.SingleAsync(x => x.ProductId == s.Product);
        Assert.Equal(10m, level.Quantity);
        Assert.Equal(2.5m, level.ShortageQuantity);

        var op = await ctx.StockOperations.SingleAsync(x => x.Id == s.ConsumptionOperation);
        Assert.Equal(StockOperationKind.ServiceConsumption, op.Kind);
        Assert.Equal(s.Appointment, op.AppointmentId);
        Assert.Null(op.CreatedByUserId); // система

        var mv = await ctx.StockMovements.SingleAsync(x => x.OperationId == s.ConsumptionOperation);
        Assert.True(mv.IsShortage);
        Assert.Null(mv.BatchId);
        Assert.Equal(-2.5m, mv.Quantity);
        Assert.False((await ctx.StockMovements.CountAsync(x => x.OperationId != s.ConsumptionOperation && x.IsShortage)) > 0);

        var set = await ctx.ServiceMaterialSets.SingleAsync(x => x.Id == s.SetId);
        Assert.Null(set.LocationId);
        var line = await ctx.ServiceMaterials.SingleAsync(x => x.SetId == s.SetId);
        Assert.Equal(12.5m, line.Quantity);

        var alert = await ctx.StaffAlerts.SingleAsync(x => x.Id == s.AlertId);
        Assert.Equal("shortage", alert.Kind);
        Assert.Equal("open", alert.Status);
        Assert.Null(alert.DaysBucket);
        Assert.NotEqual(default, alert.CreatedAt);

        // expiring: bucket 0 (прострочено), дата, smallint
        ctx.Add(new StaffAlert
        {
            Kind = "expiring", LocationId = s.Location, ProductId = s.Product, BatchId = s.Batch, Quantity = 10m, ExpiresOn = new DateOnly(2027, 1, 31),
            DaysBucket = 0, DedupeKey = $"expiring:{s.Batch}:0",
        });
        await ctx.SaveChangesAsync();
        var expiring = await ctx.StaffAlerts.SingleAsync(x => x.Kind == "expiring");
        Assert.Equal((short)0, expiring.DaysBucket);
        Assert.Equal(new DateOnly(2027, 1, 31), expiring.ExpiresOn);

        var line2 = new StocktakeLine { StocktakeId = (await SeedStocktake(ctx, s)).Id, ProductId = s.Product, ExpectedShortage = 2.5m };
        ctx.Add(line2);
        await ctx.SaveChangesAsync();
        Assert.Equal(2.5m, (await ctx.StocktakeLines.SingleAsync(x => x.Id == line2.Id)).ExpectedShortage);
    }

    private static async Task<Stocktake> SeedStocktake(BeautyCrm.Infrastructure.Data.BeautyDbContext ctx, Seed s)
    {
        var st = new Stocktake { LocationId = s.Location, CreatedByUserId = s.UserId, CreatedAt = T0 };
        ctx.Add(st);
        await ctx.SaveChangesAsync();
        return st;
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
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_staff_alerts SET status = 'read', read_at = now() WHERE id = '{a.AlertId}'"));
        Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM beauty_service_material_sets WHERE id = '{a.SetId}'"));
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_service_materials SET quantity = 1 WHERE tenant_id = '{a.TenantId}'"));
    }

    [SkippableFact]
    public async Task with_check_and_composite_foreign_keys_block_cross_tenant_writes()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        // tenant B пише рядки з tenant_id A: WITH CHECK (42501)
        await Rejects(b.TenantId, $@"INSERT INTO beauty_service_material_sets (tenant_id, service_id, updated_by_user_id)
            VALUES ('{a.TenantId}', '{a.Service}', '{a.UserId}')", "42501");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, quantity, dedupe_key)
            VALUES ('{a.TenantId}', 'low_stock', '{a.Location}', '{a.Product}', 1, 'x')", "42501");
        await Rejects(null, $@"INSERT INTO beauty_service_materials (tenant_id, set_id, product_id, quantity)
            VALUES ('{a.TenantId}', '{a.SetId}', '{a.Product}', 1)", "42501");
        // tenant B посилається на сутності A: композитні FK (23503)
        await Rejects(b.TenantId, $@"INSERT INTO beauty_service_material_sets (tenant_id, service_id, updated_by_user_id)
            VALUES ('{b.TenantId}', '{a.Service}', '{b.UserId}')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_service_materials (tenant_id, set_id, product_id, quantity)
            VALUES ('{b.TenantId}', '{b.SetId}', '{a.Product}', 1)", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_service_materials (tenant_id, set_id, product_id, quantity)
            VALUES ('{b.TenantId}', '{a.SetId}', '{b.Product}', 1)", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, appointment_id)
            VALUES ('{b.TenantId}', 'service_consumption', '{b.Location}', '{a.Appointment}')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, batch_id, quantity, dedupe_key, days_bucket)
            VALUES ('{b.TenantId}', 'expiring', '{b.Location}', '{b.Product}', '{a.Batch}', 1, 'y', 7)", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, operation_id, quantity, dedupe_key)
            VALUES ('{b.TenantId}', 'shortage', '{b.Location}', '{b.Product}', '{a.ConsumptionOperation}', 1, 'z')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, quantity, dedupe_key, status, read_at, read_by_user_id)
            VALUES ('{b.TenantId}', 'low_stock', '{b.Location}', '{b.Product}', 1, 'w', 'read', now(), '{a.UserId}')", "23503");
        // dedupe_key унікальний лише в межах tenant
        await Rejects(a.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, operation_id, quantity, dedupe_key)
            VALUES ('{a.TenantId}', 'shortage', '{a.Location}', '{a.Product}', '{a.ConsumptionOperation}', 1, 'shortage:{a.ConsumptionOperation}:{a.Product}')", "23505", "ux_beauty_staff_alerts_dedupe");
        Assert.Equal(1, await Run(b.TenantId, $@"INSERT INTO beauty_staff_alerts (tenant_id, kind, location_id, product_id, quantity, dedupe_key)
            VALUES ('{b.TenantId}', 'low_stock', '{b.Location}', '{b.Product}', 1, 'shortage:{a.ConsumptionOperation}:{a.Product}')"));
        // одна операція автосписання на запис (UNIQUE) і RESTRICT на запис
        await Rejects(a.TenantId, $@"INSERT INTO beauty_stock_operations (tenant_id, kind, location_id, appointment_id)
            VALUES ('{a.TenantId}', 'service_consumption', '{a.Location}', '{a.Appointment}')", "23505", "ux_beauty_stock_operations_appointment");
        await Rejects(a.TenantId, $"DELETE FROM beauty_appointments WHERE id = '{a.Appointment}'", "23503");
        // CASCADE: рядки норм зникають разом із набором
        Assert.Equal(1, await Run(a.TenantId, $"DELETE FROM beauty_service_material_sets WHERE id = '{a.SetId}'"));
        Assert.Equal(0L, await Count(a.TenantId, "beauty_service_materials"));
    }
}
