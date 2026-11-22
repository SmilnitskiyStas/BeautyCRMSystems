using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// TASK-895 (ADR-025 §12, §32.24.2): 4 нові таблиці етапу 2b під FORCE RLS: ізоляція tenant, WITH CHECK, fail-closed без контексту,
/// композитні FK між tenant, відображення EF-сутностей (byte[] хеші, шифрований chat_id як text), append-only журналу для app-ролі
/// (тригер діє попри DML-права тестової ролі). Без реального годинника: усі часові значення задаються тестом.
/// </summary>
public sealed class InventoryStage2bRlsTests : IClassFixture<PostgresRlsFixture>
{
    private static readonly string[] Tables =
        ["beauty_staff_telegram_links", "beauty_staff_telegram_link_events", "beauty_staff_notifications", "beauty_staff_notification_alerts"];

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private readonly PostgresRlsFixture _db;

    public InventoryStage2bRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId, Guid ChannelId, Guid LinkId, Guid NotificationId, Guid AlertId);

    private async Task<Seed> SeedTenantAsync()
    {
        var t = Guid.NewGuid();
        var user = Guid.NewGuid();
        Guid channel, loc, product;
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            async Task Exec(string sql) { await using var c = new NpgsqlCommand(sql, admin); await c.ExecuteNonQueryAsync(); }
            async Task<Guid> Id(string sql) { await using var c = new NpgsqlCommand(sql, admin); return (Guid)(await c.ExecuteScalarAsync())!; }
            await Exec($@"INSERT INTO tenants (id, name, slug) VALUES ('{t}', 'T', 'inv2b-{t:N}');
                {IdentitySeed.UserSql(t, $"o@{t:N}.test", "Owner", "x", "owner", user)}");
            channel = await Id($"INSERT INTO beauty_channels (tenant_id, type, name, is_active) VALUES ('{t}', 'telegram', 'bot', true) RETURNING id");
            loc = await Id($"INSERT INTO beauty_locations (tenant_id, name, timezone, is_active) VALUES ('{t}', 'A', 'Europe/Kyiv', true) RETURNING id");
            product = await Id($"INSERT INTO beauty_products (tenant_id, name, unit, purpose, updated_by_user_id) VALUES ('{t}', 'Шампунь', 'ml', 'salon_use', '{user}') RETURNING id");
        }

        await using var ctx = _db.CreateContext(t);
        var alert = new StaffAlert { Kind = "low_stock", LocationId = loc, ProductId = product, Quantity = 1m, DedupeKey = $"low:{Guid.NewGuid():N}" };
        var link = new StaffTelegramLink
        {
            UserId = user, ChannelId = channel, Status = "active", ChatIdEncrypted = "enc", ChatIdHash = new byte[32], ConsentVersion = "inv-tg-v1",
            ConsentAt = T0, ClaimedAt = T0, BotConfirmedAt = T0, ActivatedAt = T0,
        };
        ctx.AddRange(alert, link);
        await ctx.SaveChangesAsync();

        var notification = new StaffNotification { LinkId = link.Id, Kind = "alert", Body = "Склад · A", DedupeKey = $"alert:{link.Id:N}:{alert.Id:N}", NextAttemptAt = T0 };
        ctx.Add(notification);
        await ctx.SaveChangesAsync();
        ctx.AddRange(
            new StaffNotificationAlert { NotificationId = notification.Id, AlertId = alert.Id, LinkId = link.Id },
            new StaffTelegramLinkEvent { LinkId = link.Id, UserId = user, Event = "activated", Actor = "bot", CreatedAt = T0 });
        await ctx.SaveChangesAsync();
        return new Seed(t, user, channel, link.Id, notification.Id, alert.Id);
    }

    private async Task Rejects(Guid? tenant, string sql, string state)
    {
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(tenant, sql));
        Assert.Equal(state, ex.SqlState);
    }

    private async Task<int> Run(Guid? tenant, string sql)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> Count(Guid? tenant, string table)
    {
        await using var ctx = _db.CreateContext(tenant);
        await ctx.Database.OpenConnectionAsync();
        await using var cmd = ctx.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = $"SELECT count(*) FROM {table}";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        return await cmd.ExecuteScalarAsync();
    }

    [SkippableFact]
    public async Task force_rls_and_tenant_isolation_policies_exist_on_the_four_new_tables()
    {
        SkipIfNoDb();
        foreach (var table in Tables)
        {
            Assert.Equal(1L, await Scalar($"SELECT count(*) FROM pg_class WHERE oid = to_regclass('public.{table}') AND relrowsecurity AND relforcerowsecurity"));
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation'
                AND qual IS NOT NULL AND with_check IS NOT NULL"));
            Assert.Equal(1L, await Scalar($@"SELECT count(*) FROM information_schema.columns WHERE table_name = '{table}' AND column_name = 'tenant_id' AND is_nullable = 'NO'"));
        }
    }

    [SkippableFact]
    public async Task entities_round_trip_through_ef_and_chat_data_is_stored_only_encrypted_with_hash()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(s.TenantId);

        var link = await ctx.StaffTelegramLinks.SingleAsync(x => x.Id == s.LinkId);
        Assert.Equal("active", link.Status);
        Assert.Null(link.TokenHash);
        Assert.Equal("enc", link.ChatIdEncrypted);
        Assert.Equal(32, link.ChatIdHash!.Length);
        Assert.Equal("inv-tg-v1", link.ConsentVersion);
        Assert.Equal(T0, link.ActivatedAt);
        Assert.Null(link.RevokedAt);
        Assert.Null(link.RevokeReason);

        var n = await ctx.StaffNotifications.SingleAsync(x => x.Id == s.NotificationId);
        Assert.Equal("pending", n.Status);
        Assert.Equal((short)0, n.Attempts);
        Assert.Equal("Склад · A", n.Body);
        Assert.Null(n.SentAt);

        var na = await ctx.StaffNotificationAlerts.SingleAsync(x => x.NotificationId == s.NotificationId);
        Assert.Equal(s.AlertId, na.AlertId);
        Assert.Equal(s.LinkId, na.LinkId);
        var ev = await ctx.StaffTelegramLinkEvents.SingleAsync(x => x.LinkId == s.LinkId);
        Assert.Equal("activated", ev.Event);
        Assert.Equal("bot", ev.Actor);
        Assert.Null(ev.Reason);

        // шифрований chat_id у БД - рівно те, що записано (text), а не відкритий ідентифікатор
        Assert.Equal("text", await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name = 'beauty_staff_telegram_links' AND column_name = 'chat_id_encrypted'"));
        Assert.Equal("bytea", await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name = 'beauty_staff_telegram_links' AND column_name = 'chat_id_hash'"));
    }

    [SkippableFact]
    public async Task tenant_cannot_see_or_change_other_tenants_rows_and_without_context_nothing_is_visible()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        foreach (var table in Tables)
        {
            Assert.Equal(1L, await Count(a.TenantId, table));
            Assert.Equal(1L, await Count(b.TenantId, table));
            Assert.Equal(0L, await Count(null, table)); // fail-closed
        }
        Assert.Equal(0, await Run(b.TenantId, $"UPDATE beauty_staff_telegram_links SET updated_at = now() WHERE id = '{a.LinkId}'"));
        Assert.Equal(0, await Run(b.TenantId, $"DELETE FROM beauty_staff_notifications WHERE id = '{a.NotificationId}'"));
        Assert.Equal(1L, await Count(a.TenantId, "beauty_staff_notifications"));

        // WITH CHECK: tenant B не може записати рядок із tenant_id A (RLS спрацьовує раніше за FK)
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_notifications (tenant_id, link_id, kind, body, dedupe_key)
            VALUES ('{a.TenantId}', '{a.LinkId}', 'alert', 'x', 'k1')", "42501");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_telegram_link_events (tenant_id, link_id, user_id, event, actor)
            VALUES ('{a.TenantId}', '{a.LinkId}', '{a.UserId}', 'issued', 'user')", "42501");
        await Rejects(null, $@"INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id)
            VALUES ('{a.TenantId}', '{a.NotificationId}', '{a.AlertId}', '{a.LinkId}')", "42501");

        // композитні FK: свій tenant_id, але чужі батьківські рядки
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_notifications (tenant_id, link_id, kind, body, dedupe_key)
            VALUES ('{b.TenantId}', '{a.LinkId}', 'alert', 'x', 'k2')", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_telegram_links (tenant_id, user_id, channel_id, status, consent_version, consent_at, token_hash, token_expires_at)
            VALUES ('{b.TenantId}', '{a.UserId}', '{b.ChannelId}', 'issued', 'inv-tg-v1', now(), decode(repeat('ab', 32), 'hex'), now())", "23503");
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_telegram_links (tenant_id, user_id, channel_id, status, consent_version, consent_at, revoked_at, revoke_reason)
            VALUES ('{b.TenantId}', '{b.UserId}', '{a.ChannelId}', 'revoked', 'inv-tg-v1', now(), now(), 'expired')", "23503"); // revoked: unique user_open спрацював би раніше за FK
        await Rejects(b.TenantId, $@"INSERT INTO beauty_staff_notification_alerts (tenant_id, notification_id, alert_id, link_id)
            VALUES ('{b.TenantId}', '{b.NotificationId}', '{a.AlertId}', '{b.LinkId}')", "23503");
    }

    [SkippableFact]
    public async Task link_events_are_append_only_even_for_a_role_with_dml_rights_and_rows_with_history_are_restricted()
    {
        SkipIfNoDb();
        var s = await SeedTenantAsync();
        foreach (var sql in new[]
                 {
                     $"UPDATE beauty_staff_telegram_link_events SET reason = NULL WHERE link_id = '{s.LinkId}'",
                     $"DELETE FROM beauty_staff_telegram_link_events WHERE link_id = '{s.LinkId}'",
                     "TRUNCATE beauty_staff_telegram_link_events",
                 })
        {
            var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Run(s.TenantId, sql));
            Assert.Equal("42501", ex.SqlState);
        }
        Assert.Equal(1L, await Count(s.TenantId, "beauty_staff_telegram_link_events"));
        // прив'язка з подіями/чергою не видаляється (RESTRICT), а канал і користувач із прив'язкою - теж
        await Rejects(s.TenantId, $"DELETE FROM beauty_staff_telegram_links WHERE id = '{s.LinkId}'", "23503");
        await Rejects(s.TenantId, $"DELETE FROM beauty_channels WHERE id = '{s.ChannelId}'", "23503");
        await Rejects(s.TenantId, $"DELETE FROM users WHERE id = '{s.UserId}'", "23503");
        // видалення сповіщення каскадом прибирає зв'язок алертів (30-денне прибирання воркера)
        Assert.Equal(1, await Run(s.TenantId, $"DELETE FROM beauty_staff_notifications WHERE id = '{s.NotificationId}'"));
        Assert.Equal(0L, await Count(s.TenantId, "beauty_staff_notification_alerts"));
    }
}
