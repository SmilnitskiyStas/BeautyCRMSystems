using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-767 (§25.3, ADR-014 ч. B): beauty_clients (phone_source, call_consent_at, phone_pending*, phone_requested_at, duplicate_of_client_id),
/// beauty_conversations (pending_prompt=phone_request, reply_keyboard_open_at), beauty_messages (kind=contact, contact_phone, contact_verdict).
/// RLS/FORCE, композитні FK, CHECK, дефолти, індекс sweeper.
/// </summary>
public sealed class PhoneCaptureRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public PhoneCaptureRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid ClientId, Guid Client2Id, Guid ConversationId, Guid AppointmentId, Guid MessageId);

    private int _day = 1;

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'ph-{tenantId:N}');", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var ctx = _db.CreateContext(tenantId);
        var loc = new Location { Name = "L", Timezone = "Europe/Kyiv" };
        var spec = new Specialist { FullName = "S" };
        var svc = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "Клієнт", Phone = "+380501112233" };
        var client2 = new Client { FullName = "Клієнт 2", Phone = "+380671112233" };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, spec, svc, client, client2, ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ClientId = client.Id, ExternalChatId = "100" };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        var msg = new Message { ConversationId = conv.Id, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Body = "x" };
        var appt = new Appointment
        {
            LocationId = loc.Id, SpecialistId = spec.Id, ServiceId = svc.Id, ClientId = client.Id,
            StartsAt = DateTimeOffset.UtcNow.AddDays(Interlocked.Increment(ref _day) + 10), DurationMinutes = 60,
            Source = AppointmentSource.Admin, PriceOriginal = 100m, PriceFinal = 100m,
        };
        ctx.AddRange(msg, appt);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, client.Id, client2.Id, conv.Id, appt.Id, msg.Id);
    }

    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    private async Task Rejects(Guid? tenant, string sql, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(tenant, sql));
        Assert.Equal(state, ex.SqlState);
        if (constraint is not null) Assert.Equal(constraint, ex.ConstraintName);
    }

    private Task RejectsCheck(Seed s, string sql, string constraint) => Rejects(s.TenantId, sql, PostgresErrorCodes.CheckViolation, constraint);

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

    private async Task<object?> Scalar(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private static string InsertMsg(Seed s, string kind, string? phone = null, string? verdict = null)
    {
        var cols = "tenant_id, conversation_id, direction, sender_type, body, kind";
        var vals = $"'{s.TenantId}', '{s.ConversationId}', 'inbound', 'client', '[контакт]', '{kind}'";
        if (phone is not null) { cols += ", contact_phone"; vals += $", '{phone}'"; }
        if (verdict is not null) { cols += ", contact_verdict"; vals += $", '{verdict}'"; }
        return $"INSERT INTO beauty_messages ({cols}) VALUES ({vals})";
    }

    // ---------- FORCE / RLS / дефолти ----------

    [SkippableFact]
    public async Task force_rls_is_enabled_on_changed_tables()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_clients", "beauty_conversations", "beauty_messages" })
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity", admin);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }
    }

    [SkippableFact]
    public async Task new_columns_default_to_null_for_existing_style_rows()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        Assert.Equal(2L, await Count(a.TenantId, @"SELECT 1 FROM beauty_clients WHERE phone_source IS NULL AND call_consent_at IS NULL
            AND phone_pending IS NULL AND phone_pending_source IS NULL AND phone_pending_at IS NULL AND phone_requested_at IS NULL AND duplicate_of_client_id IS NULL"));
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_conversations WHERE reply_keyboard_open_at IS NULL AND pending_prompt IS NULL"));
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_messages WHERE contact_phone IS NULL AND contact_verdict IS NULL AND kind = 'text'"));
    }

    [SkippableFact]
    public async Task new_client_columns_are_tenant_isolated()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, $@"UPDATE beauty_clients SET phone_source = 'manual_chat', call_consent_at = now(), phone_pending = '+380931112233',
            phone_pending_source = 'telegram_contact', phone_pending_at = now(), phone_requested_at = now() WHERE id = '{a.ClientId}'");

        // чужий tenant не бачить і не змінює; без контексту - fail-closed
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_clients SET phone_pending = NULL, phone_pending_source = NULL, phone_pending_at = NULL WHERE id = '{a.ClientId}'"));
        Assert.Equal(0L, await Count(b.TenantId, "SELECT 1 FROM beauty_clients WHERE phone_pending = '+380931112233'"));
        Assert.Equal(0L, await Count(null, "SELECT 1 FROM beauty_clients WHERE phone_pending IS NOT NULL"));
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_clients WHERE phone_pending = '+380931112233' AND phone_source = 'manual_chat'"));

        // контакт і питання розмови теж ізольовані
        await Run(a.TenantId, InsertMsg(a, "contact", "+380501112233", "own"));
        await Run(a.TenantId, $"UPDATE beauty_conversations SET pending_prompt = 'phone_request', pending_prompt_expires_at = now() + interval '1 hour', reply_keyboard_open_at = now() WHERE id = '{a.ConversationId}'");
        Assert.Equal(0L, await Count(b.TenantId, "SELECT 1 FROM beauty_messages WHERE contact_phone IS NOT NULL"));
        Assert.Equal(0L, await Count(null, "SELECT 1 FROM beauty_messages WHERE kind = 'contact'"));
        Assert.Equal(0L, await Count(b.TenantId, "SELECT 1 FROM beauty_conversations WHERE pending_prompt = 'phone_request'"));
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_conversations WHERE pending_prompt = 'phone_request' AND reply_keyboard_open_at IS NOT NULL"));
        Assert.Equal(0, await Run(b.TenantId, "UPDATE beauty_messages SET contact_phone = NULL WHERE kind = 'contact'"));
    }

    // ---------- beauty_clients: CHECK ----------

    [SkippableFact]
    public async Task phone_source_is_restricted_and_requires_a_phone()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        foreach (var src in new[] { "telegram_contact", "manual_chat", "web_form", "staff" })
            await Run(a.TenantId, $"UPDATE beauty_clients SET phone_source = '{src}' WHERE id = '{a.ClientId}'");
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_source = 'sms' WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_source");
        // phone_source без phone
        await RejectsCheck(a, $"INSERT INTO beauty_clients (tenant_id, full_name, phone_source) VALUES ('{a.TenantId}', 'X', 'staff')",
            "ck_beauty_clients_phone_source_requires_phone");
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone = NULL WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_source_requires_phone");
        // спадковий phone довільного формату допустимий (формат не перевіряється), source NULL
        await Run(a.TenantId, $"INSERT INTO beauty_clients (tenant_id, full_name, phone) VALUES ('{a.TenantId}', 'Legacy', '050 111 22 33')");
    }

    [SkippableFact]
    public async Task phone_pending_is_all_or_none_with_e164_format_and_restricted_source()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        Task Upd(string set) => Run(a.TenantId, $"UPDATE beauty_clients SET {set} WHERE id = '{a.ClientId}'");

        await Upd("phone_pending = '+380931112233', phone_pending_source = 'manual_chat', phone_pending_at = now()");
        await Upd("phone_pending = NULL, phone_pending_source = NULL, phone_pending_at = NULL");

        // усі три разом або жодне
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending = '+380931112233' WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_pending_all_or_none");
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending_source = 'manual_chat' WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_pending_all_or_none");
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending_at = now() WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_pending_all_or_none");
        // формат E.164
        foreach (var bad in new[] { "0501112233", "+0501112233", "+38050", "+12345678901234a", "+38 050 111", "380501112233" })
            await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending = '{bad}', phone_pending_source = 'manual_chat', phone_pending_at = now() WHERE id = '{a.ClientId}'",
                "ck_beauty_clients_phone_pending_format");
        await Upd("phone_pending = '+12025550143', phone_pending_source = 'telegram_contact', phone_pending_at = now()");
        // джерело pending лише telegram_contact|manual_chat
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending_source = 'web_form' WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_pending_source");
        await RejectsCheck(a, $"UPDATE beauty_clients SET phone_pending_source = 'staff' WHERE id = '{a.ClientId}'", "ck_beauty_clients_phone_pending_source");
    }

    // ---------- duplicate_of_client_id ----------

    [SkippableFact]
    public async Task duplicate_link_is_a_composite_tenant_fk_not_self_and_restrict()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await Run(a.TenantId, $"UPDATE beauty_clients SET duplicate_of_client_id = '{a.Client2Id}' WHERE id = '{a.ClientId}'");

        // не на себе
        await RejectsCheck(a, $"UPDATE beauty_clients SET duplicate_of_client_id = '{a.Client2Id}' WHERE id = '{a.Client2Id}'", "ck_beauty_clients_duplicate_not_self");
        // клієнт іншого tenant неможливий: композитний FK (tenant_id, duplicate_of_client_id)
        await Rejects(a.TenantId, $"UPDATE beauty_clients SET duplicate_of_client_id = '{b.ClientId}' WHERE id = '{a.Client2Id}'", PostgresErrorCodes.ForeignKeyViolation);
        // RESTRICT: оригінал з посиланням на нього не видаляється
        await Rejects(a.TenantId, $"DELETE FROM beauty_clients WHERE id = '{a.Client2Id}'", PostgresErrorCodes.ForeignKeyViolation);
        // зняття посилання дозволяє видалення
        await Run(a.TenantId, $"UPDATE beauty_clients SET duplicate_of_client_id = NULL WHERE id = '{a.ClientId}'");
        Assert.Equal(1, await Run(a.TenantId, $"DELETE FROM beauty_clients WHERE id = '{a.Client2Id}'"));
    }

    // ---------- beauty_conversations: phone_request ----------

    [SkippableFact]
    public async Task pending_prompt_accepts_phone_request_with_optional_appointment_and_keeps_reminder_choice_rules()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        Task Upd(string set) => Run(a.TenantId, $"UPDATE beauty_conversations SET {set} WHERE id = '{a.ConversationId}'");
        const string exp = "pending_prompt_expires_at = now() + interval '1 hour'";

        // phone_request без запису (NULL) і із записом
        await Upd($"pending_prompt = 'phone_request', {exp}");
        await Upd($"pending_prompt = 'phone_request', pending_prompt_appointment_id = '{a.AppointmentId}', {exp}");
        // reminder_choice як і раніше потребує запис і TTL
        await Upd($"pending_prompt = 'reminder_choice', pending_prompt_appointment_id = '{a.AppointmentId}', {exp}");
        await RejectsCheck(a, $"UPDATE beauty_conversations SET pending_prompt = 'reminder_choice', pending_prompt_appointment_id = NULL, {exp} WHERE id = '{a.ConversationId}'",
            "ck_beauty_conversations_pending_prompt_all_or_none");
        await Upd("pending_prompt = NULL, pending_prompt_appointment_id = NULL, pending_prompt_expires_at = NULL");
        // phone_request потребує TTL
        await RejectsCheck(a, $"UPDATE beauty_conversations SET pending_prompt = 'phone_request' WHERE id = '{a.ConversationId}'",
            "ck_beauty_conversations_pending_prompt_all_or_none");
        // запис/TTL без питання неможливі
        await RejectsCheck(a, $"UPDATE beauty_conversations SET pending_prompt_appointment_id = '{a.AppointmentId}' WHERE id = '{a.ConversationId}'",
            "ck_beauty_conversations_pending_prompt_all_or_none");
        await RejectsCheck(a, $"UPDATE beauty_conversations SET {exp} WHERE id = '{a.ConversationId}'", "ck_beauty_conversations_pending_prompt_all_or_none");
        // невідомий тип
        await RejectsCheck(a, $"UPDATE beauty_conversations SET pending_prompt = 'other', {exp} WHERE id = '{a.ConversationId}'", "ck_beauty_conversations_pending_prompt");
    }

    // ---------- beauty_messages: contact ----------

    [SkippableFact]
    public async Task kind_contact_is_accepted_and_older_kinds_remain()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        foreach (var kind in new[] { "text", "command", "unsupported", "callback" })
            await Run(a.TenantId, InsertMsg(a, kind));
        await Run(a.TenantId, InsertMsg(a, "contact"));
        await Run(a.TenantId, InsertMsg(a, "contact", "+380501112233", "own"));
        await Run(a.TenantId, InsertMsg(a, "contact", null, "foreign"));
        await Run(a.TenantId, InsertMsg(a, "contact", null, "invalid"));
        await RejectsCheck(a, InsertMsg(a, "sticker"), "ck_beauty_messages_kind");
    }

    [SkippableFact]
    public async Task contact_phone_is_only_for_kind_contact_with_own_verdict_and_e164()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        // лише для kind=contact
        foreach (var kind in new[] { "text", "command", "unsupported", "callback" })
        {
            await RejectsCheck(a, InsertMsg(a, kind, "+380501112233", "own"), "ck_beauty_messages_contact_fields");
            await RejectsCheck(a, InsertMsg(a, kind, null, "own"), "ck_beauty_messages_contact_fields");
        }
        // номер лише для verdict=own (чужий номер не зберігається ніде)
        await RejectsCheck(a, InsertMsg(a, "contact", "+380501112233", "foreign"), "ck_beauty_messages_contact_phone");
        await RejectsCheck(a, InsertMsg(a, "contact", "+380501112233", "invalid"), "ck_beauty_messages_contact_phone");
        await Rejects(a.TenantId, $@"INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind, contact_phone)
            VALUES ('{a.TenantId}', '{a.ConversationId}', 'inbound', 'client', 'x', 'contact', '+380501112233')", PostgresErrorCodes.CheckViolation, "ck_beauty_messages_contact_phone");
        // формат E.164
        foreach (var bad in new[] { "0501112233", "+0501112233", "+38050", "+12345678901234a", "380501112233" })
            await RejectsCheck(a, InsertMsg(a, "contact", bad, "own"), "ck_beauty_messages_contact_phone");
        // verdict: тільки own|foreign|invalid
        await RejectsCheck(a, InsertMsg(a, "contact", null, "maybe"), "ck_beauty_messages_contact_verdict");
        // обнулення (конвеєр / sweeper) дозволене; kind=contact лишається
        await Run(a.TenantId, InsertMsg(a, "contact", "+12025550143", "own"));
        Assert.Equal(1, await Run(a.TenantId, "UPDATE beauty_messages SET contact_phone = NULL WHERE contact_phone IS NOT NULL"));
        Assert.Equal(0L, await Count(a.TenantId, "SELECT 1 FROM beauty_messages WHERE contact_phone IS NOT NULL"));
        Assert.Equal(1L, await Count(a.TenantId, "SELECT 1 FROM beauty_messages WHERE kind = 'contact' AND contact_verdict = 'own'"));
    }

    [SkippableFact]
    public async Task sweeper_index_is_partial_on_contact_phone()
    {
        SkipIfNoDb();
        var def = (string?)await Scalar(null, "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_beauty_messages_contact_phone'");
        Assert.NotNull(def);
        Assert.Contains("tenant_id, created_at", def);
        Assert.Contains("contact_phone IS NOT NULL", def);
    }
}
