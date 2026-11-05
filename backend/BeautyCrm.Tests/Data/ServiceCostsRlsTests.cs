using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-762 (§24.3, ADR-014): beauty_service_costs, beauty_service_cost_audit, знімок собівартості на beauty_appointments.
/// RLS/FORCE, UNIQUE з NULL location, CHECK, композитні FK, дефолти, аудит без значень (append-only за GRANT), CHECK знімка.
/// </summary>
public sealed class ServiceCostsRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public ServiceCostsRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId, Guid LocationA, Guid LocationB, Guid ServiceId, Guid SpecialistId, Guid ClientId);

    private int _day = 1;

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $@"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'cost-{tenantId:N}');
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

    private async Task<Guid> AddAppointmentAsync(Seed s, string status = "pending", decimal price = 100m)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var appt = new Appointment
        {
            LocationId = s.LocationA, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = s.ClientId,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = price, PriceFinal = price,
            Status = status == "completed" ? AppointmentStatus.Completed : AppointmentStatus.Pending,
        };
        ctx.Add(appt);
        await ctx.SaveChangesAsync();
        return appt.Id;
    }

    private static string Cost(Seed s, string? location = null, string cost = "100.00", string percent = "NULL", Guid? service = null, Guid? user = null) =>
        $@"INSERT INTO beauty_service_costs (tenant_id, service_id, location_id, cost_amount, master_percent, updated_by_user_id)
           VALUES ('{s.TenantId}', '{service ?? s.ServiceId}', {(location is null ? "NULL" : $"'{location}'")}, {cost}, {percent}, '{user ?? s.UserId}')";

    private static string Audit(Seed s, string action = "set", string fields = "ARRAY['cost_amount']", string? location = null, Guid? service = null) =>
        $@"INSERT INTO beauty_service_cost_audit (tenant_id, service_id, location_id, user_id, action, changed_fields)
           VALUES ('{s.TenantId}', '{service ?? s.ServiceId}', {(location is null ? "NULL" : $"'{location}'")}, '{s.UserId}', '{action}', {fields}::text[])";

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

    private async Task<long> Count(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM ({sql}) q";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        return await cmd.ExecuteScalarAsync();
    }

    // ---------- RLS / FORCE ----------

    [SkippableFact]
    public async Task force_rls_and_tenant_isolation_policies_exist_on_new_and_related_tables()
    {
        SkipIfNoDb();
        foreach (var table in new[] { "beauty_service_costs", "beauty_service_cost_audit", "beauty_appointments", "beauty_services", "beauty_service_prices" })
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
        foreach (var table in new[] { "beauty_service_costs", "beauty_service_cost_audit" })
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation'
                AND qual IS NOT NULL AND with_check IS NOT NULL"));
    }

    [SkippableFact]
    public async Task costs_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, Cost(a));
        await Run(a.TenantId, Audit(a));

        foreach (var (table, set) in new[] { ("beauty_service_costs", "cost_amount = 1"), ("beauty_service_cost_audit", "action = 'delete'") })
        {
            Assert.Equal(0, await Run(b.TenantId, $"UPDATE {table} SET {set}"));
            Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM {table}"));
            Assert.Equal(0L, await Count(b.TenantId, $"SELECT 1 FROM {table}"));
            Assert.Equal(0L, await Count(null, $"SELECT 1 FROM {table}")); // fail-closed
            Assert.Equal(1L, await Count(a.TenantId, $"SELECT 1 FROM {table}"));
        }
    }

    [SkippableFact]
    public async Task writing_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Rejects(b.TenantId, Cost(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(null, Cost(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(b.TenantId, Audit(a), PostgresErrorCodes.InsufficientPrivilege);
        await Run(b.TenantId, Cost(b));
        await Rejects(b.TenantId, $"UPDATE beauty_service_costs SET tenant_id = '{a.TenantId}'", PostgresErrorCodes.InsufficientPrivilege);
    }

    // ---------- CHECK ----------

    [SkippableFact]
    public async Task cost_amount_and_master_percent_ranges_are_enforced()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Rejects(s.TenantId, Cost(s, cost: "-0.01"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_costs_cost");
        await Rejects(s.TenantId, Cost(s, cost: "1000000.01"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_costs_cost");
        await Rejects(s.TenantId, Cost(s, percent: "-0.01"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_costs_master_percent");
        await Rejects(s.TenantId, Cost(s, percent: "100.01"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_costs_master_percent");

        await Run(s.TenantId, Cost(s, cost: "0", percent: "0"));                        // межі включно
        await Run(s.TenantId, Cost(s, location: s.LocationA.ToString(), cost: "1000000", percent: "100"));
        await Run(s.TenantId, Cost(s, location: s.LocationB.ToString(), cost: "10.005", percent: "33.335")); // numeric(12,2) / (5,2)
        Assert.Equal(10.01m, (decimal)(await Scalar($"SELECT cost_amount FROM beauty_service_costs WHERE location_id = '{s.LocationB}'"))!);
        Assert.Equal(33.34m, (decimal)(await Scalar($"SELECT master_percent FROM beauty_service_costs WHERE location_id = '{s.LocationB}'"))!);
        await Rejects(s.TenantId, "UPDATE beauty_service_costs SET cost_amount = -1", PostgresErrorCodes.CheckViolation);
    }

    [SkippableFact]
    public async Task audit_action_and_changed_fields_are_constrained_and_carry_no_values()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Rejects(s.TenantId, Audit(s, action: "update"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_cost_audit_action");
        await Rejects(s.TenantId, Audit(s, fields: "ARRAY['cost_amount','price']"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_cost_audit_changed_fields");
        await Rejects(s.TenantId, Audit(s, fields: "ARRAY['master_percent','123.45']"), PostgresErrorCodes.CheckViolation, "ck_beauty_service_cost_audit_changed_fields");

        await Run(s.TenantId, Audit(s, fields: "ARRAY['cost_amount','master_percent']"));
        await Run(s.TenantId, Audit(s, action: "delete", fields: "ARRAY[]", location: s.LocationA.ToString()));

        // у таблиці аудиту немає жодної колонки для значень
        Assert.Equal("id,tenant_id,service_id,location_id,user_id,action,changed_fields,created_at",
            string.Join(',', await Columns("beauty_service_cost_audit")));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM pg_attribute WHERE attrelid = 'beauty_service_cost_audit'::regclass AND attname = 'changed_fields' AND attnotnull"));
    }

    private async Task<List<string>> Columns(string table)
    {
        var list = new List<string>();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' ORDER BY ordinal_position", admin);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    // ---------- UNIQUE (NULLS NOT DISTINCT) ----------

    [SkippableFact]
    public async Task one_network_row_and_at_most_one_override_per_location()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var other = await SeedTenantAsync();
        await Run(s.TenantId, Cost(s));                                         // мережа
        await Rejects(s.TenantId, Cost(s, cost: "5"), PostgresErrorCodes.UniqueViolation, "ux_beauty_service_costs_scope"); // друга мережева (NULL = NULL)
        await Run(s.TenantId, Cost(s, location: s.LocationA.ToString()));       // мережа + override A
        await Run(s.TenantId, Cost(s, location: s.LocationB.ToString()));       // + override B
        await Rejects(s.TenantId, Cost(s, location: s.LocationA.ToString(), cost: "7"), PostgresErrorCodes.UniqueViolation, "ux_beauty_service_costs_scope");
        Assert.Equal(3L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_costs"));

        // іншу послугу цього tenant і інший tenant це не зачіпає
        await Run(other.TenantId, Cost(other));
        await Run(other.TenantId, Cost(other, location: other.LocationA.ToString()));

        Assert.Contains("NULLS NOT DISTINCT",
            (string)(await Scalar("SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_beauty_service_costs_scope'"))!);
    }

    // ---------- FK ----------

    [SkippableFact]
    public async Task composite_fks_reject_foreign_tenant_references()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Rejects(a.TenantId, Cost(a, service: b.ServiceId), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(a.TenantId, Cost(a, location: b.LocationA.ToString()), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(a.TenantId, Cost(a, user: b.UserId), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(a.TenantId, Audit(a, service: b.ServiceId), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(a.TenantId, Audit(a, location: b.LocationA.ToString()), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(a.TenantId, Cost(a, service: Guid.NewGuid()), PostgresErrorCodes.ForeignKeyViolation);
    }

    [SkippableFact]
    public async Task deleting_service_or_location_cascades_and_deleting_the_author_is_restricted()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Run(s.TenantId, Cost(s));
        await Run(s.TenantId, Cost(s, location: s.LocationA.ToString()));
        await Run(s.TenantId, Audit(s));
        await Run(s.TenantId, Audit(s, location: s.LocationA.ToString()));

        // автор: RESTRICT (адміністративний DELETE від власника схеми)
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Admin($"DELETE FROM users WHERE id = '{s.UserId}'"));
        Assert.Contains(ex.SqlState, new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });

        // заклад: CASCADE лише для свого override/аудиту; мережеві рядки лишаються
        await Run(s.TenantId, $"DELETE FROM beauty_locations WHERE id = '{s.LocationA}'");
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_costs"));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_cost_audit"));

        // послуга: CASCADE на решту
        await Run(s.TenantId, $"DELETE FROM beauty_services WHERE id = '{s.ServiceId}'");
        Assert.Equal(0L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_costs"));
        Assert.Equal(0L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_cost_audit"));
    }

    private async Task Admin(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    // ---------- дефолти / індекси ----------

    [SkippableFact]
    public async Task defaults_and_fk_indexes_are_in_place()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Run(s.TenantId, Cost(s, cost: "12.5"));
        await Run(s.TenantId, Audit(s));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_costs WHERE id IS NOT NULL AND master_percent IS NULL AND created_at IS NOT NULL AND updated_at IS NOT NULL AND location_id IS NULL AND cost_amount = 12.50"));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_service_cost_audit WHERE id IS NOT NULL AND created_at IS NOT NULL"));

        // індекс на кожен FK (перша колонка tenant_id + колонка FK)
        foreach (var name in new[]
        {
            "ux_beauty_service_costs_scope", "ix_beauty_service_costs_tenant_id_location_id", "ix_beauty_service_costs_tenant_id_updated_by_user_id",
            "ix_beauty_service_cost_audit_tenant_id_service_id_created_at", "ix_beauty_service_cost_audit_tenant_id_location_id", "ix_beauty_service_cost_audit_tenant_id_user_id",
        })
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_indexes WHERE indexname = '{name}'"));
    }

    // ---------- append-only аудит (модель GRANT з runbook: SELECT/INSERT) ----------

    [SkippableFact]
    public async Task audit_is_append_only_for_a_role_granted_select_and_insert()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var role = $"beauty_audit_t_{Guid.NewGuid():N}"[..28];
        await Admin($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await Admin($"GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT ON beauty_service_cost_audit TO {role}");
            await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
            await conn.OpenAsync();
            async Task Exec(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await Exec($"SET ROLE {role}");
            await Exec($"SELECT set_config('app.tenant_id', '{s.TenantId}', false)");
            await Exec(Audit(s));
            var upd = await Assert.ThrowsAsync<PostgresException>(() => Exec("UPDATE beauty_service_cost_audit SET action = 'delete'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, upd.SqlState);
            var del = await Assert.ThrowsAsync<PostgresException>(() => Exec("DELETE FROM beauty_service_cost_audit"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, del.SqlState);
            await Exec("RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }

    // ---------- знімок на beauty_appointments ----------

    private static string Snap(Guid id, string set) => $"UPDATE beauty_appointments SET {set} WHERE id = '{id}'";

    [SkippableFact]
    public async Task appointment_snapshot_defaults_to_null_and_is_only_allowed_for_completed()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var pending = await AddAppointmentAsync(s);
        Assert.Equal(1L, await Count(s.TenantId, $"SELECT 1 FROM beauty_appointments WHERE id = '{pending}' AND cost_snapshot IS NULL AND master_percent_snapshot IS NULL AND master_payout_snapshot IS NULL AND cost_captured_at IS NULL"));

        // будь-яке поле знімка поза completed відхиляється
        await Rejects(s.TenantId, Snap(pending, "cost_snapshot = 10, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_status");
        await Rejects(s.TenantId, Snap(pending, "cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_status");
        await Rejects(s.TenantId, Snap(pending, "status = 'cancelled', cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_status");
        await Rejects(s.TenantId, Snap(pending, "status = 'no_show', cost_snapshot = 1, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_status");

        // завершення без собівартості: лише cost_captured_at (запис «без собівартості»)
        Assert.Equal(1, await Run(s.TenantId, Snap(pending, "status = 'completed', cost_captured_at = now()")));
    }

    [SkippableFact]
    public async Task appointment_snapshot_values_and_consistency_are_enforced()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var id = await AddAppointmentAsync(s, price: 333.33m);

        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = -0.01, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_values");
        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 1, master_payout_snapshot = -1, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_values");
        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 1, master_percent_snapshot = 100.01, master_payout_snapshot = 1, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_values");
        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 1, master_percent_snapshot = -1, master_payout_snapshot = 1, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_values");

        // cost без cost_captured_at; виплата без cost; відсоток без виплати
        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 1"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_consistency");
        await Rejects(s.TenantId, Snap(id, "status = 'completed', master_payout_snapshot = 1, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_consistency");
        await Rejects(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 1, master_percent_snapshot = 10, cost_captured_at = now()"), PostgresErrorCodes.CheckViolation, "ck_beauty_appointments_cost_consistency");

        // повний валідний знімок (111.0988 -> 111.10 рахує застосунок; БД лише зберігає numeric(12,2))
        Assert.Equal(1, await Run(s.TenantId, Snap(id, "status = 'completed', cost_snapshot = 120, master_percent_snapshot = 33.33, master_payout_snapshot = 111.1, cost_captured_at = now()")));
        Assert.Equal(1L, await Count(s.TenantId, $"SELECT 1 FROM beauty_appointments WHERE id = '{id}' AND cost_snapshot = 120.00 AND master_percent_snapshot = 33.33 AND master_payout_snapshot = 111.10"));

        // нуль - допустима собівартість і виплата (акція 100%)
        var zero = await AddAppointmentAsync(s);
        Assert.Equal(1, await Run(s.TenantId, Snap(zero, "status = 'completed', cost_snapshot = 0, master_percent_snapshot = 0, master_payout_snapshot = 0, cost_captured_at = now()")));
    }

    [SkippableFact]
    public async Task snapshot_columns_have_the_expected_types()
    {
        SkipIfNoDb();
        foreach (var (col, precision, scale) in new[] { ("cost_snapshot", 12, 2), ("master_payout_snapshot", 12, 2), ("master_percent_snapshot", 5, 2) })
        {
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_appointments'
                AND column_name = '{col}' AND data_type = 'numeric' AND numeric_precision = {precision} AND numeric_scale = {scale} AND is_nullable = 'YES'"));
        }
        Assert.Equal(1L, await Scalar(@"SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_appointments'
            AND column_name = 'cost_captured_at' AND data_type = 'timestamp with time zone' AND is_nullable = 'YES'"));
    }
}
