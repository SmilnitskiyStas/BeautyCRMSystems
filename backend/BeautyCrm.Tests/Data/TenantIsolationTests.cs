using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>Mandatory RLS isolation test for beauty_* (two tenants) + appointment overlap guard.</summary>
public sealed class TenantIsolationTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public TenantIsolationTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId);

    private async Task<Seed> SeedTenantAsync(string label)
    {
        var tenantId = Guid.NewGuid();
        await using var ctx = _db.CreateContext(tenantId);
        var location = new Location { Name = $"Salon {label}", Timezone = "Europe/Kyiv" };
        var specialist = new Specialist { FullName = $"Master {label}" };
        var service = new Service { Name = $"Haircut {label}", DurationMinutes = 60 };
        var client = new Client { FullName = $"Client {label}", Phone = "+380000000000" };
        ctx.AddRange(location, specialist, service, client);
        await ctx.SaveChangesAsync(); // tenant_id stamped from TenantContext
        return new Seed(tenantId, location.Id, specialist.Id, service.Id, client.Id);
    }

    private static Appointment NewAppointment(Seed s, DateTimeOffset startsAt, AppointmentStatus status = AppointmentStatus.Pending) => new()
    {
        LocationId = s.LocationId,
        SpecialistId = s.SpecialistId,
        ServiceId = s.ServiceId,
        ClientId = s.ClientId,
        StartsAt = startsAt,
        DurationMinutes = 60,
        Status = status,
        Source = AppointmentSource.Admin,
        PriceOriginal = 500m,
        PriceFinal = 500m,
    };

    private static string? SqlState(Exception ex) =>
        (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;

    [SkippableFact]
    public async Task Each_tenant_sees_only_its_own_rows()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync("A");
        var b = await SeedTenantAsync("B");

        await using (var ctxA = _db.CreateContext(a.TenantId))
        {
            var locations = await ctxA.Locations.ToListAsync();
            Assert.Single(locations);
            Assert.Equal(a.LocationId, locations[0].Id);
            Assert.Null(await ctxA.Clients.FirstOrDefaultAsync(c => c.Id == b.ClientId));
        }

        await using (var ctxB = _db.CreateContext(b.TenantId))
        {
            var locations = await ctxB.Locations.ToListAsync();
            Assert.Single(locations);
            Assert.Equal(b.LocationId, locations[0].Id);
            Assert.Null(await ctxB.Specialists.FirstOrDefaultAsync(x => x.Id == a.SpecialistId));
        }
    }

    [SkippableFact]
    public async Task Without_tenant_nothing_is_visible_and_inserts_are_rejected()
    {
        SkipIfNoDb();
        await SeedTenantAsync("A");

        await using var ctx = _db.CreateContext(tenantId: null);
        Assert.Equal(0, await ctx.Locations.CountAsync());
        Assert.Equal(0, await ctx.Clients.CountAsync());

        ctx.Locations.Add(new Location { TenantId = Guid.NewGuid(), Name = "x", Timezone = "UTC" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, SqlState(ex)); // RLS WITH CHECK
    }

    [SkippableFact]
    public async Task Tenant_cannot_insert_update_or_delete_rows_of_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync("A");
        var b = await SeedTenantAsync("B");

        await using (var ctxB = _db.CreateContext(b.TenantId))
        {
            ctxB.Locations.Add(new Location { TenantId = a.TenantId, Name = "spoofed", Timezone = "UTC" });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctxB.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, SqlState(ex));
        }

        await using (var ctxB = _db.CreateContext(b.TenantId))
        {
            var updated = await ctxB.Locations.Where(l => l.Id == a.LocationId)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.Name, "hijacked"));
            var deleted = await ctxB.Clients.Where(c => c.Id == a.ClientId).ExecuteDeleteAsync();
            Assert.Equal(0, updated);
            Assert.Equal(0, deleted);
        }

        await using (var ctxA = _db.CreateContext(a.TenantId))
        {
            var location = await ctxA.Locations.SingleAsync();
            Assert.Equal("Salon A", location.Name);
            Assert.True(await ctxA.Clients.AnyAsync(c => c.Id == a.ClientId));
        }
    }

    [SkippableFact]
    public async Task Tenant_cannot_reference_another_tenants_entities()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync("A");
        var b = await SeedTenantAsync("B");

        await using var ctxB = _db.CreateContext(b.TenantId);
        // B's appointment pointing at A's specialist: composite FK (tenant_id, specialist_id) fails
        ctxB.Appointments.Add(NewAppointment(b with { SpecialistId = a.SpecialistId }, DateTimeOffset.UtcNow.AddDays(1)));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctxB.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, SqlState(ex));
    }

    [SkippableFact]
    public async Task Specialist_cannot_have_overlapping_active_appointments()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync("A");
        var start = new DateTimeOffset(2030, 1, 15, 10, 0, 0, TimeSpan.Zero);

        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var first = NewAppointment(a, start);
            ctx.Appointments.Add(first);
            await ctx.SaveChangesAsync();
            Assert.Equal(start.AddMinutes(60), first.EndsAt); // set by trigger, read back
        }

        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Appointments.Add(NewAppointment(a, start.AddMinutes(30)));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, SqlState(ex));
        }

        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Appointments.Add(NewAppointment(a, start.AddMinutes(30), AppointmentStatus.Cancelled)); // cancelled doesn't block
            ctx.Appointments.Add(NewAppointment(a, start.AddMinutes(60)));                              // back-to-back is fine
            await ctx.SaveChangesAsync();
            Assert.Equal(3, await ctx.Appointments.CountAsync());
        }
    }
}
