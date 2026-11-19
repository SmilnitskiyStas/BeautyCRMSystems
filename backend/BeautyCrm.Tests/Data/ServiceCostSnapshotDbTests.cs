using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-763 (§24.4, ADR-014 §2.2): знімок собівартості одним умовним UPDATE при переході в completed (реальна БД, RLS):
/// рівні мережа/заклад, нуль, без собівартості, знижка, округлення, гонка двох completed, повторне completed, інші статуси.
/// </summary>
public sealed class ServiceCostSnapshotDbTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public ServiceCostSnapshotDbTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed record Seed(Guid TenantId, Guid UserId, Guid LocationA, Guid LocationB, Guid ServiceId, Guid SpecialistId, Guid ClientId);

    private int _day = 1;

    private async Task<Seed> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $@"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'snap-{tenantId:N}');
                   INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                   VALUES ('{userId}', '{tenantId}', 'u@{tenantId:N}.test', 'Owner', 'x', 'owner');", admin);
            await cmd.ExecuteNonQueryAsync();
        }
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

    private async Task<Guid> AddAppointmentAsync(Seed s, decimal original = 100m, decimal? final = null, Guid? location = null,
        AppointmentStatus status = AppointmentStatus.Confirmed)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var appt = new Appointment
        {
            LocationId = location ?? s.LocationA, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = s.ClientId,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = original, PriceFinal = final ?? original, Status = status,
        };
        ctx.Add(appt);
        await ctx.SaveChangesAsync();
        return appt.Id;
    }

    private async Task SetCostAsync(Seed s, decimal cost, decimal? percent, Guid? location = null)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var row = await ctx.ServiceCosts.FirstOrDefaultAsync(c => c.ServiceId == s.ServiceId && c.LocationId == location);
        if (row is null)
            ctx.Add(row = new ServiceCost { ServiceId = s.ServiceId, LocationId = location, UpdatedByUserId = s.UserId });
        row.CostAmount = cost;
        row.MasterPercent = percent;
        await ctx.SaveChangesAsync();
    }

    private async Task<StoreResult<AppointmentDto>?> CompleteAsync(Seed s, Guid id, string status = "completed")
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        return await new EfBeautyStore(ctx, new NoSecrets()).SetStatusAsync(id, status, default);
    }

    private sealed record Snap(string Status, decimal? Cost, decimal? Percent, decimal? Payout, DateTimeOffset? CapturedAt);

    private async Task<Snap> ReadAsync(Seed s, Guid id)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var a = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        return new Snap(EnumText<AppointmentStatus>.ToDb(a.Status), a.CostSnapshot, a.MasterPercentSnapshot, a.MasterPayoutSnapshot, a.CostCapturedAt);
    }

    [SkippableFact]
    public async Task completed_takes_location_override_over_network_and_other_locations_use_network()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 120m, 10m);
        await SetCostAsync(s, 150m, 20m, s.LocationA);
        var a = await AddAppointmentAsync(s, 200m, location: s.LocationA);
        var b = await AddAppointmentAsync(s, 200m, location: s.LocationB);

        Assert.NotNull((await CompleteAsync(s, a))!.Value);
        Assert.NotNull((await CompleteAsync(s, b))!.Value);

        var sa = await ReadAsync(s, a);
        Assert.Equal(("completed", 150m, 20m, 40m), (sa.Status, sa.Cost, sa.Percent, sa.Payout));
        Assert.NotNull(sa.CapturedAt);
        var sb = await ReadAsync(s, b);
        Assert.Equal((120m, 10m, 20m), (sb.Cost, sb.Percent, sb.Payout));
    }

    [SkippableFact]
    public async Task location_override_without_percent_replaces_the_whole_row_so_payout_is_zero()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 120m, 40m);
        await SetCostAsync(s, 150m, null, s.LocationA);
        var a = await AddAppointmentAsync(s, 200m);
        await CompleteAsync(s, a);
        var snap = await ReadAsync(s, a);
        Assert.Equal((150m, null, 0m), (snap.Cost, snap.Percent, snap.Payout));
    }

    [SkippableFact]
    public async Task zero_cost_is_a_snapshot_not_a_missing_cost()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 0m, null);
        var a = await AddAppointmentAsync(s, 100m);
        await CompleteAsync(s, a);
        var snap = await ReadAsync(s, a);
        Assert.Equal((0m, null, 0m), (snap.Cost, snap.Percent, snap.Payout));
        Assert.NotNull(snap.CapturedAt);
    }

    [SkippableFact]
    public async Task without_cost_only_captured_at_is_written()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        var a = await AddAppointmentAsync(s, 100m);
        Assert.NotNull((await CompleteAsync(s, a))!.Value);
        var snap = await ReadAsync(s, a);
        Assert.Equal("completed", snap.Status);
        Assert.Null(snap.Cost);
        Assert.Null(snap.Percent);
        Assert.Null(snap.Payout);
        Assert.NotNull(snap.CapturedAt);
    }

    [SkippableTheory]
    [InlineData("333.33", "333.33", "33.33", "111.10")] // 111.0988 -> 111.10
    [InlineData("1.00", "1.00", "0.5", "0.01")] // 0.005 -> 0.01 (half away from zero)
    [InlineData("500.00", "0.00", "40", "0.00")] // акція 100%: виплата від price_final
    [InlineData("500.00", "350.00", "10", "35.00")] // знижка: виплата від ціни продажу, не від price_original
    public async Task payout_is_rounded_from_price_final(string original, string final, string percent, string expectedPayout)
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 30m, decimal.Parse(percent, System.Globalization.CultureInfo.InvariantCulture));
        var a = await AddAppointmentAsync(s, decimal.Parse(original, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(final, System.Globalization.CultureInfo.InvariantCulture));
        await CompleteAsync(s, a);
        var snap = await ReadAsync(s, a);
        Assert.Equal(decimal.Parse(expectedPayout, System.Globalization.CultureInfo.InvariantCulture), snap.Payout);
        Assert.Equal(30m, snap.Cost);
    }

    [SkippableFact]
    public async Task changing_cost_after_completion_does_not_rewrite_the_snapshot()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 100m, 10m);
        var a = await AddAppointmentAsync(s, 200m);
        await CompleteAsync(s, a);
        var before = await ReadAsync(s, a);
        await SetCostAsync(s, 999m, 99m);
        Assert.Equal(before, await ReadAsync(s, a));
    }

    [SkippableFact]
    public async Task repeated_completed_is_appointment_closed_and_keeps_the_first_snapshot()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 100m, 10m);
        var a = await AddAppointmentAsync(s, 200m);
        await CompleteAsync(s, a);
        var first = await ReadAsync(s, a);
        await SetCostAsync(s, 5m, 5m);

        var again = await CompleteAsync(s, a);
        Assert.True(again!.AppointmentClosed);
        Assert.Null(again.Value);
        Assert.Equal(first, await ReadAsync(s, a));
    }

    [SkippableFact]
    public async Task completing_a_cancelled_appointment_is_closed_and_unknown_id_is_null()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 100m, 10m);
        var a = await AddAppointmentAsync(s, 200m, status: AppointmentStatus.Cancelled);
        Assert.True((await CompleteAsync(s, a))!.AppointmentClosed);
        var snap = await ReadAsync(s, a);
        Assert.Equal("cancelled", snap.Status);
        Assert.Null(snap.Cost);
        Assert.Null(snap.CapturedAt);
        Assert.Null(await CompleteAsync(s, Guid.NewGuid()));
    }

    [SkippableTheory]
    [InlineData("confirmed")]
    [InlineData("no_show")]
    public async Task other_transitions_do_not_write_a_snapshot(string status)
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 100m, 10m);
        var a = await AddAppointmentAsync(s, 200m, status: AppointmentStatus.Pending);
        Assert.NotNull((await CompleteAsync(s, a, status))!.Value);
        var snap = await ReadAsync(s, a);
        Assert.Equal(status, snap.Status);
        Assert.Null(snap.Cost);
        Assert.Null(snap.Percent);
        Assert.Null(snap.Payout);
        Assert.Null(snap.CapturedAt);
    }

    [SkippableFact]
    public async Task two_parallel_completions_write_exactly_one_snapshot_and_the_other_gets_closed()
    {
        SkipIfNoDb();
        var s = await SeedAsync();
        await SetCostAsync(s, 100m, 10m);
        for (var round = 0; round < 5; round++)
        {
            var a = await AddAppointmentAsync(s, 200m);
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => CompleteAsync(s, a))));
            Assert.Equal(1, results.Count(r => r!.Value is not null));
            Assert.Equal(1, results.Count(r => r!.AppointmentClosed));
            var snap = await ReadAsync(s, a);
            Assert.Equal(("completed", 100m, 10m, 20m), (snap.Status, snap.Cost, snap.Percent, snap.Payout));
        }
    }
}
