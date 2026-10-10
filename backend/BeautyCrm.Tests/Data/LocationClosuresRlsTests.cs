using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-701 (§17): RLS, композитні FK, CHECK і EXCLUDE нової таблиці beauty_location_closures та колонки closed_weekdays.</summary>
public sealed class LocationClosuresRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public LocationClosuresRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid LocationId, Guid UserId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            var slug = "clos-" + tenantId.ToString("N")[..20];
            await Exec(admin, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')");
            await Exec(admin, $@"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                                 VALUES ('{userId}', '{tenantId}', 'u@{slug}.test', 'Staff', 'x', 'admin')");
        }
        await using var ctx = _db.CreateContext(tenantId);
        var location = new Location { Name = "L", Timezone = "UTC" };
        ctx.Add(location);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, location.Id, userId);
    }

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static LocationClosure Closure(Seed s, int fromDay, int toDay, string? reason = null, Guid? user = null) => new()
    {
        LocationId = s.LocationId, DateFrom = new DateOnly(2031, 3, 1).AddDays(fromDay), DateTo = new DateOnly(2031, 3, 1).AddDays(toDay),
        Reason = reason, CreatedByUserId = user,
    };

    [SkippableFact]
    public async Task closures_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        Guid id;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var c = Closure(a, 0, 2, "ремонт", a.UserId);
            ctx.Add(c);
            await ctx.SaveChangesAsync();
            id = c.Id;
        }

        await using var other = _db.CreateContext(b.TenantId);
        Assert.Empty(await other.LocationClosures.ToListAsync());
        Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_location_closures SET reason = 'hijack' WHERE id = {id}"));
        Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_location_closures WHERE id = {id}"));
        await using var own = _db.CreateContext(a.TenantId);
        Assert.Equal("ремонт", (await own.LocationClosures.AsNoTracking().SingleAsync(x => x.Id == id)).Reason);

        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.LocationClosures.ToListAsync());
    }

    [SkippableFact]
    public async Task closure_cannot_reference_a_foreign_tenant_location_or_user()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using (var ctx = _db.CreateContext(b.TenantId)) // заклад tenant A
        {
            ctx.Add(new LocationClosure
            {
                LocationId = a.LocationId, DateFrom = new DateOnly(2031, 3, 1), DateTo = new DateOnly(2031, 3, 1),
            });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, SqlState(ex));
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // користувач tenant A
        {
            ctx.Add(Closure(b, 0, 0, user: a.UserId));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, SqlState(ex));
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // запис чужим tenant_id блокує WITH CHECK
        {
            ctx.Add(new LocationClosure
            {
                TenantId = a.TenantId, LocationId = a.LocationId, DateFrom = new DateOnly(2031, 3, 1), DateTo = new DateOnly(2031, 3, 1),
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task check_constraints_reject_inverted_dates_and_long_reason()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Closure(a, 5, 4));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, SqlState(ex));
            Assert.Equal("ck_beauty_location_closures_dates", Constraint(ex));
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Closure(a, 0, 0, new string('r', 201)));
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.NotNull(SqlState(ex)); // 22001 (varchar(200)) або 23514 (CHECK)
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Closure(a, 0, 0, new string('r', 200)));
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task exclusion_constraint_rejects_overlapping_closures_per_location_only()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Closure(a, 10, 12));
            await ctx.SaveChangesAsync();
        }
        foreach (var (from, to) in new[] { (12, 14), (8, 10), (11, 11), (9, 20) })
        {
            await using var ctx = _db.CreateContext(a.TenantId);
            ctx.Add(Closure(a, from, to));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, SqlState(ex));
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // сусідні діапазони - не перетин
        {
            ctx.Add(Closure(a, 13, 13));
            ctx.Add(Closure(a, 9, 9));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // той самий діапазон в іншому tenant
        {
            ctx.Add(Closure(b, 10, 12));
            await ctx.SaveChangesAsync();
        }
        // інший заклад того ж tenant
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var second = new Location { Name = "L2", Timezone = "UTC" };
            ctx.Add(second);
            await ctx.SaveChangesAsync();
            ctx.Add(new LocationClosure { LocationId = second.Id, DateFrom = new DateOnly(2031, 3, 11), DateTo = new DateOnly(2031, 3, 12) });
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task closed_weekdays_defaults_to_empty_and_check_rejects_unknown_values()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var l = await ctx.Locations.AsNoTracking().SingleAsync();
            Assert.Empty(l.ClosedWeekdays);
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var l = await ctx.Locations.SingleAsync();
            l.ClosedWeekdays = ["sun", "mon"];
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            Assert.Equal(["sun", "mon"], (await ctx.Locations.AsNoTracking().SingleAsync()).ClosedWeekdays);
            var l = await ctx.Locations.SingleAsync();
            l.ClosedWeekdays = ["funday"];
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal("ck_beauty_locations_closed_weekdays", Constraint(ex));
        }
    }

    [SkippableFact]
    public async Task new_table_and_locations_have_force_row_level_security()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_location_closures", "beauty_locations" })
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{table}'::regclass", admin);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!, table);
        }
    }
}
