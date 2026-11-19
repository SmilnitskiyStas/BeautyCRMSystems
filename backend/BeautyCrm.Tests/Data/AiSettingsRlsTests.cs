using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>TASK-713 (§20.4): RLS, FORCE, композитні FK, CHECK, UNIQUE і дефолти таблиць налаштувань ШІ.</summary>
public sealed class AiSettingsRlsTests : IClassFixture<PostgresRlsFixture>
{
    private readonly PostgresRlsFixture _db;

    public AiSettingsRlsTests(PostgresRlsFixture db) => _db = db;

    private void SkipIfNoDb() => Skip.If(_db.SkipReason is not null, _db.SkipReason);

    private sealed record Seed(Guid TenantId, Guid UserId);

    private async Task<Seed> SeedTenantAsync()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        var slug = "ai-" + tenantId.ToString("N")[..20];
        await Exec(admin, $"INSERT INTO tenants (id, name, slug) VALUES ('{tenantId}', 'T', '{slug}')");
        await Exec(admin, $@"INSERT INTO users (id, tenant_id, email, full_name, password_hash, role)
                             VALUES ('{userId}', '{tenantId}', 'u@{slug}.test', 'Owner', 'x', 'owner')");
        return new Seed(tenantId, userId);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string? SqlState(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.SqlState;
    private static string? Constraint(Exception ex) => (ex as PostgresException ?? ex.InnerException as PostgresException)?.ConstraintName;

    private static AiSettingsRow Settings(Seed s) => new() { ChatModel = "chat-m", AnalyticsModel = "analytics-m", UpdatedByUserId = s.UserId };

    private static AiProviderKey Key(Seed s, string provider = "anthropic") => new()
    {
        Provider = provider, KeyEncrypted = "v2:k1:AAAA", KeyKid = "k1", KeyLast4 = "abcd", CreatedByUserId = s.UserId, UpdatedByUserId = s.UserId,
    };

    private static AiUsageRow Usage(string role = "chat", string model = "m1", string keySource = "platform", int day = 1) => new()
    {
        PeriodStart = new DateOnly(2031, 3, day), Role = role, Provider = "anthropic", Model = model, KeySource = keySource,
    };

    private static AiSettingsAuditRow Audit(Seed s, string action = "settings_updated") => new()
    {
        UserId = s.UserId, Action = action, ChangedFields = ["chat_model"],
    };

    private static async Task ExpectConstraint(Func<Task> save, string state, string? constraint = null)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(save);
        Assert.Equal(state, SqlState(ex));
        if (constraint is not null) Assert.Equal(constraint, Constraint(ex));
    }

    [SkippableFact]
    public async Task all_four_tables_are_tenant_isolated_and_invisible_without_tenant_context()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        Guid sId, kId, uId, aId;
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var s = Settings(a); var k = Key(a); var u = Usage(); var au = Audit(a);
            ctx.AddRange(s, k, u, au);
            await ctx.SaveChangesAsync();
            (sId, kId, uId, aId) = (s.Id, k.Id, u.Id, au.Id);
        }

        await using (var other = _db.CreateContext(b.TenantId))
        {
            Assert.Empty(await other.AiSettings.ToListAsync());
            Assert.Empty(await other.AiProviderKeys.ToListAsync());
            Assert.Empty(await other.AiUsage.ToListAsync());
            Assert.Empty(await other.AiSettingsAudit.ToListAsync());
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_ai_settings SET mode = 'auto' WHERE id = {sId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_ai_provider_keys SET key_last4 = 'hack' WHERE id = {kId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"UPDATE beauty_ai_usage SET request_count = 99 WHERE id = {uId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_ai_settings WHERE id = {sId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_ai_provider_keys WHERE id = {kId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_ai_usage WHERE id = {uId}"));
            Assert.Equal(0, await other.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM beauty_ai_settings_audit WHERE id = {aId}"));
        }

        await using (var own = _db.CreateContext(a.TenantId))
        {
            Assert.Equal("confirm", (await own.AiSettings.AsNoTracking().SingleAsync(x => x.Id == sId)).Mode);
            Assert.Equal("abcd", (await own.AiProviderKeys.AsNoTracking().SingleAsync(x => x.Id == kId)).KeyLast4);
            Assert.Equal(0, (await own.AiUsage.AsNoTracking().SingleAsync(x => x.Id == uId)).RequestCount);
            Assert.Single(await own.AiSettingsAudit.AsNoTracking().Where(x => x.Id == aId).ToListAsync());
        }

        await using var none = _db.CreateContext(null);
        Assert.Empty(await none.AiSettings.ToListAsync());
        Assert.Empty(await none.AiProviderKeys.ToListAsync());
        Assert.Empty(await none.AiUsage.ToListAsync());
        Assert.Empty(await none.AiSettingsAudit.ToListAsync());
    }

    [SkippableFact]
    public async Task writing_a_row_with_a_foreign_tenant_id_is_blocked_by_with_check()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        var rows = new Func<object>[]
        {
            () => new AiSettingsRow { TenantId = a.TenantId, ChatModel = "m", AnalyticsModel = "m" },
            () => new AiProviderKey { TenantId = a.TenantId, Provider = "anthropic", KeyEncrypted = "v2:k1:x", KeyKid = "k1", KeyLast4 = "abcd", CreatedByUserId = a.UserId, UpdatedByUserId = a.UserId },
            () => new AiUsageRow { TenantId = a.TenantId, PeriodStart = new DateOnly(2031, 3, 1), Role = "chat", Provider = "anthropic", Model = "m", KeySource = "own" },
            () => new AiSettingsAuditRow { TenantId = a.TenantId, Action = "key_set" },
        };
        foreach (var make in rows)
        {
            await using var ctx = _db.CreateContext(b.TenantId);
            ctx.Add(make());
            await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        }
    }

    [SkippableFact]
    public async Task composite_fks_reject_users_of_another_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();

        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new AiSettingsRow { ChatModel = "m", AnalyticsModel = "m", UpdatedByUserId = a.UserId });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(Key(a)); // CreatedBy/UpdatedBy належать tenant A, запис у tenant B
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new AiSettingsAuditRow { UserId = a.UserId, Action = "key_set" });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }
        // audit.user_id = NULL (оператор платформи) і settings.updated_by = NULL дозволені
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            ctx.Add(new AiSettingsAuditRow { Action = "platform_quota_changed" });
            ctx.Add(new AiSettingsRow { ChatModel = "m", AnalyticsModel = "m" });
            await ctx.SaveChangesAsync();
        }
        // provider key вимагає обох користувачів
        await using (var ctx = _db.CreateContext(b.TenantId))
        {
            var k = Key(b); k.CreatedByUserId = Guid.NewGuid();
            ctx.Add(k);
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.ForeignKeyViolation);
        }
    }

    [SkippableFact]
    public async Task users_referenced_by_ai_rows_cannot_be_deleted_restrict()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Key(a));
            await ctx.SaveChangesAsync();
        }
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Exec(admin, $"DELETE FROM users WHERE id = '{a.UserId}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [SkippableFact]
    public async Task settings_defaults_and_one_row_per_tenant()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new AiSettingsRow { ChatModel = "chat-m", AnalyticsModel = "analytics-m" });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var s = await ctx.AiSettings.AsNoTracking().SingleAsync();
            Assert.Equal("platform", s.KeySource);
            Assert.Equal("anthropic", s.ChatProvider);
            Assert.Equal("anthropic", s.AnalyticsProvider);
            Assert.Equal("confirm", s.Mode);
            Assert.Null(s.PromptAddendum);
            Assert.Null(s.MonthlyTokenLimit);
            Assert.Null(s.MonthlyRequestLimit);
            Assert.Equal(1024, s.MaxOutputTokens);
            Assert.Null(s.PlatformEnabled);
            Assert.Null(s.PlatformMonthlyTokenQuota);
            Assert.Null(s.PlatformMonthlyRequestQuota);
            Assert.Null(s.UpdatedByUserId);
            Assert.NotEqual(default, s.CreatedAt);
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // другий рядок того ж tenant
        {
            ctx.Add(new AiSettingsRow { ChatModel = "m", AnalyticsModel = "m" });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // інший tenant - окремий рядок
        {
            ctx.Add(new AiSettingsRow { ChatModel = "m", AnalyticsModel = "m" });
            await ctx.SaveChangesAsync();
        }
    }

    [SkippableFact]
    public async Task settings_checks_reject_invalid_values()
    {
        SkipIfNoDb();
        var cases = new (string Constraint, Action<AiSettingsRow> Mutate)[]
        {
            ("ck_beauty_ai_settings_key_source", r => r.KeySource = "both"),
            ("ck_beauty_ai_settings_mode", r => r.Mode = "yolo"),
            ("ck_beauty_ai_settings_chat_provider", r => r.ChatProvider = "Open AI"),
            ("ck_beauty_ai_settings_analytics_provider", r => r.AnalyticsProvider = "1abc"),
            ("ck_beauty_ai_settings_chat_model", r => r.ChatModel = ""),
            ("ck_beauty_ai_settings_prompt_addendum", r => r.PromptAddendum = new string('x', 4001)),
            ("ck_beauty_ai_settings_monthly_token_limit", r => r.MonthlyTokenLimit = 0),
            ("ck_beauty_ai_settings_monthly_token_limit", r => r.MonthlyTokenLimit = 100_000_000_001L),
            ("ck_beauty_ai_settings_monthly_request_limit", r => r.MonthlyRequestLimit = 10_000_001),
            ("ck_beauty_ai_settings_max_output_tokens", r => r.MaxOutputTokens = 63),
            ("ck_beauty_ai_settings_max_output_tokens", r => r.MaxOutputTokens = 8193),
            ("ck_beauty_ai_settings_platform_quotas", r => r.PlatformMonthlyTokenQuota = -1),
            ("ck_beauty_ai_settings_platform_quotas", r => r.PlatformMonthlyRequestQuota = -1),
        };
        foreach (var (constraint, mutate) in cases)
        {
            var t = await SeedTenantAsync();
            await using var ctx = _db.CreateContext(t.TenantId);
            var row = new AiSettingsRow { ChatModel = "m", AnalyticsModel = "m" };
            mutate(row);
            ctx.Add(row);
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, constraint);
        }

        // граничні допустимі значення
        var ok = await SeedTenantAsync();
        await using var okCtx = _db.CreateContext(ok.TenantId);
        okCtx.Add(new AiSettingsRow
        {
            ChatModel = new string('m', 100), AnalyticsModel = "m", Mode = "auto", KeySource = "own", PromptAddendum = new string('я', 4000),
            MonthlyTokenLimit = 100_000_000_000L, MonthlyRequestLimit = 10_000_000, MaxOutputTokens = 8192,
            PlatformEnabled = true, PlatformMonthlyTokenQuota = 0, PlatformMonthlyRequestQuota = 0,
        });
        await okCtx.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task provider_keys_are_unique_per_tenant_and_provider_and_store_only_ciphertext()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        var b = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Key(a, "anthropic"));
            ctx.Add(Key(a, "openai")); // інший провайдер - ок
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Key(a, "anthropic"));
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
        }
        await using (var ctx = _db.CreateContext(b.TenantId)) // той самий провайдер в іншому tenant
        {
            ctx.Add(Key(b, "anthropic"));
            await ctx.SaveChangesAsync();
        }

        var cases = new (string Constraint, Action<AiProviderKey> Mutate)[]
        {
            ("ck_beauty_ai_provider_keys_key_encrypted", k => k.KeyEncrypted = "sk-ant-plain-text-key"),
            ("ck_beauty_ai_provider_keys_key_encrypted", k => k.KeyEncrypted = "v1:k1:AAAA"),
            ("ck_beauty_ai_provider_keys_key_kid", k => k.KeyKid = "BAD KID"),
            ("ck_beauty_ai_provider_keys_key_kid", k => k.KeyKid = ""),
            ("ck_beauty_ai_provider_keys_provider", k => k.Provider = "Anthropic"),
            ("ck_beauty_ai_provider_keys_provider", k => k.Provider = "x"),
        };
        foreach (var (constraint, mutate) in cases)
        {
            var t = await SeedTenantAsync();
            await using var ctx = _db.CreateContext(t.TenantId);
            var key = Key(t);
            mutate(key);
            ctx.Add(key);
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, constraint);
        }

        // key_last4 обмежено 4 символами
        var t4 = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(t4.TenantId))
        {
            var key = Key(t4); key.KeyLast4 = "abcde";
            ctx.Add(key);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, SqlState(ex));
        }

        // у таблиці немає колонки для відкритого ключа / майстер-ключа
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'beauty_ai_provider_keys' ORDER BY 1", admin);
        var cols = new List<string>();
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) cols.Add(rd.GetString(0));
        Assert.Equal(
            new[] { "created_at", "created_by_user_id", "id", "key_encrypted", "key_kid", "key_last4", "provider", "tenant_id", "updated_at", "updated_by_user_id" },
            cols);
    }

    [SkippableFact]
    public async Task usage_defaults_unique_key_includes_role_and_checks_reject_invalid_values()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(Usage());
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var u = await ctx.AiUsage.AsNoTracking().SingleAsync();
            Assert.Equal((0, 0, 0L, 0L), (u.RequestCount, u.ErrorCount, u.InputTokens, u.OutputTokens));
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // та сама ключова п'ятірка
        {
            ctx.Add(Usage());
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.UniqueViolation);
        }
        await using (var ctx = _db.CreateContext(a.TenantId)) // інша роль / модель / джерело / місяць - окремі рядки
        {
            ctx.Add(Usage(role: "analytics"));
            ctx.Add(Usage(model: "m2"));
            ctx.Add(Usage(keySource: "own"));
            await ctx.SaveChangesAsync();
        }

        var cases = new (string Constraint, Action<AiUsageRow> Mutate)[]
        {
            ("ck_beauty_ai_usage_period_start", r => r.PeriodStart = new DateOnly(2031, 4, 2)),
            ("ck_beauty_ai_usage_role", r => r.Role = "billing"),
            ("ck_beauty_ai_usage_key_source", r => r.KeySource = "none"),
            ("ck_beauty_ai_usage_provider", r => r.Provider = "A"),
            ("ck_beauty_ai_usage_counters", r => r.RequestCount = -1),
            ("ck_beauty_ai_usage_counters", r => r.ErrorCount = -1),
            ("ck_beauty_ai_usage_counters", r => r.InputTokens = -1),
            ("ck_beauty_ai_usage_counters", r => r.OutputTokens = -1),
        };
        foreach (var (constraint, mutate) in cases)
        {
            var t = await SeedTenantAsync();
            await using var ctx = _db.CreateContext(t.TenantId);
            var row = Usage();
            mutate(row);
            ctx.Add(row);
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, constraint);
        }
    }

    [SkippableFact]
    public async Task usage_upsert_accumulates_counters_atomically_for_the_same_key()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using var ctx = _db.CreateContext(a.TenantId);
        for (var i = 0; i < 3; i++)
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO beauty_ai_usage (tenant_id, period_start, role, provider, model, key_source, request_count, input_tokens, output_tokens)
                VALUES ({a.TenantId}, {new DateOnly(2031, 3, 1)}, 'chat', 'anthropic', 'm1', 'platform', 1, 10, 5)
                ON CONFLICT (tenant_id, period_start, role, provider, model, key_source)
                DO UPDATE SET request_count = beauty_ai_usage.request_count + excluded.request_count,
                              input_tokens = beauty_ai_usage.input_tokens + excluded.input_tokens,
                              output_tokens = beauty_ai_usage.output_tokens + excluded.output_tokens");
        }
        var u = await ctx.AiUsage.AsNoTracking().SingleAsync();
        Assert.Equal((3, 30L, 15L), (u.RequestCount, u.InputTokens, u.OutputTokens));
    }

    [SkippableFact]
    public async Task audit_defaults_and_checks()
    {
        SkipIfNoDb();
        var a = await SeedTenantAsync();
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new AiSettingsAuditRow { Action = "key_deleted", Provider = "anthropic" });
            foreach (var action in new[] { "settings_updated", "key_source_changed", "key_set", "key_replaced", "key_tested", "prompt_reset", "platform_quota_changed" })
                ctx.Add(Audit(a, action));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            var row = await ctx.AiSettingsAudit.AsNoTracking().SingleAsync(x => x.Action == "key_deleted");
            Assert.Empty(row.ChangedFields); // DEFAULT '{}'
            Assert.Null(row.UserId);
            Assert.NotEqual(default, row.CreatedAt);
            Assert.Equal(8, await ctx.AiSettingsAudit.CountAsync());
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new AiSettingsAuditRow { Action = "key_leaked" });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_beauty_ai_settings_audit_action");
        }
        await using (var ctx = _db.CreateContext(a.TenantId))
        {
            ctx.Add(new AiSettingsAuditRow { Action = "key_set", Provider = "Bad Provider" });
            await ExpectConstraint(() => ctx.SaveChangesAsync(), PostgresErrorCodes.CheckViolation, "ck_beauty_ai_settings_audit_provider");
        }

        // у журналі й обліку немає колонок для значень ключа, тексту промпту чи вмісту повідомлень
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var (table, expected) in new[]
        {
            ("beauty_ai_settings_audit", new[] { "action", "changed_fields", "created_at", "id", "provider", "tenant_id", "user_id" }),
            ("beauty_ai_usage", new[] { "created_at", "error_count", "id", "input_tokens", "key_source", "model", "output_tokens", "period_start", "provider", "request_count", "role", "tenant_id", "updated_at" }),
        })
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT column_name FROM information_schema.columns WHERE table_name = '{table}' ORDER BY 1", admin);
            var cols = new List<string>();
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync()) cols.Add(rd.GetString(0));
            Assert.Equal(expected, cols);
        }
    }

    [SkippableFact]
    public async Task all_four_tables_have_force_row_level_security_and_tenant_isolation_policy()
    {
        SkipIfNoDb();
        await using var admin = new NpgsqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        foreach (var table in new[] { "beauty_ai_settings", "beauty_ai_provider_keys", "beauty_ai_usage", "beauty_ai_settings_audit" })
        {
            await using var cmd = new NpgsqlCommand(
                $"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = '{table}'::regclass", admin);
            Assert.True((bool)(await cmd.ExecuteScalarAsync())!, table);
            await using var pol = new NpgsqlCommand(
                $"SELECT count(*) FROM pg_policies WHERE tablename = '{table}' AND policyname = 'tenant_isolation' AND with_check IS NOT NULL", admin);
            Assert.Equal(1L, (long)(await pol.ExecuteScalarAsync())!);
        }
    }
}
