using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace BeautyCrm.Tests.Channels;

/// <summary>
/// TASK-768 (§25.4, §25.5.7, §25.9, §25.10 сценарії 1, 17, 19): контакт Telegram від парсера до БД (реальний PostgreSQL з RLS),
/// порт читання з обнулінням для конвеєра і sweeper. Реального Telegram немає.
/// </summary>
public sealed class ContactPhoneDbTests : IClassFixture<PostgresRlsFixture>
{
    private const string Own = "380501112233";
    private const string OwnE164 = "+380501112233";
    private const string Other = "380509998877";

    private readonly PostgresRlsFixture _db;
    public ContactPhoneDbTests(PostgresRlsFixture db) => _db = db;
    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Env(Guid TenantId, Guid ChannelId);

    private async Task<Env> SeedAsync()
    {
        var tenantId = Guid.NewGuid();
        await Admin($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', 'cp-{tenantId:N}')");
        await using var ctx = _db.CreateContext(tenantId);
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot", IsActive = true };
        ctx.Add(ch);
        await ctx.SaveChangesAsync();
        return new Env(tenantId, ch.Id);
    }

    private async Task Admin(string sql)
    {
        await using var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> AdminScalar<T>(string sql)
    {
        await using var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)v;
    }

    private static string Update(long messageId, string contact, string chat = "{\"id\":42,\"type\":\"private\"}",
        string from = "{\"id\":42,\"is_bot\":false,\"first_name\":\"Ann\"}") =>
        $"{{\"update_id\":1,\"message\":{{\"message_id\":{messageId},\"from\":{from},\"chat\":{chat},\"date\":1700000000,\"contact\":{contact}}}}}";

    private static string OwnContact(string phone) => $"{{\"phone_number\":\"{phone}\",\"first_name\":\"Ann\",\"last_name\":\"Secret\",\"user_id\":42,\"vcard\":\"TEL:{phone}\"}}";

    private sealed class Ingest(PostgresRlsFixture fx, Env env, bool phoneEnabled, LogCapture log) : IAsyncDisposable
    {
        private readonly BeautyDbContext _ctx = fx.CreateContext(env.TenantId);
        private readonly ChannelRequestContext _channel = new();
        private readonly TelegramAdapter _adapter = new(new HttpClient(), new ChCreds());

        public async Task<int> RunAsync(string update)
        {
            // AsyncLocal не повертається з async-методу: контекст каналу відкривається на кожен прохід (як у webhook/polling)
            using var _ = _channel.Begin(new ResolvedChannel(env.TenantId, env.ChannelId, "telegram", new ChannelCredentials("t", "s")));
            var factory = LoggerFactory.Create(b => b.AddProvider(log));
            var core = new InboundIngestCore(new EfChannelMessageRepository(_ctx, _channel, new ChatPhoneOptions { Enabled = phoneEnabled }),
                new SwitchQueue(), null, _channel, factory.CreateLogger<InboundIngestCore>());
            var parsed = await _adapter.ParseInboundAsync(update);
            return (await core.IngestAsync(parsed, default)).Count;
        }

        public async ValueTask DisposeAsync() => await _ctx.DisposeAsync();
    }

    private sealed record Row(string Kind, string Body, string? Phone, string? Verdict, string? Key);

    private async Task<List<Row>> RowsAsync(Guid tenantId)
    {
        var rows = new List<Row>();
        await using var c = new NpgsqlConnection(_db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT kind, body, contact_phone, contact_verdict, idempotency_key FROM beauty_messages WHERE tenant_id = '{tenantId}' ORDER BY created_at, id", c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            rows.Add(new Row(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return rows;
    }

    /// <summary>БД-скан (§25.10 сц. 6, 19): жоден рядок жодної таблиці tenant не містить цифр номера (повний текст рядка).</summary>
    private async Task AssertNumberNowhereAsync(Guid tenantId, string digits)
    {
        foreach (var table in new[] { "beauty_messages", "beauty_conversations", "beauty_clients", "beauty_channels", "beauty_ai_actions" })
        {
            var n = await AdminScalar<long>($"SELECT count(*) FROM {table} t WHERE t.tenant_id = '{tenantId}' AND t::text LIKE '%{digits[3..]}%'");
            Assert.True(n == 0, $"phone digits found in {table}");
        }
    }

    // ------------------------------------------------------------------ збереження

    [SkippableFact]
    public async Task own_contact_is_stored_with_marker_body_verdict_and_temporary_e164_number()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, true, new LogCapture());
        Assert.Equal(1, await ing.RunAsync(Update(7, OwnContact(Own))));
        var row = Assert.Single(await RowsAsync(env.TenantId));
        Assert.Equal(("contact", "[контакт]", OwnE164, "own"), (row.Kind, row.Body, row.Phone, row.Verdict));
        Assert.DoesNotContain("501112233", row.Body);
        Assert.DoesNotContain("501112233", row.Key ?? "");
        // розмова створена для приватного чату; номер у beauty_conversations/clients відсутній
        Assert.Equal(1L, await AdminScalar<long>($"SELECT count(*) FROM beauty_conversations WHERE tenant_id = '{env.TenantId}' AND external_chat_id = '42'"));
        Assert.Equal(0L, await AdminScalar<long>($"SELECT count(*) FROM beauty_clients WHERE tenant_id = '{env.TenantId}'"));
    }

    [SkippableFact]
    public async Task foreign_contact_is_stored_without_any_number_and_the_database_holds_no_trace_of_it()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, true, new LogCapture());
        await ing.RunAsync(Update(7, $"{{\"phone_number\":\"{Other}\",\"first_name\":\"Bob\",\"last_name\":\"Stranger\",\"user_id\":43,\"vcard\":\"TEL:{Other}\"}}"));
        await ing.RunAsync(Update(8, $"{{\"phone_number\":\"{Other}\",\"first_name\":\"Bob\"}}")); // без user_id
        var rows = await RowsAsync(env.TenantId);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(("contact", "[контакт]", null, "foreign"), (r.Kind, r.Body, r.Phone, r.Verdict)));
        await AssertNumberNowhereAsync(env.TenantId, Other);
        Assert.Equal(0L, await AdminScalar<long>($"SELECT count(*) FROM beauty_messages m WHERE m.tenant_id = '{env.TenantId}' AND (m::text LIKE '%Bob%' OR m::text LIKE '%Stranger%')"));
    }

    [SkippableFact]
    public async Task invalid_own_number_is_stored_as_invalid_without_a_number()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, true, new LogCapture());
        await ing.RunAsync(Update(7, OwnContact("12345")));
        var row = Assert.Single(await RowsAsync(env.TenantId));
        Assert.Equal(("contact", "[контакт]", null, "invalid"), (row.Kind, row.Body, row.Phone, row.Verdict));
    }

    [SkippableFact]
    public async Task chat_phone_disabled_keeps_the_verdict_but_never_writes_the_number()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, false, new LogCapture());
        await ing.RunAsync(Update(7, OwnContact(Own)));
        var row = Assert.Single(await RowsAsync(env.TenantId));
        Assert.Equal(("contact", "own", null), (row.Kind, row.Verdict, row.Phone));
        await AssertNumberNowhereAsync(env.TenantId, Own);
    }

    [SkippableFact]
    public async Task group_chat_and_bot_contacts_are_not_stored()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, true, new LogCapture());
        Assert.Equal(0, await ing.RunAsync(Update(7, OwnContact(Own), chat: "{\"id\":-100,\"type\":\"supergroup\"}")));
        Assert.Equal(0, await ing.RunAsync(Update(8, OwnContact(Own), from: "{\"id\":42,\"is_bot\":true}")));
        Assert.Empty(await RowsAsync(env.TenantId));
    }

    [SkippableFact]
    public async Task duplicate_delivery_of_the_same_contact_is_deduplicated_like_a_message()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, true, new LogCapture());
        Assert.Equal(1, await ing.RunAsync(Update(7, OwnContact(Own))));
        Assert.Equal(0, await ing.RunAsync(Update(7, OwnContact(Own))));
        Assert.Equal(1, await ing.RunAsync(Update(8, OwnContact(Own)))); // інше повідомлення = новий рядок
        Assert.Equal(2, (await RowsAsync(env.TenantId)).Count);
    }

    [SkippableFact]
    public async Task repository_never_trusts_forged_fields_of_inbound_message()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        await using var ctx = _db.CreateContext(env.TenantId);
        var channel = new ChannelRequestContext();
        using var scope = channel.Begin(new ResolvedChannel(env.TenantId, env.ChannelId, "telegram", new ChannelCredentials("t", "s")));
        var repo = new EfChannelMessageRepository(ctx, channel, new ChatPhoneOptions { Enabled = true });
        InboundMessage Msg(string id, string text, string? verdict, string? phone, string kind = InboundKinds.Contact) =>
            new("telegram", id, "42", "42", null, text, DateTimeOffset.UtcNow, kind) { ContactVerdict = verdict, ContactPhone = phone };
        var saved = await repo.SaveInboundAsync([
            Msg("a", "мій номер +380501112233", "own", OwnE164),      // тіло з номером -> маркер
            Msg("b", "x", "own", "not-a-phone"),                       // verdict own без валідного E.164 -> invalid
            Msg("c", "x", "own", "+0123456789"),
            Msg("d", "x", "weird", OwnE164),                           // невідомий verdict -> foreign, без номера
            Msg("e", "x", null, OwnE164),
            Msg("f", "привіт", null, OwnE164, InboundKinds.Text),      // не контакт: поля ігноруються
        ], default);
        Assert.Equal(6, saved.Count);
        var rows = await RowsAsync(env.TenantId);
        Assert.Equal(["contact", "contact", "contact", "contact", "contact", "text"], rows.Select(r => r.Kind).ToArray());
        Assert.Equal([OwnE164, null, null, null, null, null], rows.Select(r => r.Phone).ToArray());
        Assert.Equal(["own", "invalid", "invalid", "foreign", "foreign", null], rows.Select(r => r.Verdict).ToArray());
        Assert.Equal(["[контакт]", "[контакт]", "[контакт]", "[контакт]", "[контакт]", "привіт"], rows.Select(r => r.Body).ToArray());
    }

    [SkippableFact]
    public async Task logs_exceptions_and_to_string_never_contain_the_number_or_contact_names()
    {
        SkipIfNoDb();
        var env = await SeedAsync();
        var log = new LogCapture();
        await using (var ing = new Ingest(_db, env, true, log))
        {
            await ing.RunAsync(Update(7, OwnContact(Own)));
            await ing.RunAsync(Update(8, $"{{\"phone_number\":\"{Other}\",\"first_name\":\"Bob\",\"last_name\":\"Stranger\",\"user_id\":43}}"));
            await ing.RunAsync(Update(7, OwnContact(Own)));
        }
        var read = new ContactPhoneRead("own", OwnE164);
        Assert.DoesNotContain("501112233", read.ToString());
        Assert.Contains("HasPhone = True", read.ToString());
        var messages = await new TelegramAdapter(new HttpClient(), new ChCreds()).ParseInboundAsync(Update(9, OwnContact(Own)));
        Assert.All(messages, m => Assert.DoesNotContain("501112233", m.ToString()));
        var all = log.All;
        foreach (var secret in new[] { "501112233", "509998877", "Secret", "Stranger", "Bob", "VCARD" })
            Assert.DoesNotContain(secret, all, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task tenant_isolation_contact_rows_are_invisible_to_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedAsync();
        var b = await SeedAsync();
        await using (var ing = new Ingest(_db, a, true, new LogCapture())) await ing.RunAsync(Update(7, OwnContact(Own)));
        await using var ctxB = _db.CreateContext(b.TenantId);
        Assert.Equal(0, await ctxB.Messages.AsNoTracking().CountAsync(m => m.Kind == "contact"));
        // чужий tenant не бачить рядок і через порт
        var id = await AdminScalar<Guid>($"SELECT id FROM beauty_messages WHERE tenant_id = '{a.TenantId}'");
        var store = new EfContactPhoneStore(ctxB);
        Assert.Null(await store.ReadAsync(id, default));
        Assert.Null(await store.TakeAsync(id, default));
        await store.ClearContactPhoneAsync(id, default);
        Assert.Equal(OwnE164, (await RowsAsync(a.TenantId)).Single().Phone);
    }

    // ------------------------------------------------------------------ порт для конвеєра (TASK-770)

    private async Task<(Env Env, Guid MessageId)> SeedContactAsync(bool enabled = true, string phone = Own)
    {
        var env = await SeedAsync();
        await using var ing = new Ingest(_db, env, enabled, new LogCapture());
        await ing.RunAsync(Update(7, OwnContact(phone)));
        return (env, await AdminScalar<Guid>($"SELECT id FROM beauty_messages WHERE tenant_id = '{env.TenantId}'"));
    }

    [SkippableFact]
    public async Task store_read_does_not_clear_and_take_returns_the_number_once_and_clears_it()
    {
        SkipIfNoDb();
        var (env, id) = await SeedContactAsync();
        await using var ctx = _db.CreateContext(env.TenantId);
        var store = new EfContactPhoneStore(ctx);

        var read = await store.ReadAsync(id, default);
        Assert.Equal(("own", OwnE164), (read!.Verdict, read.Phone));
        Assert.Equal(OwnE164, (await RowsAsync(env.TenantId)).Single().Phone);

        Assert.Equal(OwnE164, await store.TakeAsync(id, default));
        Assert.Null((await RowsAsync(env.TenantId)).Single().Phone);
        Assert.Null(await store.TakeAsync(id, default));
        var after = await store.ReadAsync(id, default);
        Assert.Equal(("own", null), (after!.Verdict, after.Phone));
    }

    [SkippableFact]
    public async Task store_take_joins_the_pipeline_transaction_and_rolls_back_with_it()
    {
        SkipIfNoDb();
        var (env, id) = await SeedContactAsync();
        await using var ctx = _db.CreateContext(env.TenantId);
        var store = new EfContactPhoneStore(ctx);
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            Assert.Equal(OwnE164, await store.TakeAsync(id, default));
            await tx.RollbackAsync();
        }
        Assert.Equal(OwnE164, (await RowsAsync(env.TenantId)).Single().Phone);

        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            Assert.Equal(OwnE164, await store.TakeAsync(id, default));
            await tx.CommitAsync();
        }
        Assert.Null((await RowsAsync(env.TenantId)).Single().Phone);
    }

    [SkippableFact]
    public async Task store_take_is_atomic_under_concurrency_only_one_caller_gets_the_number()
    {
        SkipIfNoDb();
        var (env, id) = await SeedContactAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var ctx = _db.CreateContext(env.TenantId);
            return await new EfContactPhoneStore(ctx).TakeAsync(id, default);
        }));
        Assert.Single(results.Where(r => r is not null));
    }

    [SkippableFact]
    public async Task store_clear_nulls_the_number_idempotently_and_read_ignores_non_contact_messages()
    {
        SkipIfNoDb();
        var (env, id) = await SeedContactAsync();
        await using var ctx = _db.CreateContext(env.TenantId);
        var store = new EfContactPhoneStore(ctx);
        await store.ClearContactPhoneAsync(id, default);
        await store.ClearContactPhoneAsync(id, default);
        Assert.Null((await RowsAsync(env.TenantId)).Single().Phone);

        await using var ing = new Ingest(_db, env, true, new LogCapture());
        await ing.RunAsync($"{{\"update_id\":2,\"message\":{{\"message_id\":9,\"from\":{{\"id\":42}},\"chat\":{{\"id\":42,\"type\":\"private\"}},\"text\":\"привіт\"}}}}");
        var textId = await AdminScalar<Guid>($"SELECT id FROM beauty_messages WHERE tenant_id = '{env.TenantId}' AND kind = 'text'");
        Assert.Null(await store.ReadAsync(textId, default));
    }

    // ------------------------------------------------------------------ sweeper (§25.5.7)

    private ContactPhoneSweeper Sweeper(BeautyDbContext ctx, Guid tenantId, int hours = 24, LogCapture? log = null)
    {
        var factory = LoggerFactory.Create(b => b.AddProvider(log ?? new LogCapture()));
        return new ContactPhoneSweeper(ctx, new TenantContext(tenantId), new ChatPhoneOptions { Enabled = true, ContactRetentionHours = hours },
            TimeProvider.System, factory.CreateLogger<ContactPhoneSweeper>());
    }

    [SkippableFact]
    public async Task sweeper_nulls_numbers_older_than_retention_and_keeps_fresh_ones()
    {
        SkipIfNoDb();
        var (env, oldId) = await SeedContactAsync();
        await Admin($"UPDATE beauty_messages SET created_at = now() - interval '25 hours' WHERE id = '{oldId}'");
        await using (var ing = new Ingest(_db, env, true, new LogCapture())) await ing.RunAsync(Update(8, OwnContact(Own))); // свіже

        var log = new LogCapture();
        await using var ctx = _db.CreateContext(env.TenantId);
        Assert.Equal(1, await Sweeper(ctx, env.TenantId, log: log).SweepTenantAsync(env.TenantId, default));

        var rows = await RowsAsync(env.TenantId);
        Assert.Equal([null, OwnE164], rows.Select(r => r.Phone).ToArray());
        Assert.Equal(["own", "own"], rows.Select(r => r.Verdict).ToArray()); // verdict лишається
        Assert.DoesNotContain("501112233", log.All);
        Assert.Contains("count=1", log.All);
        Assert.Equal(0, await Sweeper(ctx, env.TenantId).SweepTenantAsync(env.TenantId, default)); // повторно нічого
    }

    [SkippableFact]
    public async Task sweeper_honours_configured_retention_and_boundary()
    {
        SkipIfNoDb();
        var (env, id) = await SeedContactAsync();
        await Admin($"UPDATE beauty_messages SET created_at = now() - interval '23 hours' WHERE id = '{id}'");
        await using var ctx = _db.CreateContext(env.TenantId);
        Assert.Equal(0, await Sweeper(ctx, env.TenantId, 24).SweepTenantAsync(env.TenantId, default));
        Assert.Equal(1, await Sweeper(ctx, env.TenantId, 12).SweepTenantAsync(env.TenantId, default));
    }

    [SkippableFact]
    public async Task sweeper_works_in_batches_of_500_and_is_confined_to_its_tenant()
    {
        SkipIfNoDb();
        var (a, _) = await SeedContactAsync();
        var (b, bId) = await SeedContactAsync();
        var convA = await AdminScalar<Guid>($"SELECT id FROM beauty_conversations WHERE tenant_id = '{a.TenantId}'");
        await Admin($"""
            INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, kind, contact_phone, contact_verdict, created_at)
            SELECT '{a.TenantId}', '{convA}', 'inbound', 'client', '[контакт]', 'contact', '+380501112233', 'own', now() - interval '30 hours' FROM generate_series(1, 1203)
            """);
        await Admin($"UPDATE beauty_messages SET created_at = now() - interval '30 hours' WHERE id = '{bId}'");

        await using (var ctx = _db.CreateContext(a.TenantId))
            Assert.Equal(1203, await Sweeper(ctx, a.TenantId).SweepTenantAsync(a.TenantId, default)); // 500 + 500 + 203; свіжий seed лишається
        Assert.Equal(1L, await AdminScalar<long>($"SELECT count(*) FROM beauty_messages WHERE tenant_id = '{a.TenantId}' AND contact_phone IS NOT NULL"));
        // чужий tenant не зачеплений (RLS)
        Assert.Equal(OwnE164, (await RowsAsync(b.TenantId)).Single().Phone);
        await using (var ctx = _db.CreateContext(b.TenantId))
            Assert.Equal(1, await Sweeper(ctx, b.TenantId).SweepTenantAsync(b.TenantId, default));
    }

    [SkippableFact]
    public async Task sweeper_refuses_a_mismatching_tenant_scope()
    {
        SkipIfNoDb();
        var (a, _) = await SeedContactAsync();
        var (b, _) = await SeedContactAsync();
        await using var ctx = _db.CreateContext(a.TenantId);
        var sweeper = new ContactPhoneSweeper(ctx, new TenantContext(a.TenantId), new ChatPhoneOptions(), TimeProvider.System, NullLogger<ContactPhoneSweeper>.Instance);
        await Assert.ThrowsAsync<TenantMismatchException>(() => sweeper.SweepTenantAsync(b.TenantId, default));
    }
}
