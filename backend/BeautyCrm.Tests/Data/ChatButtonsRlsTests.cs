using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-749 (§23.3, ADR-013): beauty_messages (kind=callback, actions, callback_*), beauty_chat_keyboards,
/// beauty_appointments.client_confirmed_at. RLS/FORCE, композитні FK, CHECK, UNIQUE, дефолти, відсутність місця для ПД.
/// </summary>
public sealed class ChatButtonsRlsTests : IClassFixture<PostgresRlsFixture>
{
    private const string Tok1 = "AAAAAAAAAAAAAAAAAAAAAA";
    private const string Tok2 = "BBBBBBBBBBBBBBBBBBBBBB";

    private readonly PostgresRlsFixture _db;

    public ChatButtonsRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(
        Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, Guid ConversationId,
        Guid OutboundId, Guid OutboundId2, Guid InboundId, Guid AppointmentId, Guid ProposalId);

    private int _day = 1;

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'btn-{tenantId:N}');", admin);
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
        Message Out() => new()
        {
            ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.Ai, Body = "m", Status = "sent",
        };
        var out1 = Out();
        var out2 = Out();
        var inb = new Message { ConversationId = conv.Id, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Body = "[кнопка]", Kind = "callback" };
        var appt = new Appointment
        {
            LocationId = loc.Id, SpecialistId = spec.Id, ServiceId = svc.Id, ClientId = client.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m,
        };
        ctx.AddRange(out1, out2, inb, appt);
        await ctx.SaveChangesAsync();
        var proposal = new ChatBookingProposal
        {
            ConversationId = conv.Id, ClientId = client.Id, LocationId = loc.Id, SpecialistId = spec.Id, ServiceId = svc.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(20), DurationMinutes = 60, PriceOriginal = 100m, PriceFinal = 100m,
            ConfirmationTokenHash = new string('a', 64), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        ctx.Add(proposal);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, loc.Id, spec.Id, svc.Id, client.Id, conv.Id, out1.Id, out2.Id, inb.Id, appt.Id, proposal.Id);
    }

    private static string Sql(string s) => s.Replace("'", "''");

    /// <summary>INSERT клавіатури; за замовчуванням валідний appointment_actions для запису.</summary>
    private static string InsertKb(Seed s, Guid? messageId = null, string kind = "appointment_actions", string token = Tok1, string options = "[]",
        string? proposal = "-", string? appointment = "-", string status = "active", string extraCols = "", string extraVals = "")
    {
        var prop = proposal == "-" ? (kind == "booking_confirm" ? $"'{s.ProposalId}'" : "NULL") : proposal is null ? "NULL" : $"'{proposal}'";
        var appt = appointment == "-" ? (kind is "appointment_actions" or "cancel_confirm" or "reminder_choice" ? $"'{s.AppointmentId}'" : "NULL") : appointment is null ? "NULL" : $"'{appointment}'";
        return $@"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, proposal_id, appointment_id, status, expires_at{extraCols})
           VALUES ('{s.TenantId}', '{s.ConversationId}', '{messageId ?? s.OutboundId}', '{kind}', '{token}', '{Sql(options)}'::jsonb, {prop}, {appt}, '{status}', now() + interval '30 minutes'{extraVals})";
    }

    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

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

    // ---------- RLS ----------

    [SkippableFact]
    public async Task keyboards_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, InsertKb(a));
        const string key = "token = 'AAAAAAAAAAAAAAAAAAAAAA'";

        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_chat_keyboards SET status = 'revoked' WHERE {key}"));
        Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM beauty_chat_keyboards WHERE {key}"));
        Assert.Equal(0L, await Count(b.TenantId, "SELECT 1 FROM beauty_chat_keyboards"));
        Assert.Equal(0L, await Count(null, "SELECT 1 FROM beauty_chat_keyboards")); // fail-closed
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_chat_keyboards"));

        // той самий токен в іншому tenant - окремий рядок (UNIQUE per tenant)
        await Run(b.TenantId, InsertKb(b));
        Assert.Equal(1L, await Count(b.TenantId, $"SELECT 1 FROM beauty_chat_keyboards WHERE {key}"));
    }

    [SkippableFact]
    public async Task writing_a_keyboard_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Rejects(b.TenantId, InsertKb(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(null, InsertKb(a), PostgresErrorCodes.InsufficientPrivilege);
        await Run(b.TenantId, InsertKb(b));
        await Rejects(b.TenantId, $"UPDATE beauty_chat_keyboards SET tenant_id = '{a.TenantId}' WHERE conversation_id = '{b.ConversationId}'",
            PostgresErrorCodes.InsufficientPrivilege);
    }

    [SkippableFact]
    public async Task force_rls_and_policy_are_active_on_new_and_altered_tables()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_chat_keyboards", "beauty_messages", "beauty_appointments" })
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
        await using var pol = new NpgsqlCommand(@"
            SELECT count(*) FROM pg_policies
            WHERE schemaname = 'public' AND tablename = 'beauty_chat_keyboards' AND policyname = 'tenant_isolation'
              AND qual IS NOT NULL AND with_check IS NOT NULL", admin);
        Assert.Equal(1L, await pol.ExecuteScalarAsync());
    }

    // ---------- композитні FK ----------

    [SkippableFact]
    public async Task composite_fks_reject_references_to_another_tenant_and_own_references_pass()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        // чужа розмова
        await Rejects(b.TenantId, InsertKb(b).Replace($"'{b.ConversationId}'", $"'{a.ConversationId}'"), PostgresErrorCodes.ForeignKeyViolation);
        // чуже повідомлення
        await Rejects(b.TenantId, InsertKb(b, a.OutboundId), PostgresErrorCodes.ForeignKeyViolation);
        // чужа пропозиція / чужий запис
        await Rejects(b.TenantId, InsertKb(b, kind: "booking_confirm", proposal: a.ProposalId.ToString()), PostgresErrorCodes.ForeignKeyViolation);
        await Rejects(b.TenantId, InsertKb(b, appointment: a.AppointmentId.ToString()), PostgresErrorCodes.ForeignKeyViolation);
        // чуже повідомлення як used_by
        await Rejects(b.TenantId, InsertKb(b, status: "used", extraCols: ", used_at, used_by_message_id", extraVals: $", now(), '{a.InboundId}'"),
            PostgresErrorCodes.ForeignKeyViolation);

        // свої посилання проходять (усі п'ять FK)
        await Run(b.TenantId, InsertKb(b, kind: "booking_confirm", token: Tok1));
        await Run(b.TenantId, InsertKb(b, b.OutboundId2, token: Tok2, status: "used", extraCols: ", used_at, used_by_message_id, used_option",
            extraVals: $", now(), '{b.InboundId}', 0"));
    }

    [SkippableFact]
    public async Task references_are_restricted_and_every_new_foreign_key_has_a_covering_index()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Run(a.TenantId, InsertKb(a, kind: "booking_confirm"));
        await Run(a.TenantId, InsertKb(a, a.OutboundId2, token: Tok2, status: "used", extraCols: ", used_at, used_by_message_id",
            extraVals: $", now(), '{a.InboundId}'"));

        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var sql in new[]
                 {
                     $"DELETE FROM beauty_conversations WHERE id = '{a.ConversationId}'",
                     $"DELETE FROM beauty_messages WHERE id = '{a.OutboundId}'",
                     $"DELETE FROM beauty_messages WHERE id = '{a.InboundId}'",
                     $"DELETE FROM beauty_chat_booking_proposals WHERE id = '{a.ProposalId}'",
                 })
        {
            await using var cmd = new NpgsqlCommand(sql, admin);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }

        await using var idx = new NpgsqlCommand(@"
            SELECT c.conname FROM pg_constraint c
            WHERE c.contype = 'f' AND c.conrelid = 'beauty_chat_keyboards'::regclass
              AND NOT EXISTS (
                  SELECT 1 FROM pg_index i
                  WHERE i.indrelid = c.conrelid AND i.indpred IS NULL
                    AND (string_to_array(i.indkey::text, ' ')::int2[])[1:cardinality(c.conkey)] = c.conkey)", admin);
        var missing = new List<string>();
        await using (var r = await idx.ExecuteReaderAsync())
            while (await r.ReadAsync()) missing.Add(r.GetString(0));
        Assert.Empty(missing); // індекс на кожен FK, частковий не рахується

        await using var cnt = new NpgsqlCommand("SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND conrelid = 'beauty_chat_keyboards'::regclass", admin);
        Assert.Equal(5L, await cnt.ExecuteScalarAsync());
    }

    // ---------- beauty_chat_keyboards: CHECK / UNIQUE / дефолти ----------

    [SkippableFact]
    public async Task keyboard_checks_kind_token_options_status()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        const string C = "ck_beauty_chat_keyboards_";

        await Rejects(a.TenantId, InsertKb(a, kind: "other"), PostgresErrorCodes.CheckViolation, C + "kind");
        foreach (var token in new[] { new string('A', 21), new string('A', 23), "AAAAAAAAAAAAAAAAAAAAA=", "AAAAAAAAAAAAAAAAAAAA+/", "AAAAAAAAAAAAAAAAAAAA A" })
            await Rejects(a.TenantId, InsertKb(a, token: token), token.Length > 22 ? PostgresErrorCodes.StringDataRightTruncation : PostgresErrorCodes.CheckViolation,
                token.Length > 22 ? null : C + "token");
        await Rejects(a.TenantId, InsertKb(a, options: "{}"), PostgresErrorCodes.CheckViolation, C + "options");
        await Rejects(a.TenantId, InsertKb(a, options: "\"x\""), PostgresErrorCodes.CheckViolation, C + "options");
        await Rejects(a.TenantId, InsertKb(a, options: "[" + string.Join(",", Enumerable.Repeat("{}", 11)) + "]"), PostgresErrorCodes.CheckViolation, C + "options");
        await Rejects(a.TenantId, InsertKb(a, status: "pending"), PostgresErrorCodes.CheckViolation, C + "status");
        await Rejects(a.TenantId, $@"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, appointment_id)
            VALUES ('{a.TenantId}', '{a.ConversationId}', '{a.OutboundId}', 'appointment_actions', '{Tok1}', '[]', '{a.AppointmentId}')",
            PostgresErrorCodes.NotNullViolation); // expires_at обов'язковий

        // усі допустимі kind (з потрібними посиланнями) і 10 options
        var kinds = new[] { "booking_confirm", "slot_pick", "reminder_choice", "appointment_list", "appointment_actions", "cancel_confirm" };
        foreach (var kind in kinds)
        {
            await Run(a.TenantId, $"DELETE FROM beauty_chat_keyboards WHERE tenant_id = '{a.TenantId}'");
            await Run(a.TenantId, InsertKb(a, kind: kind, options: "[" + string.Join(",", Enumerable.Repeat("{\"verb\":\"sp\"}", 10)) + "]"));
        }
    }

    [SkippableFact]
    public async Task keyboard_kind_requires_proposal_or_appointment_reference()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Rejects(a.TenantId, InsertKb(a, kind: "booking_confirm", proposal: null), PostgresErrorCodes.CheckViolation, "ck_beauty_chat_keyboards_proposal");
        foreach (var kind in new[] { "appointment_actions", "cancel_confirm", "reminder_choice" })
            await Rejects(a.TenantId, InsertKb(a, kind: kind, appointment: null), PostgresErrorCodes.CheckViolation, "ck_beauty_chat_keyboards_appointment");
        // slot_pick і appointment_list без пропозиції й запису
        await Run(a.TenantId, InsertKb(a, kind: "slot_pick"));
        await Run(a.TenantId, InsertKb(a, a.OutboundId2, "appointment_list", Tok2));
    }

    [SkippableFact]
    public async Task keyboard_used_fields_are_set_if_and_only_if_status_used()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Run(a.TenantId, InsertKb(a));
        const string C = "ck_beauty_chat_keyboards_";
        const string where = "WHERE conversation_id = '{0}'";
        string W() => string.Format(where, a.ConversationId);

        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = 'used' {W()}", PostgresErrorCodes.CheckViolation, C + "used");
        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = 'used', used_at = now() {W()}", PostgresErrorCodes.CheckViolation, C + "used");
        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = 'used', used_by_message_id = '{a.InboundId}' {W()}", PostgresErrorCodes.CheckViolation, C + "used");
        foreach (var st in new[] { "expired", "revoked" })
        {
            await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = '{st}', used_at = now() {W()}", PostgresErrorCodes.CheckViolation, C + "used");
            await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = '{st}', used_by_message_id = '{a.InboundId}' {W()}", PostgresErrorCodes.CheckViolation, C + "used");
        }
        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET used_option = 1 {W()}", PostgresErrorCodes.CheckViolation, C + "used_option"); // не used
        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET status = 'used', used_at = now(), used_by_message_id = '{a.InboundId}', used_option = 10 {W()}",
            PostgresErrorCodes.CheckViolation, C + "used_option");
        await Rejects(a.TenantId, $"UPDATE beauty_chat_keyboards SET clear_attempts = -1 {W()}", PostgresErrorCodes.CheckViolation, C + "clear_attempts");

        Assert.Equal(1, await Run(a.TenantId,
            $"UPDATE beauty_chat_keyboards SET status = 'used', used_at = now(), used_by_message_id = '{a.InboundId}', used_option = 2 {W()}"));
        Assert.Equal(1, await Run(a.TenantId,
            $"UPDATE beauty_chat_keyboards SET status = 'expired', used_at = NULL, used_by_message_id = NULL, used_option = NULL {W()}"));
    }

    [SkippableFact]
    public async Task keyboard_unique_per_message_and_per_tenant_token_and_defaults()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await Run(a.TenantId, InsertKb(a));
        // одна клавіатура на повідомлення
        await Rejects(a.TenantId, InsertKb(a, token: Tok2), PostgresErrorCodes.UniqueViolation, "ix_beauty_chat_keyboards_tenant_id_message_id");
        // токен унікальний у межах tenant
        await Rejects(a.TenantId, InsertKb(a, a.OutboundId2), PostgresErrorCodes.UniqueViolation, "ix_beauty_chat_keyboards_tenant_id_token");
        await Run(a.TenantId, InsertKb(a, a.OutboundId2, token: Tok2));

        // дефолти: status=active, clear_attempts=0, nullable-поля порожні, id/created_at/updated_at проставлено
        await using var ctx = _db.CreateContext(a.TenantId);
        var k = await ctx.ChatKeyboards.AsNoTracking().SingleAsync(x => x.Token == Tok1);
        Assert.Equal("active", k.Status);
        Assert.Equal(0, k.ClearAttempts);
        Assert.Null(k.UsedAt);
        Assert.Null(k.UsedOption);
        Assert.Null(k.UsedByMessageId);
        Assert.Null(k.MarkupClearedAt);
        Assert.Null(k.ProposalId);
        Assert.Equal(a.AppointmentId, k.AppointmentId);
        Assert.NotEqual(Guid.Empty, k.Id);
        Assert.NotEqual(default, k.CreatedAt);
        Assert.NotEqual(default, k.UpdatedAt);
    }

    [SkippableFact]
    public async Task keyboard_round_trips_through_ef_with_jsonb_options()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new ChatKeyboard
            {
                ConversationId = a.ConversationId, MessageId = a.OutboundId, Kind = "booking_confirm", Token = Tok1,
                Options = "[{\"verb\":\"by\",\"label\":\"Так\"}]", ProposalId = a.ProposalId, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var k = await ctx.ChatKeyboards.SingleAsync();
            Assert.Contains("\"verb\"", k.Options);
            Assert.Equal(a.ProposalId, k.ProposalId);
            Assert.Equal("active", k.Status);
            var before = k.UpdatedAt;
            k.Status = "revoked";
            await ctx.SaveChangesAsync();
            Assert.True(k.UpdatedAt >= before);
        }
    }

    [SkippableFact]
    public async Task keyboard_indexes_exist_with_expected_shape()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT indexname, indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'beauty_chat_keyboards'", admin);
        var defs = new Dictionary<string, string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) defs[r.GetString(0)] = r.GetString(1);

        Assert.Contains("status", defs["ix_beauty_chat_keyboards_active"].Split("WHERE")[1]);
        Assert.Contains("'active'", defs["ix_beauty_chat_keyboards_active"].Split("WHERE")[1]);
        Assert.Contains("(tenant_id, conversation_id)", defs["ix_beauty_chat_keyboards_active"]);
        var sweeper = defs["ix_beauty_chat_keyboards_sweeper"];
        Assert.Contains("(tenant_id, updated_at)", sweeper);
        Assert.Contains("status", sweeper);
        Assert.Contains("markup_cleared_at IS NULL", sweeper);
        Assert.DoesNotContain("WHERE", defs["ix_beauty_chat_keyboards_tenant_id_conversation_id"]);
        Assert.Contains("UNIQUE", defs["ix_beauty_chat_keyboards_tenant_id_message_id"]);
        Assert.Contains("UNIQUE", defs["ix_beauty_chat_keyboards_tenant_id_token"]);
    }

    // ---------- beauty_messages ----------

    [SkippableFact]
    public async Task message_kind_accepts_callback_and_still_rejects_unknown_values()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        string Ins(string kind, string extraCols = "", string extraVals = "", string direction = "inbound", string sender = "client") =>
            $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind{extraCols})
               VALUES ('{a.TenantId}', '{a.ConversationId}', '{direction}', '{sender}', 'b', '{kind}'{extraVals})";

        foreach (var kind in new[] { "text", "command", "unsupported", "callback" })
            await Run(a.TenantId, Ins(kind));
        await Rejects(a.TenantId, Ins("button"), PostgresErrorCodes.CheckViolation, "ck_beauty_messages_kind");
        await Rejects(a.TenantId, Ins("Callback"), PostgresErrorCodes.CheckViolation, "ck_beauty_messages_kind");

        // наявні рядки не змінено: дефолт kind = text, нові колонки порожні
        await using var ctx = _db.CreateContext(a.TenantId);
        var m = await ctx.Messages.AsNoTracking().SingleAsync(x => x.Id == a.OutboundId);
        Assert.Equal("text", m.Kind);
        Assert.Null(m.Actions);
        Assert.Null(m.CallbackData);
        Assert.Null(m.CallbackMessageRef);
        Assert.Null(m.ExternalMessageId);
    }

    [SkippableFact]
    public async Task message_callback_fields_only_for_callback_kind_and_are_length_bounded()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        string Ins(string kind, string data, string reference) =>
            $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind, callback_data, callback_message_ref)
               VALUES ('{a.TenantId}', '{a.ConversationId}', 'inbound', 'client', 'b', '{kind}', {data}, {reference})";
        const string C = "ck_beauty_messages_callback_fields";

        foreach (var kind in new[] { "text", "command", "unsupported" })
        {
            await Rejects(a.TenantId, Ins(kind, "'by:AAAAAAAAAAAAAAAAAAAAAA'", "NULL"), PostgresErrorCodes.CheckViolation, C);
            await Rejects(a.TenantId, Ins(kind, "NULL", "'42'"), PostgresErrorCodes.CheckViolation, C);
        }
        // callback: обидва поля, жодне (невалідний формат) або одне з них
        await Run(a.TenantId, Ins("callback", "'by:AAAAAAAAAAAAAAAAAAAAAA'", "'42'"));
        await Run(a.TenantId, Ins("callback", "NULL", "NULL"));
        await Run(a.TenantId, Ins("callback", "NULL", "'42'"));
        await Run(a.TenantId, Ins("callback", $"'{new string('x', 64)}'", "'42'"));
        // довжина: callback_data ≤ 64, callback_message_ref ≤ 32
        await Rejects(a.TenantId, Ins("callback", $"'{new string('x', 65)}'", "NULL"), PostgresErrorCodes.StringDataRightTruncation);
        await Rejects(a.TenantId, Ins("callback", "NULL", $"'{new string('1', 33)}'"), PostgresErrorCodes.StringDataRightTruncation);
        // повернення kind у text при заповнених полях заборонено
        await Rejects(a.TenantId,
            $"UPDATE beauty_messages SET kind = 'text' WHERE conversation_id = '{a.ConversationId}' AND callback_data = 'by:AAAAAAAAAAAAAAAAAAAAAA'",
            PostgresErrorCodes.CheckViolation, C);
    }

    [SkippableFact]
    public async Task message_actions_only_for_outbound_json_object_up_to_4096_bytes()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        const string C = "ck_beauty_messages_actions";
        string Upd(Guid id, string json) => $"UPDATE beauty_messages SET actions = '{Sql(json)}'::jsonb WHERE id = '{id}'";

        const string layout = "{\"rows\":[[{\"label\":\"Так\",\"data\":\"by:AAAAAAAAAAAAAAAAAAAAAA\"}]]}";
        Assert.Equal(1, await Run(a.TenantId, Upd(a.OutboundId, layout)));
        Assert.Equal(1, await Run(a.TenantId, $"UPDATE beauty_messages SET actions = NULL WHERE id = '{a.OutboundId}'"));

        await Rejects(a.TenantId, Upd(a.InboundId, layout), PostgresErrorCodes.CheckViolation, C);  // вхідне
        await Rejects(a.TenantId, Upd(a.OutboundId, "[]"), PostgresErrorCodes.CheckViolation, C);   // не об'єкт
        await Rejects(a.TenantId, Upd(a.OutboundId, "\"x\""), PostgresErrorCodes.CheckViolation, C);
        await Rejects(a.TenantId, Upd(a.OutboundId, "null"), PostgresErrorCodes.CheckViolation, C); // jsonb null != SQL NULL

        // межа 4096 байт у текстовому поданні jsonb (actions::text додає пробіл: {"p": "xxx"} = 9 символів + n)
        string Pad(int total) => "{\"p\":\"" + new string('x', total - 9) + "\"}";
        Assert.Equal(1, await Run(a.TenantId, Upd(a.OutboundId, Pad(4096))));
        await Rejects(a.TenantId, Upd(a.OutboundId, Pad(4097)), PostgresErrorCodes.CheckViolation, C);

        // вставка вхідного з actions заборонена
        await Rejects(a.TenantId,
            $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, actions)
               VALUES ('{a.TenantId}', '{a.ConversationId}', 'inbound', 'client', 'b', '{layout}'::jsonb)",
            PostgresErrorCodes.CheckViolation, C);
    }

    [SkippableFact]
    public async Task outbound_external_message_id_is_writable_and_unchanged_column()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        Assert.Equal(1, await Run(a.TenantId, $"UPDATE beauty_messages SET external_message_id = '12345' WHERE id = '{a.OutboundId}'"));
        await Rejects(a.TenantId, $"UPDATE beauty_messages SET external_message_id = '{new string('1', 257)}' WHERE id = '{a.OutboundId}'",
            PostgresErrorCodes.StringDataRightTruncation);
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'beauty_messages' AND column_name = 'external_message_id'", admin);
        Assert.Equal(256, await cmd.ExecuteScalarAsync());
    }

    // ---------- beauty_appointments.client_confirmed_at ----------

    [SkippableFact]
    public async Task appointment_client_confirmed_at_is_nullable_and_round_trips()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
            Assert.Null((await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId)).ClientConfirmedAt);

        Assert.Equal(1, await Run(a.TenantId, $"UPDATE beauty_appointments SET client_confirmed_at = now() WHERE id = '{a.AppointmentId}'"));
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var ap = await ctx.Appointments.SingleAsync(x => x.Id == a.AppointmentId);
            Assert.NotNull(ap.ClientConfirmedAt);
            ap.ClientConfirmedAt = null;
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
            Assert.Null((await ctx.Appointments.AsNoTracking().SingleAsync(x => x.Id == a.AppointmentId)).ClientConfirmedAt);

        // чужий tenant не бачить і не змінює
        var b = await SeedTenantAsync();
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_appointments SET client_confirmed_at = now() WHERE id = '{a.AppointmentId}'"));
    }

    // ---------- ПД / довгі дані ----------

    [SkippableFact]
    public async Task keyboards_table_has_no_columns_for_personal_data_or_free_text()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'beauty_chat_keyboards' ORDER BY ordinal_position", admin);
        var cols = new Dictionary<string, (string Type, int? Len)>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) cols[r.GetString(0)] = (r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2));

        Assert.Equal(new[]
        {
            "id", "tenant_id", "conversation_id", "message_id", "kind", "token", "options", "proposal_id", "appointment_id", "status",
            "expires_at", "used_at", "used_option", "used_by_message_id", "markup_cleared_at", "clear_attempts", "created_at", "updated_at",
        }.OrderBy(x => x), cols.Keys.OrderBy(x => x));

        foreach (var col in cols.Keys)
            foreach (var forbidden in new[] { "phone", "email", "name", "body", "text", "chat_id", "callback_data", "label" })
                Assert.DoesNotContain(forbidden, col);
        // єдине неструктуроване поле - options (jsonb, ≤ 10 елементів); текстові колонки обмежені за довжиною
        Assert.DoesNotContain(cols.Values, v => v.Type == "text");
        Assert.Equal(("character varying", 22), (cols["token"].Type, cols["token"].Len!.Value));
        Assert.Equal(24, cols["kind"].Len);
        Assert.Equal(16, cols["status"].Len);
        Assert.Equal("jsonb", cols["options"].Type);

        // callback-колонки повідомлень - обмежені коди, не text
        await using var msg = new NpgsqlCommand(@"
            SELECT column_name, data_type, character_maximum_length FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'beauty_messages' AND column_name IN ('callback_data', 'callback_message_ref', 'actions')
            ORDER BY column_name", admin);
        var m = new Dictionary<string, (string, int?)>();
        await using var mr = await msg.ExecuteReaderAsync();
        while (await mr.ReadAsync()) m[mr.GetString(0)] = (mr.GetString(1), mr.IsDBNull(2) ? null : mr.GetInt32(2));
        Assert.Equal(("character varying", 64), (m["callback_data"].Item1, m["callback_data"].Item2!.Value));
        Assert.Equal(("character varying", 32), (m["callback_message_ref"].Item1, m["callback_message_ref"].Item2!.Value));
        Assert.Equal("jsonb", m["actions"].Item1);
    }
}
