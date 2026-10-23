using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-703 (§18.1): колонка beauty_locations.working_hours - NULL за замовчуванням, jsonb, під RLS tenant.</summary>
public sealed class LocationWorkingHoursRlsTests : IClassFixture<PostgresRlsFixture>
{
    private const string Hours = """{"mon":[{"from":"10:00","to":"14:00"}]}""";

    private readonly PostgresRlsFixture _db;

    public LocationWorkingHoursRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private async Task<(Guid TenantId, Guid LocationId)> SeedAsync(string? hours = null)
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            var slug = "wh-" + tenantId.ToString("N")[..20];
            await using var cmd = new NpgsqlCommand($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var ctx = _db.CreateContext(tenantId);
        var location = new Location { Name = "L", Timezone = "UTC", WorkingHours = hours };
        ctx.Add(location);
        await ctx.SaveChangesAsync();
        return (tenantId, location.Id);
    }

    [SkippableFact]
    public async Task working_hours_default_to_null_and_roundtrip_as_jsonb()
    {
        SkipIfNoDb();
        var unset = await SeedAsync();
        var set = await SeedAsync(Hours);

        await using (var ctx = _db.CreateContext(unset.TenantId))
            Assert.Null((await ctx.Locations.AsNoTracking().SingleAsync(l => l.Id == unset.LocationId)).WorkingHours);

        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT pg_typeof(working_hours)::text, working_hours->'mon'->0->>'from' FROM beauty_locations WHERE id = '{set.LocationId}'", admin);
        await using var rd = await cmd.ExecuteReaderAsync();
        Assert.True(await rd.ReadAsync());
        Assert.Equal("jsonb", rd.GetString(0));
        Assert.Equal("10:00", rd.GetString(1));
    }

    [SkippableFact]
    public async Task working_hours_are_tenant_isolated()
    {
        SkipIfNoDb();
        var a = await SeedAsync(Hours);
        var b = await SeedAsync();

        await using var other = _db.CreateContext(b.TenantId);
        Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_locations SET working_hours = NULL WHERE id = {a.LocationId}"));
        Assert.Empty(await other.Locations.Where(l => l.Id == a.LocationId).ToListAsync());

        await using var own = _db.CreateContext(a.TenantId);
        Assert.NotNull((await own.Locations.AsNoTracking().SingleAsync(l => l.Id == a.LocationId)).WorkingHours);
        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.Locations.ToListAsync());
    }
}
