using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-736 (§22.3, ADR-012): схема нагадувань, вподобань, задач «зателефонувати» та inbox.
/// RLS/FORCE, композитні FK, CHECK, UNIQUE, дефолти, відсутність місця для ПД. Backfill і Down: RemindersContactsInboxMigrationTests.
/// </summary>
public sealed class RemindersContactsInboxRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public RemindersContactsInboxRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(
        Guid TenantId, Guid UserId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, Guid ChannelId,
        Guid ConversationId, Guid MessageId, Guid AppointmentId, Guid ReminderId);

    private int _day = 1;

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            var slug = "rem-" + tenantId.ToString("N")[..20];
            await using var cmd = new NpgsqlCommand($@"
                INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}');
                INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                VALUES ('{userId}', '{tenantId}', 'a-{userId:N}@example.test', 'Admin', 'x', 'admin');", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var ctx = _db.CreateContext(tenantId);
        var loc = new Location { Name = "L", Timezone = "Europe/Kyiv" };
        var spec = new Specialist { FullName = "S" };
        var svc = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "Клієнт", Phone = "+380501112233" };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, spec, svc, client, ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ClientId = client.Id, ExternalChatId = "100" };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        var msg = new Message
        {
            ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "m", Status = "sending",
        };
        var appt = NewAppointment(loc.Id, spec.Id, svc.Id, client.Id);
        ctx.AddRange(msg, appt);
        await ctx.SaveChangesAsync();
        var rem = new Reminder { AppointmentId = appt.Id, ScheduledAt = DateTimeOffset.UtcNow.AddDays(1) };
        ctx.Add(rem);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, userId, loc.Id, spec.Id, svc.Id, client.Id, ch.Id, conv.Id, msg.Id, appt.Id, rem.Id);
    }

    private Appointment NewAppointment(Guid loc, Guid spec, Guid svc, Guid client) => new()
    {
        LocationId = loc, SpecialistId = spec, ServiceId = svc, ClientId = client,
        StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
        Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m,
    };

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    /// <summary>Виконує SQL під tenant-роллю й очікує помилку PostgreSQL із заданим станом (і, якщо задано, іменем обмеження).</summary>
    private async Task Rejects(Guid? tenant, string sql, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(tenant, sql));
        Assert.Equal(state, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
    }

    // Сирий SQL через ADO (не ExecuteSqlRaw: фігурні дужки масивів '{60}' ламають string.Format); tenant ставить interceptor при відкритті.
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

    private static string InsertPref(Seed s, string method = "telegram", string offsets = "{120}", string source = "chat", string extraCols = "", string extraVals = "") =>
        $@"INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, source{extraCols})
           VALUES ('{s.TenantId}', '{s.ClientId}', '{method}', '{offsets}', '{source}'{extraVals})";

    private static string InsertTask(Seed s, string cols = "", string vals = "", string reason = "reminder", string status = "open") =>
        $@"INSERT INTO beauty_call_tasks (tenant_id, appointment_id, client_id, location_id, reason, due_at, status{cols})
           VALUES ('{s.TenantId}', '{s.AppointmentId}', '{s.ClientId}', '{s.LocationId}', '{reason}', now(), '{status}'{vals})";

    // ---------- RLS ----------

    [SkippableFact]
    public async Task new_tables_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, InsertPref(a));
        await Run(a.TenantId, InsertTask(a));
        await Run(a.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id) VALUES ('{a.TenantId}')");

        var tables = new (string Table, string Update, string Key)[]
        {
            ("beauty_client_contact_preferences", "source = 'admin'", $"client_id = '{a.ClientId}'"),
            ("beauty_call_tasks", "note = 'x'", $"appointment_id = '{a.AppointmentId}'"),
            ("beauty_reminder_settings", "call_enabled = false", $"tenant_id = '{a.TenantId}'"),
        };
        foreach (var (table, update, key) in tables)
        {
            Assert.Equal(0, await Run(b.TenantId, $"UPDATE {table} SET {update} WHERE {key}"));
            Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM {table} WHERE {key}"));
            Assert.Equal(0L, await Count(b.TenantId, $"SELECT 1 FROM {table}"));
            Assert.Equal(0L, await Count(null, $"SELECT 1 FROM {table}")); // без контексту - fail-closed
            Assert.Equal(1L, await Count(a.TenantId, $"SELECT 1 FROM {table}"));
        }
    }

    [SkippableFact]
    public async Task writing_a_row_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        foreach (var sql in new[]
                 {
                     InsertPref(a), InsertTask(a), $"INSERT INTO beauty_reminder_settings (tenant_id) VALUES ('{a.TenantId}')",
                 })
        {
            await Rejects(b.TenantId, sql, PostgresErrorCodes.InsufficientPrivilege);
            await Rejects(null, sql, PostgresErrorCodes.InsufficientPrivilege);
        }
        // UPDATE не може "перенести" рядок до іншого tenant
        await Run(b.TenantId, InsertPref(b));
        await Rejects(b.TenantId, $"UPDATE beauty_client_contact_preferences SET tenant_id = '{a.TenantId}' WHERE client_id = '{b.ClientId}'",
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [SkippableFact]
    public async Task force_rls_and_policy_are_active_on_new_and_altered_tables()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[]
                 {
                     "beauty_client_contact_preferences", "beauty_call_tasks", "beauty_reminder_settings",
                     "beauty_appointments", "beauty_reminders", "beauty_conversations", "beauty_messages", "beauty_clients",
                 })
        {
            await using var cmd = new NpgsqlCommand($@"
                SELECT c.relrowsecurity, c.relforcerowsecurity,
                       (SELECT count(*) FROM pg_policies p WHERE p.schemaname = 'public' AND p.tablename = c.relname AND p.policyname = 'tenant_isolation')
                FROM pg_class c WHERE c.oid = to_regclass('public.{table}')", admin);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.True(r.GetBoolean(0), table);
            Assert.True(r.GetBoolean(1), table);
            Assert.Equal(1L, r.GetInt64(2));
        }

        // політики нових таблиць мають і USING, і WITH CHECK
        await using var pol = new NpgsqlCommand(@"
            SELECT count(*) FROM pg_policies
            WHERE schemaname = 'public' AND policyname = 'tenant_isolation' AND qual IS NOT NULL AND with_check IS NOT NULL
              AND tablename IN ('beauty_client_contact_preferences', 'beauty_call_tasks', 'beauty_reminder_settings')", admin);
        Assert.Equal(3L, await pol.ExecuteScalarAsync());
    }

    // ---------- композитні FK ----------

    [SkippableFact]
    public async Task composite_fks_reject_references_to_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        // вподобання: клієнт / розмова / користувач іншого tenant
        await Rejects(b.TenantId, $@"INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, offsets_minutes, source)
            VALUES ('{b.TenantId}', '{a.ClientId}', 'none', '{{}}', 'stop')", PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, InsertPref(b, extraCols: ", telegram_conversation_id", extraVals: $", '{a.ConversationId}'"), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, InsertPref(b, source: "admin", extraCols: ", updated_by_user_id", extraVals: $", '{a.UserId}'"), PostgresErrorCodes.ForeignKeyViolation);

        // задачі: запис / клієнт / заклад / нагадування / користувач
        foreach (var (col, foreign) in new[]
                 {
                     ("appointment_id", a.AppointmentId), ("client_id", a.ClientId), ("location_id", a.LocationId),
                 })
        {
            var s = new Dictionary<string, Guid>
            {
                ["appointment_id"] = b.AppointmentId, ["client_id"] = b.ClientId, ["location_id"] = b.LocationId,
            };
            s[col] = foreign;
            await Rejects(b.TenantId, $@"INSERT INTO beauty_call_tasks (tenant_id, appointment_id, client_id, location_id, reason, due_at)
                VALUES ('{b.TenantId}', '{s["appointment_id"]}', '{s["client_id"]}', '{s["location_id"]}', 'reminder', now())",
                PostgresErrorCodes.ForeignKeyViolation);
        }
        await Rejects(b.TenantId, InsertTask(b, ", reminder_id", $", '{a.ReminderId}'"), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, InsertTask(b, ", completed_by_user_id, completed_at", $", '{a.UserId}', now()", status: "done"), PostgresErrorCodes.ForeignKeyViolation);

        // налаштування: користувач іншого tenant
        await Rejects(b.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id, updated_by_user_id) VALUES ('{b.TenantId}', '{a.UserId}')",
            PostgresErrorCodes.ForeignKeyViolation);

        // змінювані таблиці
        await Rejects(b.TenantId, $"UPDATE beauty_reminders SET message_id = '{a.MessageId}' WHERE id = '{b.ReminderId}'", PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{a.AppointmentId}',
            pending_prompt_expires_at = now() WHERE id = '{b.ConversationId}'", PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, $"UPDATE beauty_messages SET sender_type = 'staff', sender_user_id = '{a.UserId}' WHERE id = '{b.MessageId}'",
            PostgresErrorCodes.ForeignKeyViolation);

        // свої посилання проходять
        await Run(b.TenantId, InsertPref(b, source: "admin", extraCols: ", telegram_conversation_id, updated_by_user_id", extraVals: $", '{b.ConversationId}', '{b.UserId}'"));
        await Run(b.TenantId, InsertTask(b, ", reminder_id", $", '{b.ReminderId}'"));
        await Run(b.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id, updated_by_user_id) VALUES ('{b.TenantId}', '{b.UserId}')");
        await Run(b.TenantId, $"UPDATE beauty_reminders SET message_id = '{b.MessageId}' WHERE id = '{b.ReminderId}'");
        await Run(b.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{b.AppointmentId}',
            pending_prompt_expires_at = now() WHERE id = '{b.ConversationId}'");
        await Run(b.TenantId, $"UPDATE beauty_messages SET sender_type = 'staff', sender_user_id = '{b.UserId}' WHERE id = '{b.MessageId}'");
    }

    [SkippableFact]
    public async Task delete_behaviour_restrict_for_references_and_cascade_from_client_to_preferences()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Run(a.TenantId, InsertPref(a, source: "admin", extraCols: ", telegram_conversation_id, updated_by_user_id", extraVals: $", '{a.ConversationId}', '{a.UserId}'"));
        await Run(a.TenantId, InsertTask(a, ", reminder_id", $", '{a.ReminderId}'"));
        await Run(a.TenantId, $"UPDATE beauty_reminders SET message_id = '{a.MessageId}' WHERE id = '{a.ReminderId}'");
        await Run(a.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{a.AppointmentId}',
            pending_prompt_expires_at = now() WHERE id = '{a.ConversationId}'");
        await Run(a.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id, updated_by_user_id) VALUES ('{a.TenantId}', '{a.UserId}')");

        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        // RESTRICT: розмови, користувачі, записи, нагадування, заклади, повідомлення не видаляються, поки на них є посилання
        foreach (var sql in new[]
                 {
                     $"DELETE FROM beauty_conversations WHERE id = '{a.ConversationId}'",
                     $"DELETE FROM users WHERE id = '{a.UserId}'",
                     $"DELETE FROM beauty_reminders WHERE id = '{a.ReminderId}'",
                     $"DELETE FROM beauty_locations WHERE id = '{a.LocationId}'",
                     $"DELETE FROM beauty_messages WHERE id = '{a.MessageId}'",
                     $"DELETE FROM beauty_appointments WHERE id = '{a.AppointmentId}'",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, admin);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }
        // задача блокує і видалення клієнта (RESTRICT)...
        await using (var del = new NpgsqlCommand($"DELETE FROM beauty_clients WHERE id = '{a.ClientId}'", admin))
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, (await Assert.ThrowsAsync<PostgresException>(() => del.ExecuteNonQueryAsync())).SqlState);

        // ...а вподобання без задач і записів іде разом з клієнтом (CASCADE)
        var c = await SeedTenantAsync();
        await Run(c.TenantId, InsertPref(c));
        await using var cleanup = new NpgsqlCommand($@"
            DELETE FROM beauty_reminders WHERE tenant_id = '{c.TenantId}';
            DELETE FROM beauty_appointments WHERE tenant_id = '{c.TenantId}';
            DELETE FROM beauty_conversations WHERE tenant_id = '{c.TenantId}';
            DELETE FROM beauty_clients WHERE id = '{c.ClientId}';", admin);
        await cleanup.ExecuteNonQueryAsync();
        await using var left = new NpgsqlCommand($"SELECT count(*) FROM beauty_client_contact_preferences WHERE tenant_id = '{c.TenantId}'", admin);
        Assert.Equal(0L, await left.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task every_new_foreign_key_has_a_covering_index()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT c.conname FROM pg_constraint c
            WHERE c.contype = 'f' AND (
                  c.conrelid IN ('beauty_client_contact_preferences'::regclass, 'beauty_call_tasks'::regclass, 'beauty_reminder_settings'::regclass)
               OR c.conname IN ('fk_beauty_reminders_beauty_messages_tenant_id_message_id',
                                'fk_beauty_conversations_beauty_appointments_tenant_id_pending_',
                                'fk_beauty_messages_users_tenant_id_sender_user_id'))
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index i
                  WHERE i.indrelid = c.conrelid
                    AND (string_to_array(i.indkey::text, ' ')::int2[])[1:cardinality(c.conkey)] = c.conkey)", admin);
        var missing = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) missing.Add(r.GetString(0));
        Assert.Empty(missing);

        // кількість FK не менша за очікувану (захист від порожнього збігу)
        await using var cnt = new NpgsqlCommand(@"
            SELECT count(*) FROM pg_constraint WHERE contype = 'f'
              AND conrelid IN ('beauty_client_contact_preferences'::regclass, 'beauty_call_tasks'::regclass, 'beauty_reminder_settings'::regclass)", admin);
        Assert.Equal(3L + 5L + 1L, await cnt.ExecuteScalarAsync());
    }

    // ---------- beauty_appointments ----------

    [SkippableFact]
    public async Task appointment_reminder_columns_default_to_none_and_empty()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(a.TenantId);
        var ap = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId);
        Assert.Equal("none", ap.ReminderMethod);
        Assert.Empty(ap.ReminderOffsets);
        Assert.Equal(ReminderOption.None, ap.ReminderOption);
    }

    [SkippableFact]
    public async Task appointment_reminder_plan_checks()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        const string C = "ck_beauty_appointments_";
        var bad = new (string Set, string Constraint)[]
        {
            ("reminder_method = 'sms'", C + "reminder_method"),
            ("reminder_method = 'telegram', reminder_offsets = '{30}'", C + "reminder_offsets"),
            ("reminder_method = 'telegram', reminder_offsets = '{60,60,120,1440}'", C + "reminder_offsets"), // cardinality > 3
            ("reminder_method = 'telegram', reminder_offsets = '{60,120,1440,60}'", C + "reminder_offsets"),
            ("reminder_method = 'telegram', reminder_offsets = '{}'", C + "reminder_none_empty"),   // не none, але порожньо
            ("reminder_method = 'none', reminder_offsets = '{60}'", C + "reminder_none_empty"),     // none, але не порожньо
            ("reminder_method = 'auto', reminder_offsets = '{}'", C + "reminder_none_empty"),
            ("reminder_method = 'call', reminder_offsets = '{60,120}'", C + "reminder_call_single"),
            ("reminder_option = 'weekly'", C + "reminder_option"),
        };
        foreach (var (set, constraint) in bad)
            await Rejects(a.TenantId, $"UPDATE beauty_appointments SET {set} WHERE id = '{a.AppointmentId}'", PostgresErrorCodes.CheckViolation, constraint);

        // допустимі комбінації (повний набір методів і точок, custom як дзеркало)
        foreach (var set in new[]
                 {
                     "reminder_method = 'auto', reminder_offsets = '{60}', reminder_option = '1h'",
                     "reminder_method = 'auto', reminder_offsets = '{120}', reminder_option = '2h'",
                     "reminder_method = 'telegram', reminder_offsets = '{120,1440}', reminder_option = 'custom'",
                     "reminder_method = 'telegram', reminder_offsets = '{60,120,1440}', reminder_option = 'custom'",
                     "reminder_method = 'call', reminder_offsets = '{1440}', reminder_option = 'custom'",
                     "reminder_method = 'none', reminder_offsets = '{}', reminder_option = 'none'",
                 })
            Assert.Equal(1, await Run(a.TenantId, $"UPDATE beauty_appointments SET {set} WHERE id = '{a.AppointmentId}'"));
    }

    [SkippableFact]
    public async Task appointment_reminder_option_custom_round_trips_through_ef()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var ap = NewAppointment(a.LocationId, a.SpecialistId, a.ServiceId, a.ClientId);
            ap.ReminderOption = ReminderOption.Custom;
            ap.ReminderMethod = "telegram";
            ap.ReminderOffsets = [120, 1440];
            ctx.Add(ap);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var ap = await ctx.Appointments.AsNoTracking().SingleAsync(x => x.ReminderOption == ReminderOption.Custom);
            Assert.Equal("telegram", ap.ReminderMethod);
            Assert.Equal(new short[] { 120, 1440 }, ap.ReminderOffsets);
        }
    }

    // ---------- beauty_reminders ----------

    [SkippableFact]
    public async Task reminder_new_columns_are_nullable_and_checks_allow_skipped_and_methods()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var r = await ctx.Reminders.AsNoTracking().SingleAsync(x => x.Id == a.ReminderId);
            Assert.Null(r.OffsetMinutes);
            Assert.Null(r.Method);
            Assert.Null(r.PlannedStartsAt);
            Assert.Null(r.SkipReason);
            Assert.Null(r.MessageId);
            Assert.Equal(ReminderStatus.Scheduled, r.Status);
        }
        await Rejects(a.TenantId, $"UPDATE beauty_reminders SET method = 'sms' WHERE id = '{a.ReminderId}'", PostgresErrorCodes.CheckViolation, "ck_beauty_reminders_method");
        await Rejects(a.TenantId, $"UPDATE beauty_reminders SET status = 'paused' WHERE id = '{a.ReminderId}'", PostgresErrorCodes.CheckViolation, "ck_beauty_reminders_status");
        foreach (var m in new[] { "telegram", "auto" })
            await Run(a.TenantId, $"UPDATE beauty_reminders SET method = '{m}' WHERE id = '{a.ReminderId}'");
        foreach (var st in new[] { "scheduled", "sent", "failed", "cancelled", "skipped" })
            await Run(a.TenantId, $"UPDATE beauty_reminders SET status = '{st}' WHERE id = '{a.ReminderId}'");
        // skip_reason - код ≤ 32 символів, місця для довгого тексту немає
        await Run(a.TenantId, $"UPDATE beauty_reminders SET skip_reason = '{new string('x', 32)}' WHERE id = '{a.ReminderId}'");
        await Rejects(a.TenantId, $"UPDATE beauty_reminders SET skip_reason = '{new string('x', 33)}' WHERE id = '{a.ReminderId}'", PostgresErrorCodes.StringDataRightTruncation);
    }

    [SkippableFact]
    public async Task reminder_point_is_unique_per_appointment_only_while_scheduled_and_with_offset()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        string Ins(Seed s, string offset, string status = "scheduled") =>
            $@"INSERT INTO beauty_reminders (tenant_id, appointment_id, scheduled_at, status, offset_minutes)
               VALUES ('{s.TenantId}', '{s.AppointmentId}', now(), '{status}', {offset})";

        await Run(a.TenantId, Ins(a, "120"));
        await Rejects(a.TenantId, Ins(a, "120"), PostgresErrorCodes.UniqueViolation, "ux_beauty_reminders_point");
        await Run(a.TenantId, Ins(a, "60"));                // інша точка
        await Run(a.TenantId, Ins(a, "120", "cancelled"));  // неактивні дублікати дозволені
        await Run(a.TenantId, Ins(a, "120", "sent"));
        await Run(a.TenantId, Ins(a, "NULL"));              // спадкові рядки (offset NULL) не обмежуються
        await Run(a.TenantId, Ins(a, "NULL"));
        await Run(b.TenantId, Ins(b, "120"));               // той самий offset в іншому tenant
        // скасований рядок звільняє точку
        await Run(a.TenantId, $"UPDATE beauty_reminders SET status = 'cancelled' WHERE appointment_id = '{a.AppointmentId}' AND offset_minutes = 120 AND status = 'scheduled'");
        await Run(a.TenantId, Ins(a, "120"));
    }

    // ---------- beauty_client_contact_preferences ----------

    [SkippableFact]
    public async Task preferences_checks_pk_defaults_and_updated_by_only_for_admin()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        const string C = "ck_beauty_client_contact_preferences_";
        var bad = new (string Sql, string Constraint)[]
        {
            (InsertPref(a, method: "sms"), C + "method"),
            (InsertPref(a, source: "api"), C + "source"),
            (InsertPref(a, offsets: "{30}"), C + "offsets"),
            (InsertPref(a, method: "telegram", offsets: "{60,60,120,1440}"), C + "offsets"),
            (InsertPref(a, method: "telegram", offsets: "{}"), C + "none_empty"),
            (InsertPref(a, method: "none", offsets: "{60}", source: "stop"), C + "none_empty"),
            (InsertPref(a, method: "call", offsets: "{60,120}"), C + "call_single"),
            (InsertPref(a, method: "call", offsets: "{}"), C + "call_single"),
            (InsertPref(a, source: "chat", extraCols: ", updated_by_user_id", extraVals: $", '{a.UserId}'"), C + "updated_by"),
            (InsertPref(a, method: "none", offsets: "{}", source: "auto"), C + "source"),
        };
        foreach (var (sql, constraint) in bad)
            await Rejects(a.TenantId, sql, PostgresErrorCodes.CheckViolation, constraint);

        // 'auto' допустимий лише на записі, не у вподобанні
        await Rejects(a.TenantId, InsertPref(a, method: "auto", offsets: "{60}"), PostgresErrorCodes.CheckViolation, C + "method");

        await Run(a.TenantId, InsertPref(a, method: "call", offsets: "{1440}", source: "admin", extraCols: ", updated_by_user_id", extraVals: $", '{a.UserId}'"));
        // один рядок на клієнта (PK tenant_id + client_id)
        await Rejects(a.TenantId, InsertPref(a), PostgresErrorCodes.UniqueViolation, "pk_beauty_client_contact_preferences");
        await Run(a.TenantId, $"DELETE FROM beauty_client_contact_preferences WHERE client_id = '{a.ClientId}'");
        await Run(a.TenantId, InsertPref(a, method: "none", offsets: "{}", source: "stop"));

        await using var ctx = _db.CreateContext(a.TenantId);
        var p = await ctx.ClientContactPreferences.AsNoTracking().SingleAsync();
        Assert.Equal("none", p.Method);
        Assert.Empty(p.OffsetsMinutes);
        Assert.Null(p.TelegramConversationId);
        Assert.Null(p.UpdatedByUserId);
        Assert.NotEqual(default, p.CreatedAt);
        Assert.NotEqual(default, p.UpdatedAt);

        // offsets_minutes має DEFAULT '{}' (стан «none» без явної вказівки точок)
        var c2 = await SeedTenantAsync();
        await Run(c2.TenantId, $"INSERT INTO beauty_client_contact_preferences (tenant_id, client_id, method, source) VALUES ('{c2.TenantId}', '{c2.ClientId}', 'none', 'stop')");
    }

    // ---------- beauty_call_tasks ----------

    [SkippableFact]
    public async Task call_task_checks_defaults_and_completion_pairing()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        const string C = "ck_beauty_call_tasks_";
        await Rejects(a.TenantId, InsertTask(a, reason: "manual"), PostgresErrorCodes.CheckViolation, C + "reason");
        await Rejects(a.TenantId, InsertTask(a, status: "pending"), PostgresErrorCodes.CheckViolation, C + "status");
        // done|skipped потребують обох полів
        foreach (var st in new[] { "done", "skipped" })
        {
            await Rejects(a.TenantId, InsertTask(a, status: st), PostgresErrorCodes.CheckViolation, C + "completed");
            await Rejects(a.TenantId, InsertTask(a, ", completed_by_user_id", $", '{a.UserId}'", status: st), PostgresErrorCodes.CheckViolation, C + "completed");
            await Rejects(a.TenantId, InsertTask(a, ", completed_at", ", now()", status: st), PostgresErrorCodes.CheckViolation, C + "completed");
        }
        // open|cancelled не можуть мати автора/час
        foreach (var st in new[] { "open", "cancelled" })
        {
            await Rejects(a.TenantId, InsertTask(a, ", completed_by_user_id, completed_at", $", '{a.UserId}', now()", reason: "delivery_failed", status: st),
                PostgresErrorCodes.CheckViolation, C + "completed");
            await Rejects(a.TenantId, InsertTask(a, ", completed_at", ", now()", reason: "delivery_failed", status: st), PostgresErrorCodes.CheckViolation, C + "completed");
        }
        await Rejects(a.TenantId, InsertTask(a, ", note", $", '{new string('n', 301)}'"), PostgresErrorCodes.StringDataRightTruncation);
        await Rejects(a.TenantId, InsertTask(a, ", closed_reason", $", '{new string('c', 33)}'"), PostgresErrorCodes.StringDataRightTruncation);

        // дефолти: status = open, closed_reason / автор / offset порожні
        await Run(a.TenantId, $@"INSERT INTO beauty_call_tasks (tenant_id, appointment_id, client_id, location_id, reason, due_at)
            VALUES ('{a.TenantId}', '{a.AppointmentId}', '{a.ClientId}', '{a.LocationId}', 'reminder', now())");
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var t = await ctx.CallTasks.AsNoTracking().SingleAsync();
            Assert.Equal("open", t.Status);
            Assert.Null(t.ClosedReason);
            Assert.Null(t.CompletedByUserId);
            Assert.Null(t.CompletedAt);
            Assert.Null(t.OffsetMinutes);
            Assert.Null(t.ReminderId);
            Assert.Null(t.Note);
            Assert.NotEqual(Guid.Empty, t.Id);
            Assert.NotEqual(default, t.CreatedAt);
        }
        // закриття: done з автором і часом; повернення в open очищає обидва
        await Run(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'done', completed_by_user_id = '{a.UserId}', completed_at = now() WHERE appointment_id = '{a.AppointmentId}'");
        await Rejects(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'open' WHERE appointment_id = '{a.AppointmentId}'", PostgresErrorCodes.CheckViolation, C + "completed");
        await Run(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'open', completed_by_user_id = NULL, completed_at = NULL WHERE appointment_id = '{a.AppointmentId}'");
        await Run(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'skipped', completed_by_user_id = '{a.UserId}', completed_at = now(), note = '{new string('n', 300)}' WHERE appointment_id = '{a.AppointmentId}'");
        await Run(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'cancelled', completed_by_user_id = NULL, completed_at = NULL, closed_reason = 'replanned' WHERE appointment_id = '{a.AppointmentId}'");
    }

    [SkippableFact]
    public async Task call_task_unique_per_reminder_and_one_open_reminder_task_per_appointment()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await Run(a.TenantId, InsertTask(a, ", reminder_id", $", '{a.ReminderId}'", reason: "delivery_failed"));
        // повтор задачі delivery_failed для того самого нагадування (повтор полера)
        await Rejects(a.TenantId, InsertTask(a, ", reminder_id", $", '{a.ReminderId}'", reason: "delivery_failed"), PostgresErrorCodes.UniqueViolation, "ux_beauty_call_tasks_reminder");
        // delivery_failed не конфліктує з відкритою reminder-задачею запису
        await Run(a.TenantId, InsertTask(a));
        await Rejects(a.TenantId, InsertTask(a), PostgresErrorCodes.UniqueViolation, "ux_beauty_call_tasks_open_reminder");
        // кілька delivery_failed на запис дозволено (без reminder_id)
        await Run(a.TenantId, InsertTask(a, reason: "delivery_failed"));
        await Run(a.TenantId, InsertTask(a, reason: "delivery_failed"));
        // закрита reminder-задача звільняє місце
        await Run(a.TenantId, $"UPDATE beauty_call_tasks SET status = 'cancelled', closed_reason = 'replanned' WHERE appointment_id = '{a.AppointmentId}' AND reason = 'reminder'");
        await Run(a.TenantId, InsertTask(a));
        // історія закритих не обмежена
        await Run(a.TenantId, InsertTask(a, status: "cancelled"));
        await Run(a.TenantId, InsertTask(a, status: "cancelled"));
        // ізоляція ключів між tenant
        await Run(b.TenantId, InsertTask(b, ", reminder_id", $", '{b.ReminderId}'"));
    }

    // ---------- beauty_reminder_settings ----------

    [SkippableFact]
    public async Task reminder_settings_defaults_one_row_per_tenant_and_quiet_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Run(a.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id) VALUES ('{a.TenantId}')");
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var s = await ctx.ReminderSettings.AsNoTracking().SingleAsync();
            Assert.True(s.TelegramEnabled);
            Assert.True(s.CallEnabled);
            Assert.Equal(new TimeOnly(22, 0), s.QuietStart);
            Assert.Equal(new TimeOnly(8, 0), s.QuietEnd);
            Assert.Null(s.UpdatedByUserId);
            Assert.NotEqual(default, s.UpdatedAt);
        }
        await Rejects(a.TenantId, $"INSERT INTO beauty_reminder_settings (tenant_id) VALUES ('{a.TenantId}')", PostgresErrorCodes.UniqueViolation, "pk_beauty_reminder_settings");
        await Rejects(a.TenantId, $"UPDATE beauty_reminder_settings SET quiet_end = quiet_start WHERE tenant_id = '{a.TenantId}'", PostgresErrorCodes.CheckViolation, "ck_beauty_reminder_settings_quiet");

        // EF записує false і північ (не підміняє їх дефолтами БД)
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new ReminderSettingsRow { TenantId = b.TenantId, TelegramEnabled = false, CallEnabled = false, QuietStart = new TimeOnly(0, 0), QuietEnd = new TimeOnly(6, 30) });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            var s = await ctx.ReminderSettings.AsNoTracking().SingleAsync();
            Assert.False(s.TelegramEnabled);
            Assert.False(s.CallEnabled);
            Assert.Equal(new TimeOnly(0, 0), s.QuietStart);
            Assert.Equal(new TimeOnly(6, 30), s.QuietEnd);
        }
    }

    // ---------- beauty_conversations / beauty_messages ----------

    [SkippableFact]
    public async Task conversation_inbox_columns_default_null_and_pending_prompt_is_all_or_none()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var c = await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == a.ConversationId);
            Assert.Null(c.AwaitingStaffSince);
            Assert.Null(c.StaffLastReadAt);
            Assert.Null(c.StaffRepliedAt);
            Assert.Null(c.HandoffAt);
            Assert.Null(c.RemindersStopAt);
            Assert.Null(c.PendingPrompt);
            Assert.Null(c.PendingPromptAppointmentId);
            Assert.Null(c.PendingPromptExpiresAt);
            Assert.Equal("active", c.AiState);
        }

        const string All = "ck_beauty_conversations_pending_prompt_all_or_none";
        await Rejects(a.TenantId, $"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice' WHERE id = '{a.ConversationId}'", PostgresErrorCodes.CheckViolation, All);
        await Rejects(a.TenantId, $"UPDATE beauty_conversations SET pending_prompt_expires_at = now() WHERE id = '{a.ConversationId}'", PostgresErrorCodes.CheckViolation, All);
        await Rejects(a.TenantId, $"UPDATE beauty_conversations SET pending_prompt_appointment_id = '{a.AppointmentId}' WHERE id = '{a.ConversationId}'", PostgresErrorCodes.CheckViolation, All);
        await Rejects(a.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{a.AppointmentId}'
            WHERE id = '{a.ConversationId}'", PostgresErrorCodes.CheckViolation, All);
        await Rejects(a.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'other', pending_prompt_appointment_id = '{a.AppointmentId}',
            pending_prompt_expires_at = now() WHERE id = '{a.ConversationId}'", PostgresErrorCodes.CheckViolation, "ck_beauty_conversations_pending_prompt");
        Assert.Equal(1, await Run(a.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{a.AppointmentId}',
            pending_prompt_expires_at = now() + interval '1 day', awaiting_staff_since = now(), staff_last_read_at = now(), staff_replied_at = now(),
            handoff_at = now(), reminders_stop_at = now() WHERE id = '{a.ConversationId}'"));
        Assert.Equal(1, await Run(a.TenantId, $@"UPDATE beauty_conversations SET pending_prompt = NULL, pending_prompt_appointment_id = NULL,
            pending_prompt_expires_at = NULL WHERE id = '{a.ConversationId}'"));
    }

    [SkippableFact]
    public async Task message_sender_user_only_for_staff_and_held_status_is_accepted()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        string Ins(string sender, string direction, string status, string? user) =>
            $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, sender_user_id)
               VALUES ('{a.TenantId}', '{a.ConversationId}', '{direction}', '{sender}', 'b', '{status}', {(user is null ? "NULL" : $"'{user}'")})";

        foreach (var sender in new[] { "client", "ai", "system" })
            await Rejects(a.TenantId, Ins(sender, "outbound", "sending", a.UserId.ToString()), PostgresErrorCodes.CheckViolation, "ck_beauty_messages_sender_user");
        await Run(a.TenantId, Ins("staff", "outbound", "sending", a.UserId.ToString()));
        await Run(a.TenantId, Ins("staff", "outbound", "sending", null)); // спадкові повідомлення без автора
        // held - вхідні під час handoff; CHECK на status немає (довжина 16)
        await Run(a.TenantId, Ins("client", "inbound", "held", null));
        await Run(a.TenantId, $"UPDATE beauty_messages SET status = 'processed' WHERE conversation_id = '{a.ConversationId}' AND status = 'held'");
        await using var ctx = _db.CreateContext(a.TenantId);
        Assert.Null((await ctx.Messages.AsNoTracking().SingleAsync(x => x.Id == a.MessageId)).SenderUserId);
    }

    [SkippableFact]
    public async Task inbox_indexes_exist_with_expected_shape()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT indexname, indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND indexname IN (
                'ix_beauty_conversations_awaiting_staff', 'ix_beauty_conversations_last_message_desc',
                'ix_beauty_conversations_tenant_id_last_message_at', 'ux_beauty_reminders_point',
                'ux_beauty_call_tasks_reminder', 'ux_beauty_call_tasks_open_reminder',
                'ix_beauty_call_tasks_tenant_id_status_due_at', 'ix_beauty_call_tasks_tenant_id_location_id_due_at',
                'ix_beauty_reminders_tenant_id_appointment_id')", admin);
        var defs = new Dictionary<string, string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) defs[r.GetString(0)] = r.GetString(1);
        Assert.Equal(9, defs.Count);
        Assert.Contains("WHERE (awaiting_staff_since IS NOT NULL)", defs["ix_beauty_conversations_awaiting_staff"]);
        Assert.Contains("last_message_at DESC", defs["ix_beauty_conversations_last_message_desc"]);
        Assert.DoesNotContain("WHERE", defs["ix_beauty_conversations_last_message_desc"]);
        Assert.Contains("WHERE", defs["ix_beauty_conversations_tenant_id_last_message_at"]); // спадковий індекс open-розмов не замінено
        Assert.Contains("UNIQUE", defs["ux_beauty_reminders_point"]);
        Assert.Contains("UNIQUE", defs["ux_beauty_call_tasks_reminder"]);
        Assert.Contains("UNIQUE", defs["ux_beauty_call_tasks_open_reminder"]);
        Assert.DoesNotContain("WHERE", defs["ix_beauty_reminders_tenant_id_appointment_id"]); // повний індекс FK збережено
    }

    // ---------- ПД ----------

    [SkippableFact]
    public async Task new_tables_have_no_columns_for_phone_names_or_message_text()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();

        async Task<List<string>> Columns(string table)
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' ORDER BY ordinal_position", admin);
            var list = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(r.GetString(0));
            return list;
        }

        // call_tasks: ПД (телефон, ім'я, текст) не копіюються; єдиний вільний текст - note (≤ 300)
        Assert.Equal(new[]
        {
            "id", "appointment_id", "client_id", "location_id", "reminder_id", "reason", "offset_minutes", "due_at", "status", "closed_reason",
            "completed_by_user_id", "completed_at", "note", "tenant_id", "created_at", "updated_at",
        }.OrderBy(x => x), (await Columns("beauty_call_tasks")).OrderBy(x => x));

        // вподобання й налаштування: лише коди, числа, прапорці й посилання
        Assert.Equal(new[]
        {
            "tenant_id", "client_id", "method", "offsets_minutes", "telegram_conversation_id", "source", "updated_by_user_id", "created_at", "updated_at",
        }.OrderBy(x => x), (await Columns("beauty_client_contact_preferences")).OrderBy(x => x));
        Assert.Equal(new[]
        {
            "tenant_id", "telegram_enabled", "call_enabled", "quiet_start", "quiet_end", "updated_by_user_id", "updated_at",
        }.OrderBy(x => x), (await Columns("beauty_reminder_settings")).OrderBy(x => x));

        foreach (var table in new[] { "beauty_call_tasks", "beauty_client_contact_preferences", "beauty_reminder_settings" })
            foreach (var col in await Columns(table))
                foreach (var forbidden in new[] { "phone", "email", "name", "body", "text", "chat_id", "token" })
                    Assert.DoesNotContain(forbidden, col);

        // reminders: нові колонки - лише обмежені за довжиною коди (varchar), без text
        await using var cmd2 = new NpgsqlCommand(@"
            SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'beauty_reminders' AND column_name IN ('method', 'skip_reason')
            ORDER BY column_name", admin);
        await using var r2 = await cmd2.ExecuteReaderAsync();
        var seen = 0;
        while (await r2.ReadAsync())
        {
            Assert.Equal("character varying", r2.GetString(1));
            Assert.InRange(r2.GetInt32(2), 1, 32);
            seen++;
        }
        Assert.Equal(2, seen);
    }
}
