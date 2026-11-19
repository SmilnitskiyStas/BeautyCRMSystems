using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-885 (§32.17, §32.18, реальна БД, RLS, роль app): автосписання матеріалів при completed, дефіцит і його погашення,
/// FEFO без прострочених (локальна північ Europe/Kyiv, годинник - лише TimeProvider-заглушка), SAVEPOINT-відкат, гонки, інваріанти.
/// </summary>
public sealed class InventoryConsumptionDbTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;
    public InventoryConsumptionDbTests(PostgresRlsFixture db) => _db = db;
    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CaptureLog : ILogger<EfBeautyStore>
    {
        public List<(LogLevel Level, string Text)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Entries) Entries.Add((logLevel, formatter(state, exception))); }
    }

    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed record Seed(Guid TenantId, Guid UserId, Guid LocA, Guid LocB, Guid ServiceId, Guid SpecialistId, Guid ClientId);

    private int _day = 1;

    private async Task AdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> AdminScalarAsync<T>(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        var v = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(v!, typeof(T));
    }

    private async Task<Seed> SeedAsync(bool moduleOn = true)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await AdminAsync($@"INSERT INTO tenants (id, name, slug, modules) VALUES ('{tenantId}', 'T', 'cons-{tenantId:N}', '{(moduleOn ? "{beauty_inventory}" : "{}")}');
                            INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                            VALUES ('{userId}', '{tenantId}', 'u@{tenantId:N}.test', 'Owner', 'x', 'owner');");
        await using var ctx = _db.CreateContext(tenantId);
        var a = new Location { Name = "A", Timezone = "Europe/Kyiv" };
        var b = new Location { Name = "B", Timezone = "Europe/Kyiv" };
        var spec = new Specialist { FullName = "S" };
        var svc = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "Клієнт", Phone = "+380501112233" };
        ctx.AddRange(a, b, spec, svc, client);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, userId, a.Id, b.Id, svc.Id, spec.Id, client.Id);
    }

    private async Task<Guid> AddProductAsync(Seed s, string name = "P", ProductPurpose purpose = ProductPurpose.SalonUse, bool active = true, ProductUnit unit = ProductUnit.Ml)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var p = new Product { Name = name + Guid.NewGuid().ToString("N")[..6], Unit = unit, Purpose = purpose, IsActive = active, UpdatedByUserId = s.UserId };
        ctx.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private async Task SetNormsAsync(Seed s, Guid? location, params (Guid Product, decimal Qty)[] lines)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var set = new ServiceMaterialSet { ServiceId = s.ServiceId, LocationId = location, UpdatedByUserId = s.UserId };
        ctx.Add(set);
        await ctx.SaveChangesAsync();
        foreach (var l in lines) ctx.Add(new ServiceMaterial { SetId = set.Id, ProductId = l.Product, Quantity = l.Qty });
        await ctx.SaveChangesAsync();
    }

    private EfBeautyStore Store(BeautyDbContext ctx, DateTimeOffset? now = null, bool? autoOn = null, CaptureLog? log = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(autoOn is null ? [] : new Dictionary<string, string?> { ["Inventory:AutoConsumptionEnabled"] = autoOn.ToString() }).Build();
        return new EfBeautyStore(ctx, new NoSecrets(), new FixedTime(now ?? Noon), cfg, log);
    }

    private static OperationHeader Header(Seed s, Guid location, string kind = "receipt", Guid? to = null) =>
        new(Guid.NewGuid(), kind, location, to, null, null, null, kind == "write_off" ? "other" : null, null, s.UserId, Noon, "k" + Guid.NewGuid().ToString("N"), "r");

    private async Task ReceiveAsync(Seed s, Guid location, Guid product, decimal qty, DateOnly? expires = null, decimal? cost = 2m)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var r = await Store(ctx).PostReceiptAsync(Header(s, location), [new ReceiptLine(product, qty, cost, expires, null)], default);
        Assert.Equal(StockWriteStatus.Ok, r.Status);
    }

    private async Task<Guid> AddAppointmentAsync(Seed s, Guid? location = null, decimal cost = 0)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var appt = new Appointment
        {
            LocationId = location ?? s.LocA, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = s.ClientId,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m, Status = AppointmentStatus.Confirmed,
        };
        ctx.Add(appt);
        await ctx.SaveChangesAsync();
        return appt.Id;
    }

    private async Task<StoreResult<AppointmentDto>?> CompleteAsync(Seed s, Guid id, DateTimeOffset? now = null, bool? autoOn = null, CaptureLog? log = null)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await Store(ctx, now, autoOn, log).SetStatusAsync(id, "completed", default);
    }

    private Task<int> OpCountAsync(Seed s, Guid appt) =>
        AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{s.TenantId}' AND appointment_id = '{appt}'");

    private async Task<(decimal Qty, decimal Shortage)> LevelAsync(Seed s, Guid loc, Guid prod)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var l = await ctx.StockLevels.AsNoTracking().SingleOrDefaultAsync(x => x.LocationId == loc && x.ProductId == prod);
        return l is null ? (0, 0) : (l.Quantity, l.ShortageQuantity);
    }

    private async Task<string> StatusAsync(Seed s, Guid appt) => await AdminScalarAsync<string>($"SELECT status FROM beauty_appointments WHERE id = '{appt}'");

    /// <summary>§32.17: level = Σ партій = Σ рухів без нестачі; shortage = −Σ рухів нестачі; Σ усіх рухів = level − shortage; партії й рівні ≥ 0.</summary>
    private async Task AssertInvariantsAsync(Seed s)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand($@"
            SELECT l.location_id, l.product_id, l.quantity, l.shortage_quantity,
              coalesce((SELECT sum(b.quantity) FROM beauty_stock_batches b WHERE b.tenant_id = l.tenant_id AND b.location_id = l.location_id AND b.product_id = l.product_id), 0),
              coalesce((SELECT sum(m.quantity) FROM beauty_stock_movements m WHERE m.tenant_id = l.tenant_id AND m.location_id = l.location_id AND m.product_id = l.product_id AND NOT m.is_shortage), 0),
              coalesce((SELECT sum(m.quantity) FROM beauty_stock_movements m WHERE m.tenant_id = l.tenant_id AND m.location_id = l.location_id AND m.product_id = l.product_id AND m.is_shortage), 0),
              coalesce((SELECT min(b.quantity) FROM beauty_stock_batches b WHERE b.tenant_id = l.tenant_id AND b.location_id = l.location_id AND b.product_id = l.product_id), 0)
            FROM beauty_stock_levels l WHERE l.tenant_id = '{s.TenantId}'", admin);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var (q, sh, batches, mov, movShort, minBatch) = (rd.GetDecimal(2), rd.GetDecimal(3), rd.GetDecimal(4), rd.GetDecimal(5), rd.GetDecimal(6), rd.GetDecimal(7));
            var tag = $"{rd.GetGuid(0)}/{rd.GetGuid(1)}";
            Assert.True(q >= 0 && sh >= 0 && minBatch >= 0, $"negative {tag}");
            Assert.True(q == batches, $"level {q} != batches {batches} ({tag})");
            Assert.True(q == mov, $"level {q} != movements {mov} ({tag})");
            Assert.True(sh == -movShort, $"shortage {sh} != -shortage movements {-movShort} ({tag})");
            Assert.True(mov + movShort == q - sh, $"journal sum != balance ({tag})");
        }
    }

    // ---------- автосписання ----------

    [SkippableFact]
    public async Task Completion_consumes_materials_fefo_and_keeps_cost_snapshot()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 1m, new DateOnly(2026, 12, 1));
        await ReceiveAsync(s, s.LocA, p, 5m, new DateOnly(2026, 11, 25));
        await AdminAsync($"INSERT INTO beauty_service_costs (id, tenant_id, service_id, location_id, cost_amount, master_percent, updated_by_user_id) VALUES (gen_random_uuid(), '{s.TenantId}', '{s.ServiceId}', NULL, 12.50, 40, '{s.UserId}')");
        var id = await AddAppointmentAsync(s);

        var r = await CompleteAsync(s, id);
        Assert.True(r!.Value is not null);
        Assert.Equal(1, await OpCountAsync(s, id));
        Assert.Equal((4m, 0m), await LevelAsync(s, s.LocA, p));
        // FEFO: ближчий строк (25.11) віддав 2, з партії 1.12 нічого
        Assert.Equal(2m, await AdminScalarAsync<decimal>($"SELECT quantity FROM beauty_stock_batches WHERE tenant_id = '{s.TenantId}' AND expires_on = '2026-12-01'") + 1m);
        Assert.Equal(3m, await AdminScalarAsync<decimal>($"SELECT quantity FROM beauty_stock_batches WHERE tenant_id = '{s.TenantId}' AND expires_on = '2026-11-25'"));
        // система як автор; ADR-014 знімок не змінено
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_operations WHERE appointment_id = '{id}' AND created_by_user_id IS NOT NULL"));
        Assert.Equal(12.50m, await AdminScalarAsync<decimal>($"SELECT cost_snapshot FROM beauty_appointments WHERE id = '{id}'"));
        Assert.Equal(40m, await AdminScalarAsync<decimal>($"SELECT master_percent_snapshot FROM beauty_appointments WHERE id = '{id}'"));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Repeated_completion_and_status_roundtrip_do_not_consume_twice()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 10m);
        var id = await AddAppointmentAsync(s);

        Assert.True((await CompleteAsync(s, id))!.Value is not null);
        Assert.True((await CompleteAsync(s, id))!.AppointmentClosed);
        Assert.Equal((8m, 0m), await LevelAsync(s, s.LocA, p));

        // статус повернули й завершили знову (cost_consistency: знімок скидаємо разом зі статусом) -> одна операція, без другого списання
        await AdminAsync($"UPDATE beauty_appointments SET status = 'confirmed', cost_snapshot = NULL, master_percent_snapshot = NULL, master_payout_snapshot = NULL, cost_captured_at = NULL WHERE id = '{id}'");
        Assert.True((await CompleteAsync(s, id))!.Value is not null);
        Assert.Equal(1, await OpCountAsync(s, id));
        Assert.Equal((8m, 0m), await LevelAsync(s, s.LocA, p));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Module_off_or_flag_off_or_no_norms_skip_but_visit_completes()
    {
        SkipIfNoDb();
        // модуль вимкнено
        var off = await SeedAsync(moduleOn: false);
        var po = await AddProductAsync(off);
        await SetNormsAsync(off, null, (po, 2m));
        await ReceiveAsync(off, off.LocA, po, 10m);
        var a1 = await AddAppointmentAsync(off);
        Assert.True((await CompleteAsync(off, a1))!.Value is not null);
        Assert.Equal(0, await OpCountAsync(off, a1));
        Assert.Equal((10m, 0m), await LevelAsync(off, off.LocA, po));

        // прапор вимкнено
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 10m);
        var a2 = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a2, autoOn: false))!.Value is not null);
        Assert.Equal(0, await OpCountAsync(s, a2));

        // немає норми
        var n = await SeedAsync();
        var a3 = await AddAppointmentAsync(n);
        Assert.True((await CompleteAsync(n, a3))!.Value is not null);
        Assert.Equal(0, await OpCountAsync(n, a3));
    }

    [SkippableFact]
    public async Task Location_override_replaces_network_set_and_empty_override_consumes_nothing()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var net = await AddProductAsync(s, "Net");
        var loc = await AddProductAsync(s, "Loc");
        await SetNormsAsync(s, null, (net, 1m));
        await SetNormsAsync(s, s.LocA, (loc, 3m));
        await SetNormsAsync(s, s.LocB); // порожній override
        foreach (var l in new[] { s.LocA, s.LocB })
        { await ReceiveAsync(s, l, net, 10m); await ReceiveAsync(s, l, loc, 10m); }

        var a = await AddAppointmentAsync(s, s.LocA);
        Assert.True((await CompleteAsync(s, a))!.Value is not null);
        Assert.Equal((10m, 0m), await LevelAsync(s, s.LocA, net));
        Assert.Equal((7m, 0m), await LevelAsync(s, s.LocA, loc));

        var b = await AddAppointmentAsync(s, s.LocB);
        Assert.True((await CompleteAsync(s, b))!.Value is not null);
        Assert.Equal(0, await OpCountAsync(s, b));
        Assert.Equal((10m, 0m), await LevelAsync(s, s.LocB, net));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Inactive_sale_and_disabled_in_location_products_are_skipped()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var inactive = await AddProductAsync(s, "I");
        var sale = await AddProductAsync(s, "S", ProductPurpose.Sale);
        var disabled = await AddProductAsync(s, "D");
        var ok = await AddProductAsync(s, "K", ProductPurpose.Both);
        await SetNormsAsync(s, null, (inactive, 1m), (sale, 1m), (disabled, 1m), (ok, 1m));
        foreach (var p in new[] { inactive, sale, disabled, ok }) await ReceiveAsync(s, s.LocA, p, 5m);
        await AdminAsync($@"UPDATE beauty_products SET is_active = false WHERE id = '{inactive}';
            INSERT INTO beauty_product_location_settings (id, tenant_id, product_id, location_id, is_enabled, updated_by_user_id)
            VALUES (gen_random_uuid(), '{s.TenantId}', '{disabled}', '{s.LocA}', false, '{s.UserId}')");
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a))!.Value is not null);
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, inactive));
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, sale));
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, disabled));
        Assert.Equal((4m, 0m), await LevelAsync(s, s.LocA, ok));
        await AssertInvariantsAsync(s);
    }

    // ---------- нестача ----------

    [SkippableFact]
    public async Task Shortage_never_blocks_completion_records_shortage_movement_and_one_alert()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var partial = await AddProductAsync(s, "Part");
        var none = await AddProductAsync(s, "None");
        await SetNormsAsync(s, null, (partial, 3m), (none, 2m));
        await ReceiveAsync(s, s.LocA, partial, 1m);
        var a = await AddAppointmentAsync(s);

        var r = await CompleteAsync(s, a);
        Assert.True(r!.Value is not null);
        Assert.Equal("completed", await StatusAsync(s, a));
        Assert.Equal((0m, 2m), await LevelAsync(s, s.LocA, partial));
        Assert.Equal((0m, 2m), await LevelAsync(s, s.LocA, none));
        Assert.Equal(2, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{s.TenantId}' AND is_shortage AND quantity < 0 AND batch_id IS NULL AND unit_cost IS NULL AND type = 'service_consumption'"));
        Assert.Equal(2, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_staff_alerts WHERE tenant_id = '{s.TenantId}' AND kind = 'shortage' AND status = 'open' AND dedupe_key LIKE 'shortage:%'"));
        Assert.Equal(2m, await AdminScalarAsync<decimal>($"SELECT quantity FROM beauty_staff_alerts WHERE tenant_id = '{s.TenantId}' AND product_id = '{partial}'"));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Receipt_repays_shortage_partial_full_and_surplus()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 10m));
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a))!.Value is not null);
        Assert.Equal((0m, 10m), await LevelAsync(s, s.LocA, p));

        await ReceiveAsync(s, s.LocA, p, 4m); // q < s: партії немає
        Assert.Equal((0m, 6m), await LevelAsync(s, s.LocA, p));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_batches WHERE tenant_id = '{s.TenantId}' AND product_id = '{p}'"));
        await AssertInvariantsAsync(s);

        await ReceiveAsync(s, s.LocA, p, 6m); // q = s: партія не створюється
        Assert.Equal((0m, 0m), await LevelAsync(s, s.LocA, p));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_batches WHERE tenant_id = '{s.TenantId}' AND product_id = '{p}'"));
        await AssertInvariantsAsync(s);

        var a2 = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a2))!.Value is not null); // знову нестача 10
        await ReceiveAsync(s, s.LocA, p, 25m); // q > s: нестача 10 покрито, партія 15
        Assert.Equal((15m, 0m), await LevelAsync(s, s.LocA, p));
        Assert.Equal(15m, await AdminScalarAsync<decimal>($"SELECT initial_quantity FROM beauty_stock_batches WHERE tenant_id = '{s.TenantId}' AND product_id = '{p}'"));
        Assert.Equal(1, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{s.TenantId}' AND type = 'receipt' AND is_shortage AND quantity = 10 AND unit_cost = 2"));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Transfer_into_location_with_shortage_repays_it_and_legs_sum_to_zero()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 3m));
        var a = await AddAppointmentAsync(s, s.LocB);
        Assert.True((await CompleteAsync(s, a))!.Value is not null); // у B нестача 3
        await ReceiveAsync(s, s.LocA, p, 10m);

        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var h = Header(s, s.LocA, "transfer", s.LocB);
            var r = await Store(ctx).PostTransferAsync(h, [new StockLine(0, p, 5m, null)], new DateOnly(2026, 11, 18), default);
            Assert.Equal(StockWriteStatus.Ok, r.Status);
            Assert.Equal(0m, r.Operation!.Movements.Sum(m => m.Quantity));
            Assert.Contains(r.Operation.Movements, m => m.IsShortage && m.Type == "transfer_in" && m.Quantity == 3m);
        }
        Assert.Equal((2m, 0m), await LevelAsync(s, s.LocB, p));
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, p));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Stocktake_resets_shortage_and_difference_is_against_balance()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 3m));
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a))!.Value is not null); // нестача 3, рівень 0

        Guid stId; int version;
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var st = Store(ctx);
            var c = await st.CreateStocktakeAsync(s.LocA, null, [p], s.UserId, Noon, default);
            stId = c.Stocktake!.Id;
            Assert.Equal(3m, c.Stocktake.Lines.Single().ExpectedShortage);
            var put = await st.PutStocktakeLinesAsync(stId, c.Stocktake.Version, [new StocktakeLineChange(p, 5m, new DateOnly(2027, 1, 1))], s.UserId, Noon, default);
            version = put.Stocktake!.Version;
            Assert.Equal(8m, put.Stocktake.Lines.Single().Difference); // 5 - (0 - 3)
        }
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var r = await Store(ctx).PostStocktakeAsync(stId, version, s.UserId, Noon, new DateOnly(2026, 11, 18), default);
            Assert.Equal(StocktakeWriteStatus.Ok, r.Status);
            Assert.Contains(r.Operation!.Movements, m => m.IsShortage && m.Type == "stocktake_adjust" && m.Quantity == 3m);
        }
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, p));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Manual_write_off_and_transfer_source_do_not_touch_shortage()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 4m));
        await ReceiveAsync(s, s.LocA, p, 2m);
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a))!.Value is not null); // 2 з партії, нестача 2
        await ReceiveAsync(s, s.LocA, p, 1m); // покриває 1 нестачі, партії немає
        Assert.Equal((0m, 1m), await LevelAsync(s, s.LocA, p));

        await using var ctx = _db.CreateContext(s.TenantId);
        var r = await Store(ctx).PostWriteOffAsync(Header(s, s.LocA, "write_off"), [new StockLine(0, p, 1m, null)], new DateOnly(2026, 11, 18), default);
        Assert.Equal(StockWriteStatus.Insufficient, r.Status); // «мінус» не списується вручну: лише з партій
        Assert.Equal((0m, 1m), await LevelAsync(s, s.LocA, p));
    }

    // ---------- FEFO без прострочених, локальна північ ----------

    [SkippableTheory]
    [InlineData("2026-11-19T21:59:00Z", 2, 0)] // 23:59 Київ 19.11: партія з expires_on 19.11 ще придатна
    [InlineData("2026-11-19T22:01:00Z", 0, 2)] // 00:01 Київ 20.11: прострочена, не списується -> нестача
    public async Task Expired_batches_by_location_local_date_are_not_consumed(string at, int consumed, int shortage)
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 5m, new DateOnly(2026, 11, 19));
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a, DateTimeOffset.Parse(at)))!.Value is not null);
        var (q, sh) = await LevelAsync(s, s.LocA, p);
        Assert.Equal(5m - consumed, q);
        Assert.Equal((decimal)shortage, sh);
        await AssertInvariantsAsync(s);
    }

    // ---------- SAVEPOINT ----------

    [SkippableFact]
    public async Task Technical_failure_rolls_back_only_inventory_step_visit_stays_completed()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 5m);
        await AdminScalarAsync<int>($@"CREATE FUNCTION fail_cons_{s.TenantId:N}() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'injected'; END $$;
            CREATE TRIGGER fail_cons BEFORE INSERT ON beauty_stock_movements FOR EACH ROW WHEN (NEW.type = 'service_consumption' AND NEW.tenant_id = '{s.TenantId}')
            EXECUTE FUNCTION fail_cons_{s.TenantId:N}(); SELECT 1");
        await AdminAsync($"INSERT INTO beauty_service_costs (id, tenant_id, service_id, location_id, cost_amount, master_percent, updated_by_user_id) VALUES (gen_random_uuid(), '{s.TenantId}', '{s.ServiceId}', NULL, 7.00, 10, '{s.UserId}')");
        var a = await AddAppointmentAsync(s);
        var log = new CaptureLog();
        try
        {
            var r = await CompleteAsync(s, a, log: log);
            Assert.True(r!.Value is not null);
        }
        finally { await AdminAsync($"DROP TRIGGER fail_cons ON beauty_stock_movements; DROP FUNCTION fail_cons_{s.TenantId:N}()"); }

        Assert.Equal("completed", await StatusAsync(s, a));
        Assert.Equal(7.00m, await AdminScalarAsync<decimal>($"SELECT cost_snapshot FROM beauty_appointments WHERE id = '{a}'"));
        Assert.Equal(0, await OpCountAsync(s, a));
        Assert.Equal((5m, 0m), await LevelAsync(s, s.LocA, p));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{s.TenantId}' AND type = 'service_consumption'"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("inventory_consumption_failed") && e.Text.Contains(a.ToString()));
        await AssertInvariantsAsync(s);
    }

    // M1 (security-review): блокування рівня іншою транзакцією -> lock_timeout, SAVEPOINT поглинає 55P03, візит completed за обмежений час
    [SkippableFact]
    public async Task Locked_stock_level_times_out_visit_completes_snapshot_kept_no_movements()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 5m);
        await AdminAsync($"INSERT INTO beauty_service_costs (id, tenant_id, service_id, location_id, cost_amount, master_percent, updated_by_user_id) VALUES (gen_random_uuid(), '{s.TenantId}', '{s.ServiceId}', NULL, 9.00, 10, '{s.UserId}')");
        var a = await AddAppointmentAsync(s);
        var log = new CaptureLog();

        await using var blocker = new NpgsqlConnection(_db.AdminConnectionString);
        await blocker.OpenAsync();
        await using var holder = await blocker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand($"SELECT 1 FROM beauty_stock_levels WHERE tenant_id = '{s.TenantId}' AND product_id = '{p}' FOR UPDATE", blocker, holder))
            await lockCmd.ExecuteScalarAsync();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await CompleteAsync(s, a, log: log);
        sw.Stop();
        await holder.RollbackAsync();

        Assert.True(r!.Value is not null);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"completion took {sw.Elapsed}");
        Assert.Equal("completed", await StatusAsync(s, a));
        Assert.Equal(9.00m, await AdminScalarAsync<decimal>($"SELECT cost_snapshot FROM beauty_appointments WHERE id = '{a}'"));
        Assert.Equal(0, await OpCountAsync(s, a));
        Assert.Equal(0, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{s.TenantId}' AND type = 'service_consumption'"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Text.Contains("inventory_consumption_failed") && e.Text.Contains("sqlstate=55P03"));
        await AssertInvariantsAsync(s);
    }

    // L1: будь-яке непорожнє значення, крім true (без регістру), вимикає автосписання; порожнє/відсутнє = увімкнено
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    [InlineData("no", false)]
    [InlineData("garbage", false)]
    public void Auto_consumption_flag_is_fail_safe(string? value, bool expected) =>
        Assert.Equal(expected, EfBeautyStore.IsAutoConsumptionOn(value));

    [SkippableTheory]
    [InlineData("0", 0)]
    [InlineData("off", 0)]
    [InlineData("no", 0)]
    [InlineData("false", 0)]
    [InlineData("true", 1)]
    [InlineData(null, 1)]
    public async Task Auto_consumption_flag_value_controls_consumption_end_to_end(string? value, int expectedOps)
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 5m);
        var a = await AddAppointmentAsync(s);
        await using var ctx = _db.CreateContext(s.TenantId);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(value is null ? [] : new Dictionary<string, string?> { ["Inventory:AutoConsumptionEnabled"] = value }).Build();
        var r = await new EfBeautyStore(ctx, new NoSecrets(), new FixedTime(Noon), cfg, null).SetStatusAsync(a, "completed", default);
        Assert.True(r!.Value is not null);
        Assert.Equal(expectedOps, await OpCountAsync(s, a));
    }

    // L2: модуль у БД в іншому регістрі все одно вважається ввімкненим (як [RequireModule] і воркер)
    [SkippableFact]
    public async Task Module_name_is_matched_case_insensitively()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await AdminAsync($"UPDATE tenants SET modules = '{{BEAUTY_Inventory}}' WHERE id = '{s.TenantId}'");
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 5m);
        var a = await AddAppointmentAsync(s);
        Assert.True((await CompleteAsync(s, a))!.Value is not null);
        Assert.Equal(1, await OpCountAsync(s, a));
        Assert.Equal((3m, 0m), await LevelAsync(s, s.LocA, p));
    }

    // ---------- гонки ----------

    [SkippableFact]
    public async Task Parallel_completion_of_same_visit_consumes_once()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p = await AddProductAsync(s);
        await SetNormsAsync(s, null, (p, 2m));
        await ReceiveAsync(s, s.LocA, p, 10m);
        var a = await AddAppointmentAsync(s);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => CompleteAsync(s, a)));
        Assert.Equal(1, results.Count(r => r!.Value is not null));
        Assert.Equal(1, await OpCountAsync(s, a));
        Assert.Equal((8m, 0m), await LevelAsync(s, s.LocA, p));
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Parallel_completion_of_visits_on_same_stock_has_no_deadlock_and_keeps_invariants()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var p1 = await AddProductAsync(s, "A");
        var p2 = await AddProductAsync(s, "B");
        await SetNormsAsync(s, null, (p1, 2m), (p2, 1m));
        await ReceiveAsync(s, s.LocA, p1, 5m);
        await ReceiveAsync(s, s.LocA, p2, 3m);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) ids.Add(await AddAppointmentAsync(s));

        // паралельно з ручним списанням того ж товару
        var manual = Task.Run(async () =>
        {
            await using var ctx = _db.CreateContext(s.TenantId);
            return await Store(ctx).PostWriteOffAsync(Header(s, s.LocA, "write_off"), [new StockLine(0, p1, 1m, null)], new DateOnly(2026, 11, 18), default);
        });
        var results = await Task.WhenAll(ids.Select(id => CompleteAsync(s, id)));
        await manual;
        Assert.All(results, r => Assert.True(r!.Value is not null));
        Assert.Equal(6, await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{s.TenantId}' AND kind = 'service_consumption'"));
        var (q1, s1) = await LevelAsync(s, s.LocA, p1);
        var writeOffs = await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{s.TenantId}' AND kind = 'write_off'");
        Assert.Equal(5m - 12m - writeOffs, q1 - s1); // сальдо = прихід - норми 6 візитів - ручне списання (якщо встигло)
        await AssertInvariantsAsync(s);
    }

    [SkippableFact]
    public async Task Parallel_completion_with_reversed_norm_order_and_two_locations_has_no_deadlock_and_isolates_tenants()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var other = await SeedAsync(); // другий tenant: паралельне завершення не повинно його зачіпати
        var p1 = await AddProductAsync(s, "A");
        var p2 = await AddProductAsync(s, "B");
        var op = await AddProductAsync(other, "O");
        await SetNormsAsync(other, null, (op, 1m));
        await SetNormsAsync(s, null, (p1, 1m), (p2, 1m)); // service 1: p1, p2
        Guid svc2;
        await using (var ctx = _db.CreateContext(s.TenantId))
        {
            var sv = new Service { Name = "Svc2", DurationMinutes = 30 };
            ctx.Add(sv);
            await ctx.SaveChangesAsync();
            svc2 = sv.Id;
            var set = new ServiceMaterialSet { ServiceId = svc2, LocationId = null, UpdatedByUserId = s.UserId };
            ctx.Add(set);
            await ctx.SaveChangesAsync();
            ctx.Add(new ServiceMaterial { SetId = set.Id, ProductId = p2, Quantity = 1m }); // service 2: p2, p1 (зворотний порядок вставки)
            ctx.Add(new ServiceMaterial { SetId = set.Id, ProductId = p1, Quantity = 1m });
            await ctx.SaveChangesAsync();
        }
        foreach (var loc in new[] { s.LocA, s.LocB })
        { await ReceiveAsync(s, loc, p1, 4m); await ReceiveAsync(s, loc, p2, 4m); }

        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
            foreach (var loc in new[] { s.LocA, s.LocB })
            {
                ids.Add(await AddAppointmentAsync(s, loc));
                await using var ctx = _db.CreateContext(s.TenantId);
                var a2 = new Appointment
                {
                    LocationId = loc, SpecialistId = s.SpecialistId, ServiceId = svc2, ClientId = s.ClientId,
                    StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 30,
                    Source = AppointmentSource.Admin, PriceOriginal = 10m, PriceFinal = 10m, Status = AppointmentStatus.Confirmed,
                };
                ctx.Add(a2);
                await ctx.SaveChangesAsync();
                ids.Add(a2.Id);
            }
        var done = Task.WhenAll(ids.Select(id => CompleteAsync(s, id)));
        Assert.Same(done, await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(60)))); // дедлок = таймаут
        Assert.All(await done, r => Assert.True(r!.Value is not null));
        Assert.Equal(16, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{s.TenantId}' AND kind = 'service_consumption'"));
        foreach (var loc in new[] { s.LocA, s.LocB })
            foreach (var p in new[] { p1, p2 })
            {
                var (q, sh) = await LevelAsync(s, loc, p);
                Assert.Equal(4m - 8m, q - sh); // 8 візитів × 1 одиниця на пару, прийшло 4 -> сальдо -4 (нестача 4)
            }
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{other.TenantId}'"));
        await AssertInvariantsAsync(s);
    }

    private Task<T> AdminScalar<T>(string sql) => AdminScalarAsync<T>(sql);

    // ---------- randomized ----------

    [SkippableTheory]
    [InlineData(3)]
    [InlineData(11)]
    [InlineData(2026)]
    public async Task Random_series_with_consumption_keeps_shortage_invariants(int seed)
    {
        SkipIfNoDb();
        var rnd = new Random(seed);
        var s = await SeedAsync();
        var prods = new[] { await AddProductAsync(s, "R1"), await AddProductAsync(s, "R2") };
        await SetNormsAsync(s, null, (prods[0], 3m), (prods[1], 2m));
        var locs = new[] { s.LocA, s.LocB };
        for (var i = 0; i < 40; i++)
        {
            var loc = locs[rnd.Next(2)];
            var prod = prods[rnd.Next(2)];
            var qty = rnd.Next(1, 7);
            switch (rnd.Next(5))
            {
                case 0: await ReceiveAsync(s, loc, prod, qty); break;
                case 1 or 2:
                    Assert.True((await CompleteAsync(s, await AddAppointmentAsync(s, loc)))!.Value is not null);
                    break;
                case 3:
                {
                    await using var ctx = _db.CreateContext(s.TenantId);
                    var to = loc == s.LocA ? s.LocB : s.LocA;
                    await Store(ctx).PostTransferAsync(Header(s, loc, "transfer", to), [new StockLine(0, prod, qty, null)], new DateOnly(2026, 11, 18), default);
                    break;
                }
                default:
                {
                    await using var ctx = _db.CreateContext(s.TenantId);
                    await Store(ctx).PostWriteOffAsync(Header(s, loc, "write_off"), [new StockLine(0, prod, qty, null)], new DateOnly(2026, 11, 18), default);
                    break;
                }
            }
            await AssertInvariantsAsync(s);
        }
        Assert.True(await AdminScalarAsync<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{s.TenantId}' AND is_shortage") > 0, "scenario never produced a shortage");
    }
}
