using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-697: RLS і обмеження нових колонок beauty_appointments.cancelled_by_* / cancel_reason (роль без BYPASSRLS).</summary>
public sealed class CancellationHistoryRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public CancellationHistoryRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, Guid UserId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        // tenants/users створює суперкористувач (RLS обходить): тут важливі лише рядки для композитного FK
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            var slug = "hist-" + tenantId.ToString("N")[..20];
            await Exec(admin, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')");
            await Exec(admin, $@"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                                 VALUES ('{userId}', '{tenantId}', 'u@{slug}.test', 'Staff Name', 'x', 'admin')");
        }
        await using var ctx = _db.CreateContext(tenantId);
        var location = new Location { Name = "L", Timezone = "UTC" };
        var specialist = new Specialist { FullName = "S" };
        var service = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "C", Phone = "+380000000000" };
        ctx.AddRange(location, specialist, service, client);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, location.Id, specialist.Id, service.Id, client.Id, userId);
    }

    private static Appointment Appt(Seed s, int day, AppointmentStatus status = AppointmentStatus.Cancelled, Action<Appointment>? set = null)
    {
        var a = new Appointment
        {
            LocationId = s.LocationId, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = s.ClientId,
            StartsAt = new DateTimeOffset(2031, 1, 1, 10, 0, 0, TimeSpan.Zero).AddDays(day), DurationMinutes = 60, Status = status,
            Source = AppointmentSource.Admin, PriceOriginal = 1, PriceFinal = 1,
        };
        set?.Invoke(a);
        return a;
    }

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task cancellation_metadata_is_tenant_isolated_and_stored_when_valid()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        Guid id;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var row = Appt(a, 0, set: r =>
            {
                r.CancelledAt = DateTimeOffset.UtcNow;
                r.CancelledByType = "staff";
                r.CancelledByUserId = a.UserId;
                r.CancelReason = new string('r', 300);
            });
            ctx.Add(row);
            ctx.Add(Appt(a, 1, set: r => { r.CancelledByType = "client"; r.CancelledAt = DateTimeOffset.UtcNow; }));
            ctx.Add(Appt(a, 2, set: r => { r.CancelledByType = "system"; r.CancelledAt = DateTimeOffset.UtcNow; }));
            await ctx.SaveChangesAsync();
            id = row.Id;
        }

        await using var other = _db.CreateContext(b.TenantId);
        Assert.Empty(await other.Appointments.Where(x => x.CancelledByType != null).ToListAsync());
        Assert.Empty(await other.Appointments.Where(x => x.Id == id).ToListAsync());
        // чужий рядок не можна змінити (RLS USING відсікає його -> 0 рядків)
        Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_appointments SET cancel_reason = 'hijack' WHERE id = {id}"));
        Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_appointments WHERE id = {id}"));
        await using var own = _db.CreateContext(a.TenantId);
        var stored = await own.Appointments.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(("staff", (Guid?)a.UserId), (stored.CancelledByType, stored.CancelledByUserId));
        Assert.Equal(300, stored.CancelReason!.Length);
        // без tenant-контексту нічого не видно
        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.Appointments.ToListAsync());
    }

    [SkippableFact]
    public async Task cancelled_by_user_must_belong_to_the_same_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        foreach (var userId in new[] { a.UserId, Guid.NewGuid() }) // користувач чужого tenant / неіснуючий
        {
            await using var ctx = _db.CreateContext(b.TenantId);
            ctx.Add(Appt(b, 0, set: r => { r.CancelledByType = "staff"; r.CancelledByUserId = userId; r.CancelledAt = DateTimeOffset.UtcNow; }));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, SqlState(ex));
        }
    }

    [SkippableTheory]
    [InlineData("bogus", false, AppointmentStatus.Cancelled, "ck_beauty_appointments_cancelled_by_type")]
    [InlineData("client", true, AppointmentStatus.Cancelled, "ck_beauty_appointments_cancelled_by_user")]
    [InlineData("system", true, AppointmentStatus.Cancelled, "ck_beauty_appointments_cancelled_by_user")]
    [InlineData("client", false, AppointmentStatus.Pending, "ck_beauty_appointments_cancel_meta_status")]
    [InlineData("staff", true, AppointmentStatus.Confirmed, "ck_beauty_appointments_cancel_meta_status")]
    public async Task check_constraints_reject_inconsistent_cancellation_metadata(string type, bool withUser, AppointmentStatus status, string constraint)
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(a.TenantId);
        ctx.Add(Appt(a, 0, status, r =>
        {
            r.CancelledByType = type;
            r.CancelledByUserId = withUser ? a.UserId : null;
        }));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, SqlState(ex));
        Assert.Equal(constraint, ((PostgresException)ex.InnerException!).ConstraintName);
    }

    [SkippableFact]
    public async Task reason_without_cancelled_status_or_longer_than_300_is_rejected()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Appt(a, 0, AppointmentStatus.Pending, r => r.CancelReason = "x"));
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, SqlState(ex));
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Appt(a, 1, set: r => { r.CancelledByType = "system"; r.CancelReason = new string('x', 301); }));
            var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.NotNull(SqlState(ex)); // 22001 (varchar(300)) або 23514 (CHECK) - ніколи не збережено
        }
    }

    [SkippableFact]
    public async Task appointments_keep_force_row_level_security_after_the_migration()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = 'beauty_appointments'::regclass", admin);
        Assert.True((bool)(await cmd.ExecuteScalarAsync())!);
    }
}
