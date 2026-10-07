using System.Net;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Regression;

/// <summary>
/// TASK-681: webhook із невалідним підписом по HTTP (реальні адаптери, не mock) + ізоляція tenant на ВСІХ таблицях
/// із tenant_id (каталожна перевірка RLS + поведінкова: чужий tenant і відсутній tenant не бачать нічого).
/// </summary>
public sealed class WebhookAndRlsRegressionTests : IClassFixture<AuthApiFixture>
{
    private const string TgBody = """{"update_id":1,"message":{"message_id":7,"from":{"id":42,"first_name":"Ann"},"chat":{"id":42},"date":1700000000,"text":"Hello"}}""";
    private const string Secret = "tg-webhook-secret-123";

    private readonly AuthApiFixture _fx;
    private readonly RegressionHarness _h;
    private HttpClient? _real;

    public WebhookAndRlsRegressionTests(AuthApiFixture fx)
    {
        _fx = fx;
        _h = new RegressionHarness(fx);
    }

    private void NeedDb() => Skip.If(_fx.SkipReason is not null, _fx.SkipReason);

    /// <summary>Фабрика з реальними адаптерами каналів (UseMocks=false) і ключем шифрування секретів.</summary>
    private HttpClient RealChannelsClient() => _real ??= _fx.Factory!.WithWebHostBuilder(b =>
    {
        b.UseSetting("Channels:UseMocks", "false");
        b.UseSetting("Channels:EncryptionKey", Convert.ToBase64String(Enumerable.Range(10, 32).Select(i => (byte)i).ToArray()));
    }).CreateClient();

    private async Task<Guid> CreateTelegramChannelAsync(RegressionHarness.Salon s, string secret = Secret)
    {
        var id = Guid.NewGuid();
        var r = await _fx.Send(HttpMethod.Put, $"/api/beauty/channels/{id}",
            new { type = "telegram", name = "tg", isActive = true, token = "123456:ABCDEF", webhookSecret = secret },
            s.OwnerToken, client: RealChannelsClient());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return id;
    }

    private Task<HttpResponseMessage> PostWebhookAsync(string channel, Guid channelId, string? secretHeader, string body = TgBody)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/beauty/webhooks/{channel}/{channelId}")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        if (secretHeader is not null) req.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secretHeader);
        return RealChannelsClient().SendAsync(req); // без Authorization: webhook публічний, автентичність = підпис
    }

    private async Task<int> InboundCountAsync(Guid tenantId)
    {
        await using var ctx = _fx.Db.CreateContext(tenantId);
        return await ctx.Messages.CountAsync();
    }

    [SkippableFact]
    public async Task regression_webhook_with_invalid_or_missing_signature_is_401_and_stores_nothing()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var channelId = await CreateTelegramChannelAsync(s);

        foreach (var header in new string?[] { null, "", "wrong-secret", Secret + "x", Secret.ToUpperInvariant() })
        {
            var r = await PostWebhookAsync("telegram", channelId, header);
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal("invalid_signature", await RegressionHarness.CodeAsync(r));
        }
        Assert.Equal(0, await InboundCountAsync(s.Tenant.TenantId));
    }

    [SkippableFact]
    public async Task regression_webhook_with_valid_secret_stores_once_and_redelivery_is_deduplicated()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var channelId = await CreateTelegramChannelAsync(s);

        var ok = await PostWebhookAsync("telegram", channelId, Secret);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, (await Read<JsonElement>(ok)).GetProperty("accepted").GetInt32());
        var again = await PostWebhookAsync("telegram", channelId, Secret);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, (await Read<JsonElement>(again)).GetProperty("accepted").GetInt32());
        Assert.Equal(1, await InboundCountAsync(s.Tenant.TenantId));
    }

    [SkippableFact]
    public async Task regression_webhook_secret_of_another_tenant_and_unknown_channel_are_rejected()
    {
        NeedDb();
        var a = await _h.CreateSalonAsync();
        var b = await _h.CreateSalonAsync();
        var chA = await CreateTelegramChannelAsync(a, secret: "secret-of-tenant-A-0001");
        var chB = await CreateTelegramChannelAsync(b, secret: "secret-of-tenant-B-0002");

        // секрет B на каналі A (і навпаки) не проходить; повідомлення не потрапляють ні в A, ні в B
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("telegram", chA, "secret-of-tenant-B-0002")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("telegram", chB, "secret-of-tenant-A-0001")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostWebhookAsync("telegram", Guid.NewGuid(), "secret-of-tenant-A-0001")).StatusCode);
        Assert.Equal(0, await InboundCountAsync(a.Tenant.TenantId));
        Assert.Equal(0, await InboundCountAsync(b.Tenant.TenantId));

        // валідний секрет A лягає лише в tenant A
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync("telegram", chA, "secret-of-tenant-A-0001")).StatusCode);
        Assert.Equal(1, await InboundCountAsync(a.Tenant.TenantId));
        Assert.Equal(0, await InboundCountAsync(b.Tenant.TenantId));
    }

    [SkippableFact]
    public async Task regression_webhook_for_channel_without_secret_fails_closed_and_secrets_are_masked_in_settings()
    {
        NeedDb();
        var s = await _h.CreateSalonAsync();
        var id = Guid.NewGuid();
        var put = await _fx.Send(HttpMethod.Put, $"/api/beauty/channels/{id}",
            new { type = "telegram", name = "tg", isActive = true, token = "123456:ABCDEF" }, s.OwnerToken, client: RealChannelsClient());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        // секрет не заданий -> навіть порожній/будь-який заголовок не відкриває канал
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("telegram", id, "")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostWebhookAsync("telegram", id, "anything")).StatusCode);
        Assert.Equal(0, await InboundCountAsync(s.Tenant.TenantId));

        var raw = await (await _fx.Send(HttpMethod.Get, "/api/beauty/channels", bearer: s.OwnerToken, client: RealChannelsClient())).Content.ReadAsStringAsync();
        Assert.DoesNotContain("123456:ABCDEF", raw);
        Assert.Contains("********", raw);
    }

    // ---------- RLS на всіх таблицях ----------

    /// <summary>Таблиці, що мають бути під RLS (контракт §2 + auth §10 + політика скасування §11).</summary>
    private static readonly string[] ExpectedTenantTables =
    [
        "beauty_locations", "beauty_specialists", "beauty_specialist_locations", "beauty_services", "beauty_service_prices",
        "beauty_appointments", "beauty_clients", "beauty_client_notes", "beauty_promotions", "beauty_promotion_locations",
        "beauty_promotion_services", "beauty_channels", "beauty_conversations", "beauty_messages", "beauty_ai_actions",
        "beauty_reminders", "beauty_payments", "beauty_cancellation_settings", "users", "invites", "refresh_tokens",
    ];

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(_fx.Db.AppConnectionString);
        await c.OpenAsync();
        return c;
    }

    [SkippableFact]
    public async Task regression_rls_every_table_with_tenant_id_has_forced_rls_and_a_policy()
    {
        NeedDb();
        await using var conn = await OpenAsync();
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema='public' AND column_name='tenant_id' ORDER BY 1", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) tables.Add(rd.GetString(0));

        // жодна очікувана таблиця не загубилась (і нова таблиця з tenant_id автоматично потрапляє в перевірку нижче)
        Assert.Empty(ExpectedTenantTables.Except(tables));

        var bad = new List<string>();
        foreach (var t in tables)
        {
            await using var q = new NpgsqlCommand(
                "SELECT c.relrowsecurity, c.relforcerowsecurity, (SELECT count(*) FROM pg_policies p WHERE p.schemaname='public' AND p.tablename=@t) " +
                "FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relname=@t", conn);
            q.Parameters.AddWithValue("t", t);
            await using var rd = await q.ExecuteReaderAsync();
            Assert.True(await rd.ReadAsync(), t);
            if (!rd.GetBoolean(0) || !rd.GetBoolean(1) || rd.GetInt64(2) == 0) bad.Add(t);
        }
        Assert.True(bad.Count == 0, "Tables without ENABLE+FORCE RLS or without a policy: " + string.Join(", ", bad));

        // tenants (без tenant_id, ключ = id) теж під RLS
        await using var tq = new NpgsqlCommand(
            "SELECT c.relrowsecurity, c.relforcerowsecurity FROM pg_class c WHERE c.relname='tenants' AND c.relkind='r'", conn);
        await using var trd = await tq.ExecuteReaderAsync();
        Assert.True(await trd.ReadAsync());
        Assert.True(trd.GetBoolean(0) && trd.GetBoolean(1));
    }

    [SkippableFact]
    public async Task regression_rls_other_tenant_and_missing_tenant_see_no_rows_in_any_table()
    {
        NeedDb();
        var a = await SeedRichTenantAsync();
        var b = await _h.CreateSalonAsync(); // сусід без жодних даних, окрім власних довідників
        List<string> tables = [];
        await using (var conn = await OpenAsync())
        await using (var cmd = new NpgsqlCommand(
            "SELECT table_name FROM information_schema.columns WHERE table_schema='public' AND column_name='tenant_id'", conn))
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) tables.Add(rd.GetString(0));

        async Task<long> CountAsync(string table, Guid? asTenant, Guid ofTenant)
        {
            await using var conn = await OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            if (asTenant is { } t)
            {
                await using var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, true)", conn, tx);
                set.Parameters.AddWithValue("t", t.ToString());
                await set.ExecuteNonQueryAsync();
            }
            await using var q = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\" WHERE tenant_id = @o", conn, tx);
            q.Parameters.AddWithValue("o", ofTenant);
            return (long)(await q.ExecuteScalarAsync())!;
        }

        var nonEmptyForOwner = new List<string>();
        foreach (var table in tables)
        {
            Assert.Equal(0, await CountAsync(table, b.Tenant.TenantId, a.Tenant.TenantId)); // чужий tenant
            Assert.Equal(0, await CountAsync(table, null, a.Tenant.TenantId));              // tenant не заданий
            if (await CountAsync(table, a.Tenant.TenantId, a.Tenant.TenantId) > 0) nonEmptyForOwner.Add(table);
        }
        // перевірка має сенс лише якщо дані справді є: у власника видно рядки у ключових таблицях
        foreach (var must in new[] { "beauty_appointments", "beauty_payments", "beauty_reminders", "beauty_clients", "beauty_promotions",
                                     "beauty_channels", "beauty_messages", "beauty_cancellation_settings", "users", "beauty_conversations" })
            Assert.Contains(must, nonEmptyForOwner);
    }

    private async Task<RegressionHarness.Salon> SeedRichTenantAsync()
    {
        var s = await _h.CreateSalonAsync(networkPrice: 800m);
        var first = (await _h.SlotsAsync(s, RegressionHarness.SlotDate()))[0].GetProperty("startsAt").GetDateTimeOffset();
        Assert.Equal(HttpStatusCode.Created, (await _h.BookAsync(s, first, reminder: "2h", payment: "card")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _fx.Send(HttpMethod.Post, "/api/beauty/promotions",
            new { name = "p", discountType = "percent", discountValue = 5m, isActive = true, locationIds = Array.Empty<Guid>(), serviceIds = Array.Empty<Guid>() },
            s.OwnerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.Send(HttpMethod.Put, "/api/beauty/settings/cancellation",
            new { windowHours = 24, refundPercentInWindow = 40, refundPercentOutside = 100, deductFee = false, feePercent = 0 }, s.OwnerToken)).StatusCode);
        var channelId = await CreateTelegramChannelAsync(s);
        Assert.Equal(HttpStatusCode.OK, (await PostWebhookAsync("telegram", channelId, Secret)).StatusCode);
        return s;
    }
}
