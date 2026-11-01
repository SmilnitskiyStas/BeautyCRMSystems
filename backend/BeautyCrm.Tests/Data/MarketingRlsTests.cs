using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-776 (§26.3, ADR-015, сценарій 28): beauty_marketing_consents, beauty_marketing_consent_events, beauty_campaigns,
/// beauty_campaign_recipients, beauty_coupons, beauty_coupon_redemptions + нові колонки наявних таблиць.
/// FORCE/RLS, ізоляція tenant, CHECK, UNIQUE (в т.ч. часткові), композитні FK, append-only events (GRANT), індекси на FK.
/// </summary>
public sealed class MarketingRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public MarketingRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(
        Guid TenantId, Guid UserId, Guid ClientId, Guid ClientB, Guid LocationId, Guid ServiceId, Guid SpecialistId, Guid PromotionId, Guid ConversationId, Guid MessageId);

    private int _day = 1;

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await Admin($@"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'mkt-{tenantId:N}');
                       INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                       VALUES ('{userId}', '{tenantId}', 'u@{tenantId:N}.test', 'Owner', 'x', 'owner');");

        await using var ctx = _db.CreateContext(tenantId);
        var loc = new Location { Name = "A", Timezone = "Europe/Kyiv" };
        var spec = new Specialist { FullName = "S" };
        var svc = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "Клієнт", Phone = "+380501112233" };
        var clientB = new Client { FullName = "Клієнт 2" };
        var promo = new Promotion { Name = "Лише за кодом", DiscountType = DiscountType.Percent, DiscountValue = 10, Access = "code_only" };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, spec, svc, client, clientB, promo, ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ClientId = client.Id, ExternalChatId = Guid.NewGuid().ToString("N")[..8] };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        var msg = new Message { ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "акція" };
        ctx.Add(msg);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, userId, client.Id, clientB.Id, loc.Id, svc.Id, spec.Id, promo.Id, conv.Id, msg.Id);
    }

    private async Task<Guid> AddAppointmentAsync(Seed s)
    {
        await using var ctx = _db.CreateContext(s.TenantId);
        var appt = new Appointment
        {
            LocationId = s.LocationId, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId, ClientId = s.ClientId,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = 100, PriceFinal = 90,
        };
        ctx.Add(appt);
        await ctx.SaveChangesAsync();
        return appt.Id;
    }

    // ---------- SQL builders (значення - сирий SQL; override замінює/додає колонку) ----------

    private static string Ins(string table, Seed s, Dictionary<string, string> cols, Dictionary<string, string>? over)
    {
        var all = new Dictionary<string, string>(cols) { ["tenant_id"] = $"'{s.TenantId}'" };
        if (over is not null) foreach (var (k, v) in over) all[k] = v;
        return $"INSERT INTO {table} ({string.Join(", ", all.Keys)}) VALUES ({string.Join(", ", all.Values)})";
    }

    private static Dictionary<string, string> O(params (string k, string v)[] kv) => kv.ToDictionary(x => x.k, x => x.v);

    private static string Consent(Seed s, Dictionary<string, string>? o = null) => Ins("beauty_marketing_consents", s, new()
    {
        ["client_id"] = $"'{s.ClientId}'", ["channel"] = "'telegram'", ["level"] = "'full'", ["source"] = "'chat'", ["text_version"] = "'mc-v1'",
    }, o);

    private static string Event(Seed s, Dictionary<string, string>? o = null) => Ins("beauty_marketing_consent_events", s, new()
    {
        ["client_id"] = $"'{s.ClientId}'", ["channel"] = "'telegram'", ["level_from"] = "NULL", ["level_to"] = "'full'", ["source"] = "'chat'",
        ["via"] = "'button'", ["text_version"] = "'mc-v1'",
    }, o);

    private static string Campaign(Seed s, Dictionary<string, string>? o = null) => Ins("beauty_campaigns", s, new()
    {
        ["name"] = "'Весна'", ["type"] = "'promo'", ["promotion_id"] = $"'{s.PromotionId}'", ["segment_kind"] = "'all_consented'",
        ["template"] = "'Знижка {discount}'", ["created_by_user_id"] = $"'{s.UserId}'",
    }, o);

    private static string Recipient(Seed s, Guid campaign, Dictionary<string, string>? o = null) => Ins("beauty_campaign_recipients", s, new()
    {
        ["campaign_id"] = $"'{campaign}'", ["client_id"] = $"'{s.ClientId}'", ["status"] = "'pending'",
    }, o);

    private const string RandomHash = "encode(sha256(gen_random_uuid()::text::bytea), 'hex')";

    private static string Shared(Seed s, Dictionary<string, string>? o = null) => Ins("beauty_coupons", s, new()
    {
        ["kind"] = "'shared'", ["promotion_id"] = $"'{s.PromotionId}'", ["code_hash"] = RandomHash, ["code"] = "'SPRING'", ["code_hint"] = "'ING'",
        ["valid_from"] = "now()", ["valid_to"] = "now() + interval '30 days'",
    }, o);

    private static string Personal(Seed s, Guid campaign, Dictionary<string, string>? o = null) => Ins("beauty_coupons", s, new()
    {
        ["kind"] = "'personal'", ["promotion_id"] = $"'{s.PromotionId}'", ["campaign_id"] = $"'{campaign}'", ["client_id"] = $"'{s.ClientId}'",
        ["code_hash"] = RandomHash, ["code_hint"] = "'K7Q'", ["max_uses"] = "1",
        ["valid_from"] = "now()", ["valid_to"] = "now() + interval '30 days'",
    }, o);

    private static string Redemption(Seed s, Guid coupon, Guid appointment, Dictionary<string, string>? o = null) => Ins("beauty_coupon_redemptions", s, new()
    {
        ["coupon_id"] = $"'{coupon}'", ["appointment_id"] = $"'{appointment}'", ["client_id"] = $"'{s.ClientId}'", ["discount_amount"] = "10",
    }, o);

    // ---------- helpers ----------

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

    private async Task<Guid> RunId(Guid tenant, string insertSql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = insertSql + " RETURNING id";
        return (Guid)(await cmd.ExecuteScalarAsync())!;
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

    private async Task Admin(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private const string Check = PostgresErrorCodes.CheckViolation;
    private const string Unique = PostgresErrorCodes.UniqueViolation;
    private const string Fk = PostgresErrorCodes.ForeignKeyViolation;

    // ---------- RLS / FORCE ----------

    private static readonly string[] NewTables =
    {
        "beauty_marketing_consents", "beauty_marketing_consent_events", "beauty_campaigns",
        "beauty_campaign_recipients", "beauty_coupons", "beauty_coupon_redemptions",
    };

    [SkippableFact]
    public async Task force_rls_and_tenant_isolation_policies_exist_on_new_and_changed_tables()
    {
        SkipIfNoDb();
        foreach (var table in NewTables.Concat(new[]
                 { "beauty_clients", "beauty_promotions", "beauty_appointments", "beauty_conversations", "beauty_chat_keyboards", "beauty_chat_booking_proposals" }))
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
        foreach (var table in NewTables)
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation'
                AND qual IS NOT NULL AND with_check IS NOT NULL"));
    }

    [SkippableFact]
    public async Task all_six_tables_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, Consent(a));
        await Run(a.TenantId, Event(a));
        var camp = await RunId(a.TenantId, Campaign(a));
        await Run(a.TenantId, Recipient(a, camp));
        var coupon = await RunId(a.TenantId, Shared(a));
        await Run(a.TenantId, Redemption(a, coupon, await AddAppointmentAsync(a)));

        foreach (var (table, set) in new[]
                 {
                     ("beauty_marketing_consents", "level = 'none'"), ("beauty_marketing_consent_events", "via = 'api'"), ("beauty_campaigns", "name = 'x'"),
                     ("beauty_campaign_recipients", "status = 'failed'"), ("beauty_coupons", "status = 'revoked'"), ("beauty_coupon_redemptions", "discount_amount = 1"),
                 })
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
        await Rejects(b.TenantId, Consent(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(null, Consent(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(b.TenantId, Event(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(b.TenantId, Campaign(a), PostgresErrorCodes.InsufficientPrivilege);
        await Rejects(b.TenantId, Shared(a), PostgresErrorCodes.InsufficientPrivilege);
        await Run(b.TenantId, Consent(b));
        await Rejects(b.TenantId, $"UPDATE beauty_marketing_consents SET tenant_id = '{a.TenantId}'", PostgresErrorCodes.InsufficientPrivilege);
    }

    // ---------- consents ----------

    [SkippableFact]
    public async Task consent_state_checks_primary_key_and_defaults()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Rejects(s.TenantId, Consent(s, O(("channel", "'sms'"))), Check, "ck_beauty_marketing_consents_channel");
        await Rejects(s.TenantId, Consent(s, O(("level", "'all'"))), Check, "ck_beauty_marketing_consents_level");
        await Rejects(s.TenantId, Consent(s, O(("source", "'web'"))), Check, "ck_beauty_marketing_consents_source");
        // admin <=> updated_by_user_id
        await Rejects(s.TenantId, Consent(s, O(("source", "'admin'"), ("level", "'none'"), ("text_version", "NULL"))), Check, "ck_beauty_marketing_consents_admin");
        await Rejects(s.TenantId, Consent(s, O(("updated_by_user_id", $"'{s.UserId}'"))), Check, "ck_beauty_marketing_consents_admin");
        // chat + level<>none потребує версії тексту
        await Rejects(s.TenantId, Consent(s, O(("text_version", "NULL"))), Check, "ck_beauty_marketing_consents_version");
        await Rejects(s.TenantId, Consent(s, O(("level", "'selective'"), ("text_version", "NULL"))), Check, "ck_beauty_marketing_consents_version");

        await Run(s.TenantId, Consent(s, O(("level", "'none'"), ("text_version", "NULL"))));              // chat/none без версії - відписка
        Assert.Equal(1, await Run(s.TenantId, "DELETE FROM beauty_marketing_consents"));
        await Run(s.TenantId, Consent(s));                                                                // full, chat, mc-v1
        await Run(s.TenantId, Consent(s, O(("channel", "'push'"))));                                      // другий канал того самого клієнта
        await Rejects(s.TenantId, Consent(s), Unique, "pk_beauty_marketing_consents");                    // PK (tenant, client, channel)
        await Run(s.TenantId, Consent(s, O(("client_id", $"'{s.ClientB}'"), ("source", "'admin'"), ("level", "'none'"),
            ("text_version", "NULL"), ("updated_by_user_id", $"'{s.UserId}'"), ("conversation_id", $"'{s.ConversationId}'"))));
        await Run(s.TenantId, Consent(s, O(("client_id", $"'{s.ClientB}'"), ("source", "'stop'"), ("level", "'none'"), ("channel", "'push'"), ("text_version", "NULL"))));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_marketing_consents WHERE channel = 'telegram' AND client_id = '" + s.ClientId + "' AND created_at IS NOT NULL AND updated_at IS NOT NULL"));
    }

    // ---------- events ----------

    [SkippableFact]
    public async Task event_checks_basis_rules_and_level_must_change()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Rejects(s.TenantId, Event(s, O(("channel", "'sms'"))), Check, "ck_beauty_marketing_consent_events_channel");
        await Rejects(s.TenantId, Event(s, O(("level_to", "'all'"))), Check, "ck_beauty_marketing_consent_events_level_to");
        await Rejects(s.TenantId, Event(s, O(("level_from", "'maybe'"))), Check, "ck_beauty_marketing_consent_events_level_from");
        await Rejects(s.TenantId, Event(s, O(("source", "'web'"))), Check, "ck_beauty_marketing_consent_events_source");
        await Rejects(s.TenantId, Event(s, O(("via", "'sms'"))), Check, "ck_beauty_marketing_consent_events_via");
        await Rejects(s.TenantId, Event(s, O(("level_from", "'full'"), ("level_to", "'full'"))), Check, "ck_beauty_marketing_consent_events_changed");
        // basis: лише admin, з нотаткою, із словника
        await Rejects(s.TenantId, Event(s, O(("basis", "'written'"), ("basis_note", "'заява'"))), Check, "ck_beauty_marketing_consent_events_basis_admin");
        await Rejects(s.TenantId, Event(s, O(("source", "'admin'"), ("basis", "'written'"), ("actor_user_id", $"'{s.UserId}'"))), Check, "ck_beauty_marketing_consent_events_basis_note");
        await Rejects(s.TenantId, Event(s, O(("source", "'admin'"), ("basis", "'phone'"), ("basis_note", "'x'"), ("actor_user_id", $"'{s.UserId}'"))), Check, "ck_beauty_marketing_consent_events_basis");
        // basis_note обмежена 200 символами (varchar)
        await Rejects(s.TenantId, Event(s, O(("source", "'admin'"), ("basis", "'other'"), ("basis_note", $"'{new string('x', 201)}'"), ("actor_user_id", $"'{s.UserId}'"))),
            PostgresErrorCodes.StringDataRightTruncation);

        await Run(s.TenantId, Event(s));                                                                  // NULL -> full (рядка не було)
        await Run(s.TenantId, Event(s, O(("level_from", "'full'"), ("level_to", "'none'"), ("source", "'stop'"), ("via", "'command'"))));
        await Run(s.TenantId, Event(s, O(("level_from", "NULL"), ("level_to", "'none'"), ("via", "NULL"), ("text_version", "NULL")))); // NULL -> none (історія «Стоп»)
        await Run(s.TenantId, Event(s, O(("level_from", "'none'"), ("level_to", "'selective'"), ("source", "'admin'"), ("via", "'api'"),
            ("basis", "'in_person'"), ("basis_note", "'особисто'"), ("actor_user_id", $"'{s.UserId}'"))));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_marketing_consent_events WHERE id IS NOT NULL AND created_at IS NOT NULL AND basis = 'in_person'"));
    }

    [SkippableFact]
    public async Task consent_events_are_append_only_for_a_role_granted_select_and_insert()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var role = $"beauty_evt_t_{Guid.NewGuid():N}"[..28];
        await Admin($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await Admin($"GRANT USAGE ON SCHEMA public TO {role}; GRANT SELECT, INSERT ON beauty_marketing_consent_events TO {role}");
            await using var conn = new NpgsqlConnection(_db.AdminConnectionString);
            await conn.OpenAsync();
            async Task Exec(string sql)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await Exec($"SET ROLE {role}");
            await Exec($"SELECT set_config('app.tenant_id', '{s.TenantId}', false)");
            await Exec(Event(s));
            var upd = await Assert.ThrowsAsync<PostgresException>(() => Exec("UPDATE beauty_marketing_consent_events SET via = 'api'"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, upd.SqlState);
            var del = await Assert.ThrowsAsync<PostgresException>(() => Exec("DELETE FROM beauty_marketing_consent_events"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, del.SqlState);
            await Exec("RESET ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP OWNED BY {role}; DROP ROLE IF EXISTS {role}");
        }
    }

    // ---------- campaigns ----------

    [SkippableFact]
    public async Task campaign_type_status_and_segment_parameter_checks()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Rejects(s.TenantId, Campaign(s, O(("type", "'ad'"))), Check, "ck_beauty_campaigns_type");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'vip'"))), Check, "ck_beauty_campaigns_segment_kind");
        await Rejects(s.TenantId, Campaign(s, O(("coupon_mode", "'many'"))), Check, "ck_beauty_campaigns_coupon_mode");
        await Rejects(s.TenantId, Campaign(s, O(("status", "'paused'"))), Check, "ck_beauty_campaigns_status");
        await Rejects(s.TenantId, Campaign(s, O(("revision", "0"))), Check, "ck_beauty_campaigns_revision");
        await Rejects(s.TenantId, Campaign(s, O(("name", $"'{new string('n', 201)}'"))), PostgresErrorCodes.StringDataRightTruncation);
        await Rejects(s.TenantId, Campaign(s, O(("template", $"'{new string('t', 1001)}'"))), PostgresErrorCodes.StringDataRightTruncation);
        await Rejects(s.TenantId, Campaign(s, O(("template", "NULL"))), PostgresErrorCodes.NotNullViolation);

        // news без акції і без купона; promo з акцією
        await Rejects(s.TenantId, Campaign(s, O(("type", "'news'"))), Check, "ck_beauty_campaigns_type_promotion");                                  // news з акцією
        await Rejects(s.TenantId, Campaign(s, O(("promotion_id", "NULL"))), Check, "ck_beauty_campaigns_type_promotion");                            // promo без акції
        await Run(s.TenantId, Campaign(s, O(("type", "'news'"), ("promotion_id", "NULL"))));

        // параметри сегмента
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"))), Check, "ck_beauty_campaigns_segment_params");                        // lapsed без днів
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"), ("segment_days", "60"), ("segment_min_visits", "3"))), Check, "ck_beauty_campaigns_segment_params");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"), ("segment_days", "0"))), Check, "ck_beauty_campaigns_segment_ranges");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"), ("segment_days", "731"))), Check, "ck_beauty_campaigns_segment_ranges");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'frequent'"), ("segment_min_visits", "3"))), Check, "ck_beauty_campaigns_segment_params"); // без period
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'frequent'"), ("segment_min_visits", "3"), ("segment_period_days", "90"), ("segment_days", "30"))), Check, "ck_beauty_campaigns_segment_params");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'frequent'"), ("segment_min_visits", "3"), ("segment_period_days", "90"), ("segment_service_id", $"'{s.ServiceId}'"))), Check, "ck_beauty_campaigns_segment_params");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'frequent'"), ("segment_min_visits", "101"), ("segment_period_days", "90"))), Check, "ck_beauty_campaigns_segment_ranges");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'new'"), ("segment_days", "5"))), Check, "ck_beauty_campaigns_segment_params");
        await Rejects(s.TenantId, Campaign(s, O(("segment_kind", "'all_consented'"), ("segment_service_id", $"'{s.ServiceId}'"))), Check, "ck_beauty_campaigns_segment_params");

        await Run(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"), ("segment_days", "60"))));
        await Run(s.TenantId, Campaign(s, O(("segment_kind", "'lapsed'"), ("segment_days", "730"), ("segment_service_id", $"'{s.ServiceId}'"), ("location_id", $"'{s.LocationId}'"))));
        await Run(s.TenantId, Campaign(s, O(("segment_kind", "'frequent'"), ("segment_min_visits", "3"), ("segment_period_days", "90"))));
        await Run(s.TenantId, Campaign(s, O(("segment_kind", "'new'"))));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_campaigns WHERE name = 'Весна' AND status = 'draft' AND revision = 1 AND coupon_mode = 'none' AND segment_kind = 'new' AND id IS NOT NULL"));
    }

    [SkippableFact]
    public async Task campaign_coupon_mode_and_lifecycle_checks()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var coupon = await RunId(s.TenantId, Shared(s));

        // shared <=> shared_coupon_id
        await Rejects(s.TenantId, Campaign(s, O(("coupon_mode", "'shared'"))), Check, "ck_beauty_campaigns_shared_coupon");
        await Rejects(s.TenantId, Campaign(s, O(("shared_coupon_id", $"'{coupon}'"))), Check, "ck_beauty_campaigns_shared_coupon");
        await Run(s.TenantId, Campaign(s, O(("coupon_mode", "'shared'"), ("shared_coupon_id", $"'{coupon}'"))));
        // personal потребує coupon_valid_to; period
        await Rejects(s.TenantId, Campaign(s, O(("coupon_mode", "'personal'"))), Check, "ck_beauty_campaigns_personal_valid_to");
        await Rejects(s.TenantId, Campaign(s, O(("coupon_mode", "'personal'"), ("coupon_valid_from", "now()"), ("coupon_valid_to", "now()"))), Check, "ck_beauty_campaigns_coupon_period");
        await Run(s.TenantId, Campaign(s, O(("coupon_mode", "'personal'"), ("coupon_valid_to", "now() + interval '10 days'"))));
        await Run(s.TenantId, Campaign(s, O(("coupon_mode", "'personal'"), ("coupon_valid_from", "now()"), ("coupon_valid_to", "now() + interval '10 days'"))));
        // news з купоном заборонено
        await Rejects(s.TenantId, Campaign(s, O(("type", "'news'"), ("promotion_id", "NULL"), ("coupon_mode", "'personal'"), ("coupon_valid_to", "now() + interval '1 day'"))), Check, "ck_beauty_campaigns_type_promotion");

        // підтверджені статуси вимагають confirmed_* і scheduled_at
        foreach (var st in new[] { "scheduled", "sending", "done" })
        {
            await Rejects(s.TenantId, Campaign(s, O(("status", $"'{st}'"))), Check, "ck_beauty_campaigns_confirmed");
            await Rejects(s.TenantId, Campaign(s, O(("status", $"'{st}'"), ("confirmed_by_user_id", $"'{s.UserId}'"), ("confirmed_at", "now()"))), Check, "ck_beauty_campaigns_confirmed");
            await Run(s.TenantId, Campaign(s, O(("status", $"'{st}'"), ("confirmed_by_user_id", $"'{s.UserId}'"), ("confirmed_at", "now()"), ("scheduled_at", "now()"),
                ("confirm_key_hash", RandomHash), ("recipients_total", "3"))));
        }
        // cancelled <=> cancelled_at
        await Rejects(s.TenantId, Campaign(s, O(("status", "'cancelled'"))), Check, "ck_beauty_campaigns_cancelled");
        await Rejects(s.TenantId, Campaign(s, O(("cancelled_at", "now()"))), Check, "ck_beauty_campaigns_cancelled");
        await Run(s.TenantId, Campaign(s, O(("status", "'cancelled'"), ("cancelled_at", "now()"), ("cancelled_by_user_id", $"'{s.UserId}'"))));
        await Rejects(s.TenantId, Campaign(s, O(("recipients_total", "-1"))), Check, "ck_beauty_campaigns_recipients_total");
    }

    // ---------- recipients ----------

    [SkippableFact]
    public async Task recipient_unique_per_campaign_client_and_status_rules()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var other = await SeedTenantAsync();
        var camp = await RunId(s.TenantId, Campaign(s));
        var otherCamp = await RunId(other.TenantId, Campaign(other));

        await Run(s.TenantId, Recipient(s, camp));
        await Rejects(s.TenantId, Recipient(s, camp), Unique, "ux_beauty_campaign_recipients_campaign_client"); // ключ ідемпотентності
        await Run(s.TenantId, Recipient(s, camp, O(("client_id", $"'{s.ClientB}'"))));
        await Run(other.TenantId, Recipient(other, otherCamp));                                                 // той самий клієнт-id в іншому tenant не конфліктує

        await Rejects(s.TenantId, Recipient(s, await RunId(s.TenantId, Campaign(s)), O(("status", "'done'"))), Check, "ck_beauty_campaign_recipients_status");
        var c2 = await RunId(s.TenantId, Campaign(s));
        foreach (var st in new[] { "queued", "sent", "failed" })
            await Rejects(s.TenantId, Recipient(s, c2, O(("status", $"'{st}'"))), Check, "ck_beauty_campaign_recipients_message");
        await Rejects(s.TenantId, Recipient(s, c2, O(("status", "'skipped'"))), Check, "ck_beauty_campaign_recipients_skip_reason");

        await Run(s.TenantId, Recipient(s, c2, O(("status", "'skipped'"), ("skip_reason", "'unsubscribed'"))));
        await Run(s.TenantId, Recipient(s, c2, O(("client_id", $"'{s.ClientB}'"), ("status", "'queued'"), ("message_id", $"'{s.MessageId}'"),
            ("conversation_id", $"'{s.ConversationId}'"), ("queued_at", "now()"))));
        Assert.Equal(1L, await Count(s.TenantId, $"SELECT 1 FROM beauty_campaign_recipients WHERE campaign_id = '{c2}' AND status = 'queued' AND created_at IS NOT NULL AND updated_at IS NOT NULL"));
    }

    // ---------- coupons ----------

    [SkippableFact]
    public async Task coupon_kind_hash_period_and_usage_checks()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var camp = await RunId(s.TenantId, Campaign(s));

        await Rejects(s.TenantId, Shared(s, O(("kind", "'group'"))), Check, "ck_beauty_coupons_kind");
        await Rejects(s.TenantId, Shared(s, O(("status", "'used'"))), Check, "ck_beauty_coupons_status");
        await Rejects(s.TenantId, Shared(s, O(("code_hash", "'abc'"))), Check, "ck_beauty_coupons_code_hash");
        await Rejects(s.TenantId, Shared(s, O(("code_hash", $"'{new string('A', 64)}'"))), Check, "ck_beauty_coupons_code_hash"); // верхній регістр заборонено
        await Rejects(s.TenantId, Shared(s, O(("valid_to", "now() - interval '1 day'"))), Check, "ck_beauty_coupons_period");
        await Rejects(s.TenantId, Shared(s, O(("code", $"'{new string('C', 21)}'"))), PostgresErrorCodes.StringDataRightTruncation);
        await Rejects(s.TenantId, Shared(s, O(("code_hint", "'ABCDE'"))), PostgresErrorCodes.StringDataRightTruncation);

        // shared: без клієнта, з відкритим кодом
        await Rejects(s.TenantId, Shared(s, O(("client_id", $"'{s.ClientId}'"))), Check, "ck_beauty_coupons_kind_fields");
        await Rejects(s.TenantId, Shared(s, O(("code", "NULL"))), Check, "ck_beauty_coupons_kind_fields");
        // personal: клієнт і кампанія обов'язкові, без відкритого коду, max_uses = 1
        await Rejects(s.TenantId, Personal(s, camp, O(("client_id", "NULL"))), Check, "ck_beauty_coupons_kind_fields");
        await Rejects(s.TenantId, Personal(s, camp, O(("campaign_id", "NULL"))), Check, "ck_beauty_coupons_kind_fields");
        await Rejects(s.TenantId, Personal(s, camp, O(("code", "'SECRET'"))), Check, "ck_beauty_coupons_kind_fields");
        await Rejects(s.TenantId, Personal(s, camp, O(("max_uses", "2"))), Check, "ck_beauty_coupons_kind_fields");
        await Rejects(s.TenantId, Personal(s, camp, O(("max_uses", "NULL"))), Check, "ck_beauty_coupons_kind_fields");
        // лічильник
        await Rejects(s.TenantId, Shared(s, O(("uses_count", "-1"))), Check, "ck_beauty_coupons_uses");
        await Rejects(s.TenantId, Shared(s, O(("max_uses", "5"), ("uses_count", "6"))), Check, "ck_beauty_coupons_uses");
        await Rejects(s.TenantId, Shared(s, O(("max_uses", "0"))), Check, "ck_beauty_coupons_max_uses");
        await Rejects(s.TenantId, Shared(s, O(("max_uses", "100001"))), Check, "ck_beauty_coupons_max_uses");

        await Run(s.TenantId, Shared(s));                                                           // безлімітний shared
        await Run(s.TenantId, Shared(s, O(("max_uses", "100000"), ("uses_count", "100000"))));
        await Run(s.TenantId, Personal(s, camp));
        await Run(s.TenantId, Personal(s, camp, O(("client_id", $"'{s.ClientB}'"), ("uses_count", "1"), ("status", "'revoked'"))));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_coupons WHERE kind = 'shared' AND max_uses IS NULL AND uses_count = 0 AND status = 'active' AND id IS NOT NULL AND created_by_user_id IS NULL"));
        await Rejects(s.TenantId, "UPDATE beauty_coupons SET uses_count = uses_count + 1 WHERE kind = 'personal' AND uses_count = 1", Check, "ck_beauty_coupons_uses"); // умовне погашення має це перевіряти
    }

    [SkippableFact]
    public async Task coupon_code_hash_is_unique_per_tenant_but_may_repeat_across_tenants()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        const string hash = "'0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'";
        await Run(a.TenantId, Shared(a, O(("code_hash", hash))));
        await Rejects(a.TenantId, Shared(a, O(("code_hash", hash), ("code", "'OTHER'"))), Unique, "ux_beauty_coupons_code_hash");
        var camp = await RunId(a.TenantId, Campaign(a));
        await Rejects(a.TenantId, Personal(a, camp, O(("code_hash", hash))), Unique, "ux_beauty_coupons_code_hash"); // і між видами
        await Run(b.TenantId, Shared(b, O(("code_hash", hash))));                                                  // інший tenant - дозволено
    }

    // ---------- redemptions ----------

    [SkippableFact]
    public async Task redemption_unique_per_appointment_and_partial_unique_per_active_coupon_client()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var coupon = await RunId(s.TenantId, Shared(s));
        var ap1 = await AddAppointmentAsync(s);
        var ap2 = await AddAppointmentAsync(s);
        var ap3 = await AddAppointmentAsync(s);

        await Rejects(s.TenantId, Redemption(s, coupon, ap1, O(("discount_amount", "-0.01"))), Check, "ck_beauty_coupon_redemptions_amount");
        await Rejects(s.TenantId, Redemption(s, coupon, ap1, O(("status", "'used'"))), Check, "ck_beauty_coupon_redemptions_status");
        await Rejects(s.TenantId, Redemption(s, coupon, ap1, O(("release_reason", "'refund'"), ("released_at", "now()"), ("status", "'released'"))), Check, "ck_beauty_coupon_redemptions_reason");

        var first = await RunId(s.TenantId, Redemption(s, coupon, ap1));
        await Rejects(s.TenantId, Redemption(s, coupon, ap1, O(("client_id", $"'{s.ClientB}'"))), Unique, "ux_beauty_coupon_redemptions_appointment"); // один запис - одне погашення
        await Rejects(s.TenantId, Redemption(s, coupon, ap2), Unique, "ux_beauty_coupon_redemptions_active");        // клієнт уже має активне погашення цього купона
        // після повернення купон можна погасити знову тим самим клієнтом
        Assert.Equal(1, await Run(s.TenantId, $"UPDATE beauty_coupon_redemptions SET status = 'released', release_reason = 'cancelled', released_at = now() WHERE id = '{first}'"));
        await Run(s.TenantId, Redemption(s, coupon, ap2));
        await Rejects(s.TenantId, Redemption(s, coupon, ap3), Unique, "ux_beauty_coupon_redemptions_active");
        // інший клієнт - окремо
        await Run(s.TenantId, Redemption(s, coupon, ap3, O(("client_id", $"'{s.ClientB}'"))));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_coupon_redemptions WHERE status = 'released' AND release_reason = 'cancelled' AND released_at IS NOT NULL AND created_at IS NOT NULL"));
    }

    [SkippableFact]
    public async Task redemption_release_fields_are_consistent_both_ways()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var coupon = await RunId(s.TenantId, Shared(s));
        var ap = await AddAppointmentAsync(s);
        var id = await RunId(s.TenantId, Redemption(s, coupon, ap));

        await Rejects(s.TenantId, $"UPDATE beauty_coupon_redemptions SET status = 'released' WHERE id = '{id}'", Check, "ck_beauty_coupon_redemptions_released");
        await Rejects(s.TenantId, $"UPDATE beauty_coupon_redemptions SET status = 'released', released_at = now() WHERE id = '{id}'", Check, "ck_beauty_coupon_redemptions_released");
        await Rejects(s.TenantId, $"UPDATE beauty_coupon_redemptions SET release_reason = 'cancelled', released_at = now() WHERE id = '{id}'", Check, "ck_beauty_coupon_redemptions_released"); // active з release-полями
        // половинний стан (дірка тривальної логіки в дослівному CHECK §26.3): released_at без reason у active
        await Rejects(s.TenantId, $"UPDATE beauty_coupon_redemptions SET released_at = now() WHERE id = '{id}'", Check, "ck_beauty_coupon_redemptions_released_pair");
        await Rejects(s.TenantId, $"UPDATE beauty_coupon_redemptions SET release_reason = 'payment_failed' WHERE id = '{id}'", Check, "ck_beauty_coupon_redemptions_released_pair");
        Assert.Equal(1, await Run(s.TenantId, $"UPDATE beauty_coupon_redemptions SET status = 'released', release_reason = 'payment_failed', released_at = now() WHERE id = '{id}'"));
    }

    // ---------- FK (композитні, tenant-safe) ----------

    [SkippableFact]
    public async Task composite_fks_reject_foreign_tenant_references_everywhere()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var bCamp = await RunId(b.TenantId, Campaign(b));
        var bCoupon = await RunId(b.TenantId, Shared(b));
        var aCamp = await RunId(a.TenantId, Campaign(a));
        var aCoupon = await RunId(a.TenantId, Shared(a));
        var aAppt = await AddAppointmentAsync(a);

        await Rejects(a.TenantId, Consent(a, O(("client_id", $"'{b.ClientId}'"))), Fk);
        await Rejects(a.TenantId, Consent(a, O(("source", "'admin'"), ("level", "'none'"), ("text_version", "NULL"), ("updated_by_user_id", $"'{b.UserId}'"))), Fk);
        await Rejects(a.TenantId, Consent(a, O(("conversation_id", $"'{b.ConversationId}'"))), Fk);
        await Rejects(a.TenantId, Event(a, O(("client_id", $"'{b.ClientId}'"))), Fk);
        await Rejects(a.TenantId, Event(a, O(("campaign_id", $"'{bCamp}'"))), Fk);
        await Rejects(a.TenantId, Event(a, O(("actor_user_id", $"'{b.UserId}'"), ("source", "'admin'"))), Fk);
        await Rejects(a.TenantId, Campaign(a, O(("promotion_id", $"'{b.PromotionId}'"))), Fk);
        await Rejects(a.TenantId, Campaign(a, O(("segment_kind", "'lapsed'"), ("segment_days", "5"), ("segment_service_id", $"'{b.ServiceId}'"))), Fk);
        await Rejects(a.TenantId, Campaign(a, O(("location_id", $"'{b.LocationId}'"))), Fk);
        await Rejects(a.TenantId, Campaign(a, O(("created_by_user_id", $"'{b.UserId}'"))), Fk);
        await Rejects(a.TenantId, Campaign(a, O(("coupon_mode", "'shared'"), ("shared_coupon_id", $"'{bCoupon}'"))), Fk);
        await Rejects(a.TenantId, Recipient(a, bCamp), Fk);
        await Rejects(a.TenantId, Recipient(a, aCamp, O(("client_id", $"'{b.ClientId}'"))), Fk);
        await Rejects(a.TenantId, Recipient(a, aCamp, O(("status", "'queued'"), ("message_id", $"'{b.MessageId}'"))), Fk);
        await Rejects(a.TenantId, Recipient(a, aCamp, O(("coupon_id", $"'{bCoupon}'"))), Fk);
        await Rejects(a.TenantId, Shared(a, O(("promotion_id", $"'{b.PromotionId}'"))), Fk);
        await Rejects(a.TenantId, Personal(a, bCamp), Fk);
        await Rejects(a.TenantId, Personal(a, aCamp, O(("client_id", $"'{b.ClientId}'"))), Fk);
        await Rejects(a.TenantId, Redemption(a, bCoupon, aAppt), Fk);
        await Rejects(a.TenantId, Redemption(a, aCoupon, aAppt, O(("client_id", $"'{b.ClientId}'"))), Fk);
        await Rejects(a.TenantId, Redemption(a, aCoupon, Guid.NewGuid()), Fk);
        // нові колонки наявних таблиць
        await Rejects(a.TenantId, $"UPDATE beauty_appointments SET coupon_id = '{bCoupon}' WHERE id = '{aAppt}'", Fk);
        await Rejects(a.TenantId, $"UPDATE beauty_appointments SET campaign_id = '{bCamp}' WHERE id = '{aAppt}'", Fk);
        await Rejects(a.TenantId, $"UPDATE beauty_conversations SET offer_campaign_id = '{bCamp}', offer_until = now() WHERE id = '{a.ConversationId}'", Fk);
        await Rejects(a.TenantId, $"UPDATE beauty_conversations SET offer_coupon_id = '{bCoupon}', offer_until = now() WHERE id = '{a.ConversationId}'", Fk);
        // той самий tenant - працює (включно з циклічним посиланням кампанія <-> купон)
        Assert.Equal(1, await Run(a.TenantId, $"UPDATE beauty_appointments SET coupon_id = '{aCoupon}', campaign_id = '{aCamp}' WHERE id = '{aAppt}'"));
        await Run(a.TenantId, Campaign(a, O(("coupon_mode", "'shared'"), ("shared_coupon_id", $"'{aCoupon}'"))));
        await Run(a.TenantId, Personal(a, aCamp));
    }

    [SkippableFact]
    public async Task referenced_rows_cannot_be_deleted_restrict()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var camp = await RunId(s.TenantId, Campaign(s));
        var coupon = await RunId(s.TenantId, Personal(s, camp));
        var ap = await AddAppointmentAsync(s);
        await Run(s.TenantId, Consent(s));
        await Run(s.TenantId, Event(s, O(("campaign_id", $"'{camp}'"))));
        await Run(s.TenantId, Recipient(s, camp));
        await Run(s.TenantId, Redemption(s, coupon, ap));

        foreach (var sql in new[]
                 {
                     $"DELETE FROM beauty_clients WHERE id = '{s.ClientId}'",               // consents / events / recipients / coupons / redemptions
                     $"DELETE FROM beauty_campaigns WHERE id = '{camp}'",                   // recipients / coupons / events
                     $"DELETE FROM beauty_coupons WHERE id = '{coupon}'",                   // redemptions
                     $"DELETE FROM beauty_appointments WHERE id = '{ap}'",                  // redemptions
                     $"DELETE FROM beauty_promotions WHERE id = '{s.PromotionId}'",         // campaigns / coupons
                     $"DELETE FROM users WHERE id = '{s.UserId}'",
                 })
        {
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Admin(sql));
            Assert.Contains(ex.SqlState, new[] { Fk, PostgresErrorCodes.RestrictViolation });
        }
    }

    // ---------- наявні таблиці ----------

    [SkippableFact]
    public async Task promotion_access_defaults_to_public_and_accepts_only_known_values()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await Run(s.TenantId, "INSERT INTO beauty_promotions (tenant_id, name, discount_type, discount_value, is_active) VALUES ('" + s.TenantId + "', 'P', 'percent', 5, true)");
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_promotions WHERE name = 'P' AND access = 'public'"));
        Assert.Equal(1L, await Count(s.TenantId, "SELECT 1 FROM beauty_promotions WHERE access = 'code_only'"));
        await Rejects(s.TenantId, "UPDATE beauty_promotions SET access = 'private'", Check, "ck_beauty_promotions_access");
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_promotions' AND column_name = 'access' AND is_nullable = 'NO' AND character_maximum_length = 16"));
    }

    [SkippableFact]
    public async Task conversation_offer_columns_follow_offer_until()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var camp = await RunId(s.TenantId, Campaign(s));
        var coupon = await RunId(s.TenantId, Shared(s));
        string Set(string set) => $"UPDATE beauty_conversations SET {set} WHERE id = '{s.ConversationId}'";

        await Rejects(s.TenantId, Set($"offer_campaign_id = '{camp}'"), Check, "ck_beauty_conversations_offer_until_null");   // пропозиція без строку
        await Rejects(s.TenantId, Set($"offer_coupon_id = '{coupon}'"), Check, "ck_beauty_conversations_offer_until_null");
        await Rejects(s.TenantId, Set("offer_until = now() + interval '1 hour'"), Check, "ck_beauty_conversations_offer_until_set"); // строк без пропозиції
        Assert.Equal(1, await Run(s.TenantId, Set($"offer_campaign_id = '{camp}', offer_coupon_id = '{coupon}', offer_until = now() + interval '72 hours'")));
        Assert.Equal(1, await Run(s.TenantId, Set($"offer_campaign_id = NULL, offer_coupon_id = '{coupon}'")));                 // лише купон (код текстом)
        Assert.Equal(1, await Run(s.TenantId, Set("offer_campaign_id = NULL, offer_coupon_id = NULL, offer_until = NULL")));
    }

    [SkippableFact]
    public async Task chat_keyboard_accepts_new_kinds_and_campaign_offer_requires_a_recipient()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        var camp = await RunId(s.TenantId, Campaign(s));
        var recipient = await RunId(s.TenantId, Recipient(s, camp, O(("status", "'queued'"), ("message_id", $"'{s.MessageId}'"))));
        var otherTenant = await SeedTenantAsync();
        var foreignRecipient = await RunId(otherTenant.TenantId, Recipient(otherTenant, await RunId(otherTenant.TenantId, Campaign(otherTenant))));

        await using var ctx = _db.CreateContext(s.TenantId);
        var m2 = new Message { ConversationId = s.ConversationId, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "m2" };
        var m3 = new Message { ConversationId = s.ConversationId, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "m3" };
        ctx.AddRange(m2, m3);
        await ctx.SaveChangesAsync();

        string Kb(string kind, Guid message, string? recipientId, string token) =>
            $@"INSERT INTO beauty_chat_keyboards (tenant_id, conversation_id, message_id, kind, token, options, expires_at, campaign_recipient_id)
               VALUES ('{s.TenantId}', '{s.ConversationId}', '{message}', '{kind}', '{token}', '[]'::jsonb, now() + interval '1 day', {(recipientId is null ? "NULL" : $"'{recipientId}'")})";

        await Rejects(s.TenantId, Kb("campaign_offer", s.MessageId, null, new string('a', 22)), Check, "ck_beauty_chat_keyboards_campaign_recipient");
        await Rejects(s.TenantId, Kb("promo_blast", s.MessageId, null, new string('b', 22)), Check, "ck_beauty_chat_keyboards_kind");
        await Rejects(s.TenantId, Kb("campaign_offer", s.MessageId, foreignRecipient.ToString(), new string('c', 22)), Fk);
        await Run(s.TenantId, Kb("campaign_offer", s.MessageId, recipient.ToString(), new string('d', 22)));
        await Run(s.TenantId, Kb("marketing_consent", m2.Id, null, new string('e', 22)));
        // старі види працюють без змін
        await Run(s.TenantId, Kb("slot_pick", m3.Id, null, new string('f', 22)));
        await Rejects(s.TenantId, $"DELETE FROM beauty_campaign_recipients WHERE id = '{recipient}'", Fk); // RESTRICT: на одержувача посилається клавіатура
    }

    [SkippableFact]
    public async Task new_columns_on_existing_tables_have_expected_shape_and_every_fk_is_indexed()
    {
        SkipIfNoDb();
        foreach (var (table, column) in new[]
                 {
                     ("beauty_appointments", "coupon_id"), ("beauty_appointments", "campaign_id"), ("beauty_chat_booking_proposals", "coupon_id"),
                     ("beauty_chat_booking_proposals", "campaign_id"), ("beauty_conversations", "offer_campaign_id"), ("beauty_conversations", "offer_coupon_id"),
                     ("beauty_chat_keyboards", "campaign_recipient_id"),
                 })
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = '{column}' AND data_type = 'uuid' AND is_nullable = 'YES'"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_conversations' AND column_name = 'offer_until' AND data_type = 'timestamp with time zone' AND is_nullable = 'YES'"));
        Assert.Equal(1L, await Scalar("SELECT count(*) FROM information_schema.columns WHERE table_name = 'beauty_clients' AND column_name = 'marketing_offered_at' AND data_type = 'timestamp with time zone' AND is_nullable = 'YES'"));

        // кожен FK нових таблиць і нових колонок має індекс, перша колонка якого збігається з першою колонкою FK, а друга - з другою (або PK/UNIQUE)
        var unindexed = new List<string>();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT c.conname FROM pg_constraint c
            WHERE c.contype = 'f' AND (c.conrelid IN (
                SELECT oid FROM pg_class WHERE relname IN ('beauty_marketing_consents', 'beauty_marketing_consent_events', 'beauty_campaigns', 'beauty_campaign_recipients',
                    'beauty_coupons', 'beauty_coupon_redemptions'))
               OR c.conname IN ('fk_beauty_appointments_coupons_tenant_id_coupon_id', 'fk_beauty_appointments_campaigns_tenant_id_campaign_id',
                    'fk_beauty_chat_booking_proposals_coupons_tenant_id_coupon_id', 'fk_beauty_chat_booking_proposals_campaigns_tenant_id_campaign_',
                    'fk_beauty_conversations_campaigns_tenant_id_offer_campaign_id', 'fk_beauty_conversations_coupons_tenant_id_offer_coupon_id',
                    'fk_beauty_chat_keyboards_campaign_recipients_tenant_id_campaig'))
              AND NOT EXISTS (
                SELECT 1 FROM pg_index i WHERE i.indrelid = c.conrelid
                  AND (i.indkey::int2[])[0:cardinality(c.conkey) - 1] = c.conkey)", admin);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) unindexed.Add(r.GetString(0));
        Assert.Empty(unindexed);
    }
}
