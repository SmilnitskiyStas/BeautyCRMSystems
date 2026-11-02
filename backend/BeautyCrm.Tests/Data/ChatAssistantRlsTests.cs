using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-724 (§21.4): RLS, FORCE, композитні FK, CHECK, UNIQUE та ідемпотентні вставки схеми асистента в чаті.</summary>
public sealed class ChatAssistantRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public ChatAssistantRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(
        Guid TenantId, Guid LocationId, Guid SpecialistId, Guid ServiceId, Guid ClientId, Guid PromotionId,
        Guid ChannelId, Guid ConversationId, Guid MessageId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        await using (var admin = new NpgsqlConnection(_db.AdminConnectionString))
        {
            await admin.OpenAsync();
            var slug = "chat-" + tenantId.ToString("N")[..20];
            await using var cmd = new NpgsqlCommand($"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        await using var ctx = _db.CreateContext(tenantId);
        var loc = new Location { Name = "L", Timezone = "Europe/Kyiv" };
        var spec = new Specialist { FullName = "S" };
        var svc = new Service { Name = "Svc", DurationMinutes = 60 };
        var client = new Client { FullName = "Клієнт Telegram" };
        var promo = new Promotion { Name = "P", DiscountType = DiscountType.Percent, DiscountValue = 10 };
        var ch = new Channel { Type = ChannelType.Telegram, Name = "bot" };
        ctx.AddRange(loc, spec, svc, client, promo, ch);
        await ctx.SaveChangesAsync();
        var conv = new Conversation { ChannelId = ch.Id, ExternalChatId = "100" };
        ctx.Add(conv);
        await ctx.SaveChangesAsync();
        var msg = new Message
        {
            ConversationId = conv.Id, Direction = MessageDirection.Outbound, SenderType = MessageSenderType.System, Body = "summary", Status = "sending",
        };
        ctx.Add(msg);
        await ctx.SaveChangesAsync();
        return new Seed(tenantId, loc.Id, spec.Id, svc.Id, client.Id, promo.Id, ch.Id, conv.Id, msg.Id);
    }

    private static string Hash(char c = 'a') => new(c, 64);

    private static ChatBookingProposal Proposal(Seed s, string status = "prepared", string? hash = null) => new()
    {
        ConversationId = s.ConversationId, LocationId = s.LocationId, SpecialistId = s.SpecialistId, ServiceId = s.ServiceId,
        StartsAt = DateTimeOffset.UtcNow.AddDays(2), DurationMinutes = 60, PriceOriginal = 600m, PriceFinal = 500m,
        Status = status, ConfirmationTokenHash = hash ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    private static async Task ExpectConstraint(Func<Task> save, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(save);
        Assert.Equal(state, SqlState(ex));
        if (constraint is not null) Assert.Equal(constraint, Constraint(ex));
    }

    [SkippableFact]
    public async Task new_tables_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        Guid pId;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var p = Proposal(a);
            ctx.Add(p);
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId, LastUpdateId = 42 });
            await ctx.SaveChangesAsync();
            pId = p.Id;
        }

        await using (var other = _db.CreateContext(b.TenantId))
        {
            Assert.Empty(await other.ChatBookingProposals.ToListAsync());
            Assert.Empty(await other.ChannelPollStates.ToListAsync());
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_chat_booking_proposals SET status = 'confirmed' WHERE id = {pId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_chat_booking_proposals WHERE id = {pId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_channel_poll_state SET last_update_id = 1 WHERE channel_id = {a.ChannelId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_channel_poll_state WHERE channel_id = {a.ChannelId}"));
        }

        await using (var own = _db.CreateContext(a.TenantId))
        {
            Assert.Equal("prepared", (await own.ChatBookingProposals.AsNoTracking().SingleAsync(x => x.Id == pId)).Status);
            Assert.Equal(42, (await own.ChannelPollStates.AsNoTracking().SingleAsync()).LastUpdateId);
        }

        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.ChatBookingProposals.ToListAsync());
        Assert.Empty(await none.ChannelPollStates.ToListAsync());
    }

    [SkippableFact]
    public async Task writing_a_row_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            var p = Proposal(a);
            p.TenantId = a.TenantId;
            ctx.Add(p);
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new ChannelPollState { TenantId = a.TenantId, ChannelId = a.ChannelId });
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task composite_fks_reject_references_to_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        var mutations = new (string Name, Action<ChatBookingProposal> Mutate)[]
        {
            ("conversation", p => p.ConversationId = a.ConversationId),
            ("client", p => p.ClientId = a.ClientId),
            ("location", p => p.LocationId = a.LocationId),
            ("specialist", p => p.SpecialistId = a.SpecialistId),
            ("service", p => p.ServiceId = a.ServiceId),
            ("promotion", p => p.PromotionId = a.PromotionId),
            ("summary_message", p => p.SummaryMessageId = a.MessageId),
            ("appointment", p => p.AppointmentId = Guid.NewGuid()),
        };
        foreach (var (name, mutate) in mutations)
        {
            await using var ctx = _db.CreateContext(b.TenantId);
            var p = Proposal(b);
            mutate(p);
            ctx.Add(p);
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
            Assert.True(name.Length > 0);
        }

        // poll state: канал іншого tenant
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }

        // своя повна пропозиція з усіма необов'язковими посиланнями проходить
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var p = Proposal(a, "shown");
            p.ClientId = a.ClientId; p.PromotionId = a.PromotionId; p.SummaryMessageId = a.MessageId;
            ctx.Add(p);
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task referenced_rows_cannot_be_deleted_restrict_and_poll_state_cascades_with_channel()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Proposal(a));
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId });
            await ctx.SaveChangesAsync();
        }

        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_conversations", "beauty_locations", "beauty_specialists", "beauty_services" })
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM {table} WHERE tenant_id = '{a.TenantId}'", admin);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }

        // poll state йде разом з каналом (CASCADE); розмова RESTRICT тримає канал, тож спершу прибираємо дані розмови
        await using (var cmd = new NpgsqlCommand($@"
            DELETE FROM beauty_chat_booking_proposals WHERE tenant_id = '{a.TenantId}';
            DELETE FROM beauty_conversations WHERE tenant_id = '{a.TenantId}';
            DELETE FROM beauty_channels WHERE id = '{a.ChannelId}';", admin))
            await cmd.ExecuteNonQueryAsync();
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM beauty_channel_poll_state WHERE tenant_id = '{a.TenantId}'", admin);
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task proposal_checks_reject_invalid_values()
    {
        SkipIfNoDb();
        var cases = new (string Constraint, Action<ChatBookingProposal> Mutate)[]
        {
            ("ck_beauty_chat_booking_proposals_status", p => p.Status = "done"),
            ("ck_beauty_chat_booking_proposals_duration", p => p.DurationMinutes = 0),
            ("ck_beauty_chat_booking_proposals_prices", p => p.PriceOriginal = -1m),
            ("ck_beauty_chat_booking_proposals_prices", p => p.PriceFinal = -0.01m),
            ("ck_beauty_chat_booking_proposals_token_hash", p => p.ConfirmationTokenHash = new string('A', 64)),
            ("ck_beauty_chat_booking_proposals_token_hash", p => p.ConfirmationTokenHash = new string('a', 63)),
            ("ck_beauty_chat_booking_proposals_failure_code", p => p.FailureCode = ""),
        };
        foreach (var (constraint, mutate) in cases)
        {
            var s = await SeedTenantAsync();
            await using var ctx = _db.CreateContext(s.TenantId);
            var p = Proposal(s);
            mutate(p);
            ctx.Add(p);
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => ctx.SaveChangesAsync());
            // 63-символьний хеш char(64) доповнюється пробілом і падає на regex; решта - CHECK
            Assert.Equal(PostgresErrorCodes.CheckViolation, SqlState(ex));
            Assert.Equal(constraint, Constraint(ex));
        }

        // допустимі межі та всі статуси
        var ok = await SeedTenantAsync();
        foreach (var status in new[] { "prepared", "confirmed", "rejected", "expired", "superseded", "failed" })
        {
            await using var ctx = _db.CreateContext(ok.TenantId);
            var p = Proposal(ok, status);
            p.PriceOriginal = 0m; p.PriceFinal = 99999999.99m; p.FailureCode = new string('x', 64);
            ctx.Add(p);
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task only_one_active_proposal_per_conversation_and_token_hash_is_unique_per_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        Guid firstId;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var p = Proposal(a, "prepared", Hash('a'));
            ctx.Add(p);
            await ctx.SaveChangesAsync();
            firstId = p.Id;
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // друга активна (shown) у тій самій розмові
        {
            ctx.Add(Proposal(a, "shown"));
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation, "ux_beauty_chat_booking_proposals_active");
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // той самий token hash у tenant
        {
            ctx.Add(Proposal(a, "superseded", Hash('a')));
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation, "ux_beauty_chat_booking_proposals_token");
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // неактивні статуси можуть співіснувати
        {
            ctx.Add(Proposal(a, "superseded"));
            ctx.Add(Proposal(a, "rejected"));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // після виходу зі стану prepared/shown нова активна можлива (заміна)
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_chat_booking_proposals SET status = 'superseded' WHERE id = {firstId}");
            ctx.Add(Proposal(a, "prepared"));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // той самий hash в іншого tenant дозволений
        {
            ctx.Add(Proposal(b, "prepared", Hash('a')));
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task proposal_defaults()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var p = Proposal(a);
            p.Status = null!; // дефолт БД
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO beauty_chat_booking_proposals
                    (tenant_id, conversation_id, location_id, specialist_id, service_id, starts_at, duration_minutes,
                     price_original, price_final, confirmation_token_hash, expires_at)
                VALUES ({a.TenantId}, {a.ConversationId}, {a.LocationId}, {a.SpecialistId}, {a.ServiceId}, now(), 30, 1, 1, {Hash('b')}, now())");
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var p = await ctx.ChatBookingProposals.AsNoTracking().SingleAsync();
            Assert.Equal("prepared", p.Status);
            Assert.Null(p.ClientId);
            Assert.Null(p.SummaryMessageId);
            Assert.Null(p.AppointmentId);
            Assert.Null(p.DecidedAt);
            Assert.NotEqual(Guid.Empty, p.Id);
            Assert.NotEqual(default, p.CreatedAt);
            Assert.Equal(64, p.ConfirmationTokenHash.Length);
        }
    }

    [SkippableFact]
    public async Task poll_state_is_one_row_per_channel_and_upsert_is_idempotent()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var s = await ctx.ChannelPollStates.AsNoTracking().SingleAsync();
            Assert.Equal(0, s.LastUpdateId);
            Assert.Null(s.LeaseUntil);
            Assert.Null(s.LeaseOwner);
            Assert.NotEqual(default, s.UpdatedAt);
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // другий рядок того ж каналу
        {
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // повторний upsert offset: ON CONFLICT, лише вперед
        {
            for (var i = 0; i < 2; i++)
                await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO beauty_channel_poll_state (tenant_id, channel_id, last_update_id)
                    VALUES ({a.TenantId}, {a.ChannelId}, 500)
                    ON CONFLICT (tenant_id, channel_id) DO UPDATE
                    SET last_update_id = GREATEST(beauty_channel_poll_state.last_update_id, EXCLUDED.last_update_id), updated_at = now()");
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO beauty_channel_poll_state (tenant_id, channel_id, last_update_id)
                VALUES ({a.TenantId}, {a.ChannelId}, 100)
                ON CONFLICT (tenant_id, channel_id) DO UPDATE
                SET last_update_id = GREATEST(beauty_channel_poll_state.last_update_id, EXCLUDED.last_update_id)");
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            Assert.Equal(500, (await ctx.ChannelPollStates.AsNoTracking().SingleAsync()).LastUpdateId);
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // CHECK last_update_id >= 0
        {
            await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE beauty_channel_poll_state SET last_update_id = -1 WHERE channel_id = {a.ChannelId}"));
        }
    }

    [SkippableFact]
    public async Task poll_state_lease_is_a_single_winner_conditional_update()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new ChannelPollState { ChannelId = a.ChannelId });
            await ctx.SaveChangesAsync();
        }
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        await using var c = _db.CreateContext(a.TenantId);
        var won1 = await c.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_channel_poll_state SET lease_owner = {owner1}, lease_until = now() + interval '60 seconds'
            WHERE channel_id = {a.ChannelId} AND (lease_until IS NULL OR lease_until < now())");
        var won2 = await c.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE beauty_channel_poll_state SET lease_owner = {owner2}, lease_until = now() + interval '60 seconds'
            WHERE channel_id = {a.ChannelId} AND (lease_until IS NULL OR lease_until < now())");
        Assert.Equal(1, won1);
        Assert.Equal(0, won2);
    }

    [SkippableFact]
    public async Task conversation_ai_columns_defaults_check_and_single_winner_lease()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var c = await ctx.Conversations.AsNoTracking().SingleAsync(x => x.Id == a.ConversationId);
            Assert.Equal("active", c.AiState);
            Assert.Null(c.HandoffReason);
            Assert.Null(c.AiPausedUntil);
            Assert.Null(c.AiLeaseUntil);
            Assert.Null(c.AiLeaseOwner);
        }

        await using (var ctx = _db.CreateContext(a.TenantId)) // CHECK ai_state
        {
            await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE beauty_conversations SET ai_state = 'paused' WHERE id = {a.ConversationId}"));
        }

        var w1 = Guid.NewGuid();
        var w2 = Guid.NewGuid();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var r1 = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE beauty_conversations SET ai_lease_owner = {w1}, ai_lease_until = now() + interval '120 seconds'
                WHERE id = {a.ConversationId} AND (ai_lease_until IS NULL OR ai_lease_until < now())");
            var r2 = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE beauty_conversations SET ai_lease_owner = {w2}, ai_lease_until = now() + interval '120 seconds'
                WHERE id = {a.ConversationId} AND (ai_lease_until IS NULL OR ai_lease_until < now())");
            Assert.Equal(1, r1);
            Assert.Equal(0, r2);
            var ok = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE beauty_conversations SET ai_state = 'handoff', handoff_reason = 'complaint', ai_paused_until = now() + interval '12 hours'
                WHERE id = {a.ConversationId}");
            Assert.Equal(1, ok);
        }
    }

    [SkippableFact]
    public async Task message_kind_default_check_and_inbound_dedup_is_idempotent()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var m = await ctx.Messages.AsNoTracking().SingleAsync(x => x.Id == a.MessageId);
            Assert.Equal("text", m.Kind);
        }

        foreach (var kind in new[] { "text", "command", "unsupported" })
        {
            await using var ctx = _db.CreateContext(a.TenantId);
            ctx.Add(new Message
            {
                ConversationId = a.ConversationId, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client,
                Body = kind == "unsupported" ? "[sticker]" : "hi", Kind = kind, ExternalMessageId = kind,
            });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new Message
            {
                ConversationId = a.ConversationId, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client, Body = "x", Kind = "voice",
            });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_beauty_messages_kind");
        }

        // нові статуси вхідних (processed|skipped) допустимі: CHECK на status відсутній
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            foreach (var st in new[] { "processed", "skipped" })
                ctx.Add(new Message
                {
                    ConversationId = a.ConversationId, Direction = MessageDirection.Inbound, SenderType = MessageSenderType.Client,
                    Body = st, Status = st,
                });
            await ctx.SaveChangesAsync();
        }

        // ідемпотентна вставка вхідного за ключем {channel}:{channelId}:{chatId}:{messageId}
        var key = $"telegram:{Guid.NewGuid():N}:100:7";
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var rows = 0;
            for (var i = 0; i < 2; i++)
                rows += await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, kind, idempotency_key)
                    VALUES ({a.TenantId}, {a.ConversationId}, 'inbound', 'client', 'hello', 'received', 'text', {key})
                    ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING");
            Assert.Equal(1, rows);
        }
        // той самий ключ в іншому tenant не конфліктує
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            var rows = await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, kind, idempotency_key)
                VALUES ({b.TenantId}, {b.ConversationId}, 'inbound', 'client', 'hello', 'received', 'text', {key})
                ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING");
            Assert.Equal(1, rows);
        }
        // відповідь ШІ: ai-reply:{conversationId}:{lastInboundMessageId} - один раз на ключ
        var replyKey = $"ai-reply:{a.ConversationId}:{Guid.NewGuid()}";
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var rows = 0;
            for (var i = 0; i < 2; i++)
                rows += await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO beauty_messages (tenant_id, conversation_id, direction, sender_type, body, status, idempotency_key)
                    VALUES ({a.TenantId}, {a.ConversationId}, 'outbound', 'ai', 'reply', 'sending', {replyKey})
                    ON CONFLICT (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL DO NOTHING");
            Assert.Equal(1, rows);
        }
    }

    [SkippableFact]
    public async Task inbound_received_partial_index_exists_and_does_not_replace_the_regular_one()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT indexname, indexdef FROM pg_indexes
            WHERE schemaname = 'public' AND tablename = 'beauty_messages'
              AND indexname IN ('ix_beauty_messages_inbound_received', 'ix_beauty_messages_tenant_id_conversation_id_sent_at')", admin);
        var defs = new Dictionary<string, string>();
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) defs[r.GetString(0)] = r.GetString(1);
        Assert.Equal(2, defs.Count);
        Assert.Contains("WHERE", defs["ix_beauty_messages_inbound_received"]);
        Assert.DoesNotContain("WHERE", defs["ix_beauty_messages_tenant_id_conversation_id_sent_at"]);
    }

    [SkippableFact]
    public async Task force_rls_and_policy_are_active_and_list_tenants_function_is_absent()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_chat_booking_proposals", "beauty_channel_poll_state", "beauty_conversations", "beauty_messages" })
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
        await using var fn = new NpgsqlCommand("SELECT count(*) FROM pg_proc WHERE proname = 'beauty_list_tenants'", admin);
        Assert.Equal(0L, await fn.ExecuteScalarAsync());
    }
}
