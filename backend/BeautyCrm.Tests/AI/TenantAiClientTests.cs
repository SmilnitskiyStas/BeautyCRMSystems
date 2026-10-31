using System.Text.Json;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.AI.Beauty.Providers;
using BeautyCrm.Infrastructure.AI.Secrets;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Tests.AI;

/// <summary>TASK-715 (§20.3, §20.7, §20.11 п. 6, 6а, 9, 10, 11a/g): стан ролей, вибір ключа, ліміти, облік, помилки провайдера.</summary>
public class TenantAiClientTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private const string UserText = "Привіт, хочу записатися на манікюр-ТАЄМНИЙ-ТЕКСТ";

    private static AiRequest Req(string role = AiRoles.Chat) =>
        new("system-prompt-ТАЄМНИЙ-ПРОМПТ", [AiMessage.User(UserText)], AiToolCatalog.All, role);

    // ---- дефолти ----

    [Fact]
    public async Task defaults_without_row_are_safe_platform_disabled_confirm_haiku_sonnet()
    {
        var w = new AiWorld();
        var sc = w.Scope(A);
        var s = await sc.Runtime.GetEffectiveAsync(A, default);

        Assert.True(s.IsDefault);
        Assert.Equal("platform", s.KeySource);
        Assert.Equal(AiMode.Confirm, s.Mode);
        Assert.Equal(new AiRoleSelection("anthropic", "claude-haiku-5-5"), s.Chat);
        Assert.Equal(new AiRoleSelection("anthropic", "claude-sonnet-5-5"), s.Analytics);
        Assert.Null(s.PromptAddendum);
        var st = await sc.Runtime.GetStatusAsync(A, default);
        Assert.Equal(AiRoleStatus.No("platform_disabled"), st.Chat);
        Assert.Equal(AiRoleStatus.No("platform_disabled"), st.Analytics);
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => sc.Client.CompleteAsync(Req(), default));
        Assert.Equal("platform_disabled", ex.Reason);
        Assert.Empty(w.Anthropic.Calls);
    }

    [Fact]
    public async Task mode_comes_from_tenant_row_and_unknown_value_falls_back_to_confirm()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.Mode = "auto");
        w.SetSettings(B, r => r.Mode = "suggest");
        var other = Guid.NewGuid();
        w.SetSettings(other, r => r.Mode = "hack");
        Assert.Equal(AiMode.Auto, await w.Scope(A).Runtime.GetModeAsync(A, default));
        Assert.Equal(AiMode.Suggest, await w.Scope(B).Runtime.GetModeAsync(B, default));
        Assert.Equal(AiMode.Confirm, await w.Scope(other).Runtime.GetModeAsync(other, default));
        var fresh = Guid.NewGuid(); // без рядка
        Assert.Equal(AiMode.Confirm, await w.Scope(fresh).Runtime.GetModeAsync(fresh, default));
    }

    [Fact]
    public void auto_mode_requires_acknowledgement_only_when_switching_to_it()
    {
        Assert.True(AiModes.AutoRequiresAcknowledgement(AiMode.Confirm, AiMode.Auto, false));
        Assert.False(AiModes.AutoRequiresAcknowledgement(AiMode.Confirm, AiMode.Auto, true));
        Assert.False(AiModes.AutoRequiresAcknowledgement(AiMode.Auto, AiMode.Auto, false));
        Assert.False(AiModes.AutoRequiresAcknowledgement(AiMode.Confirm, AiMode.Suggest, false));
    }

    // ---- ключ: platform ----

    [Fact]
    public async Task platform_uses_platform_key_and_never_tenant_key()
    {
        var w = new AiWorld();
        var platformKey = AiWorld.NewApiKey();
        w.EnablePlatform(platformKey);
        w.SetSettings(A, r => r.KeySource = "platform");
        w.SetOwnKey(A, AiWorld.NewApiKey()); // власний ключ існує, але не використовується

        await w.Scope(A).Client.CompleteAsync(Req(), default);

        var call = Assert.Single(w.Anthropic.Calls);
        Assert.Equal(AiWorld.KeyFingerprint(platformKey), call.KeyFingerprint);
        Assert.Equal("claude-haiku-5-5", call.Model);
        Assert.Equal(AiRoles.Chat, call.Role);
    }

    [Theory]
    [InlineData(false, 1000, 1000, "platform_disabled")]
    [InlineData(true, 0, 1000, "platform_disabled")]
    [InlineData(true, 1000, 0, "platform_disabled")]
    [InlineData(true, 1000, 1000, "platform_key_missing")]
    public async Task platform_state_reasons(bool enabled, long tokens, int requests, string reason)
    {
        var w = new AiWorld();
        w.Config["Ai:Platform:EnabledByDefault"] = enabled.ToString();
        w.Config["Ai:Platform:DefaultMonthlyTokenQuota"] = tokens.ToString();
        w.Config["Ai:Platform:DefaultMonthlyRequestQuota"] = requests.ToString();
        w.Rebuild();
        var st = await w.Scope(A).Runtime.GetStatusAsync(A, default);
        Assert.Equal(AiRoleStatus.No(reason), st.Chat);
    }

    [Fact]
    public async Task platform_key_falls_back_to_ANTHROPIC_API_KEY_for_anthropic_only()
    {
        var w = new AiWorld();
        w.EnablePlatform();
        w.Config["ANTHROPIC_API_KEY"] = AiWorld.NewApiKey();
        w.Rebuild();
        Assert.True((await w.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.IsReady);
        Assert.Null(w.Platform.GetPlatformKey("openai"));
    }

    [Fact]
    public async Task per_tenant_platform_override_enables_or_disables_independent_of_default()
    {
        var w = new AiWorld();
        w.EnablePlatform(AiWorld.NewApiKey());
        w.SetSettings(A, r => { r.KeySource = "platform"; r.PlatformEnabled = false; });
        w.SetSettings(B, r => { r.KeySource = "platform"; r.PlatformEnabled = true; r.PlatformMonthlyTokenQuota = 50; r.PlatformMonthlyRequestQuota = 5; });
        Assert.Equal("platform_disabled", (await w.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.Reason);
        Assert.True((await w.Scope(B).Runtime.GetStatusAsync(B, default)).Chat.IsReady);
    }

    // ---- ключ: own ----

    [Fact]
    public async Task own_uses_tenant_key_each_tenant_its_own()
    {
        var w = new AiWorld();
        var ka = AiWorld.NewApiKey();
        var kb = AiWorld.NewApiKey();
        w.SetSettings(A); w.SetSettings(B);
        w.SetOwnKey(A, ka); w.SetOwnKey(B, kb);

        await w.Scope(A).Client.CompleteAsync(Req(), default);
        await w.Scope(B).Client.CompleteAsync(Req(), default);
        await w.Scope(A).Client.CompleteAsync(Req(), default);

        var fps = w.Anthropic.Calls.Select(c => c.KeyFingerprint).ToList();
        Assert.Equal([AiWorld.KeyFingerprint(ka), AiWorld.KeyFingerprint(kb), AiWorld.KeyFingerprint(ka)], fps);
        // usage лише для свого tenant (11g)
        Assert.All(w.Usage.For(A), r => Assert.Equal(A, r.TenantId));
        Assert.Equal(2, w.Usage.For(A).Sum(r => r.RequestCount));
        Assert.Equal(1, w.Usage.For(B).Sum(r => r.RequestCount));
    }

    [Fact]
    public async Task own_state_reasons_secrets_missing_unreadable()
    {
        // немає keyring -> secrets_unavailable (раніше за own_key_missing)
        var noRing = new AiWorld(withKeyring: false);
        noRing.SetSettings(A);
        Assert.Equal("secrets_unavailable", (await noRing.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.Reason);

        // keyring є, ключа немає
        var w = new AiWorld();
        w.SetSettings(A);
        Assert.Equal("own_key_missing", (await w.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.Reason);

        // шифротекст tenant A, переставлений у рядок tenant B (AAD) -> own_key_unreadable, виклику провайдера немає
        w.SetSettings(B);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        w.Keys.Rows[(B, "anthropic")] = new BeautyCrm.Infrastructure.Data.Entities.AiProviderKey
        {
            TenantId = B, Provider = "anthropic", KeyEncrypted = w.Keys.Rows[(A, "anthropic")].KeyEncrypted, KeyKid = "k1", KeyLast4 = "xxxx",
        };
        Assert.Equal("own_key_unreadable", (await w.Scope(B).Runtime.GetStatusAsync(B, default)).Chat.Reason);
        await Assert.ThrowsAsync<AiUnavailableException>(() => w.Scope(B).Client.CompleteAsync(Req(), default));
        Assert.Empty(w.Anthropic.Calls);
    }

    [Fact]
    public async Task own_key_with_unknown_kid_is_secrets_unavailable_and_old_kid_still_decrypts_after_rotation()
    {
        var w = new AiWorld();
        w.SetSettings(A);
        var key = AiWorld.NewApiKey();
        w.SetOwnKey(A, key);
        var k1 = w.Config["Ai:Secrets:Keys:k1"]!;

        // ротація: k2 активний, k1 лишається -> старий шифротекст читається
        var rotated = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Secrets:ActiveKeyId"] = "k2", ["Ai:Secrets:Keys:k1"] = k1, ["Ai:Secrets:Keys:k2"] = AiWorld.NewKeyBase64(),
        }).Build();
        w.ReplaceKeyring(rotated);
        Assert.True((await w.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.IsReady);

        // k1 видалено з keyring -> невідомий kid
        var lost = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Secrets:ActiveKeyId"] = "k2", ["Ai:Secrets:Keys:k2"] = AiWorld.NewKeyBase64(),
        }).Build();
        w.ReplaceKeyring(lost);
        Assert.Equal("secrets_unavailable", (await w.Scope(A).Runtime.GetStatusAsync(A, default)).Chat.Reason);
    }

    [Fact]
    public async Task no_adapter_for_any_default_provider_is_provider_not_available()
    {
        var w = new AiWorld();
        w.SetSettings(A);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        // реєстр без адаптера anthropic (як після зміни коду/каталогу)
        var tenant = new BeautyCrm.Infrastructure.Data.Tenancy.TenantContext(A);
        var rt = new AiTenantRuntime(w.Settings, w.Keys, w.Usage, w.Catalog, new AiProviderRegistry([]), w.Protector, w.Platform, tenant, w.Clock);
        var st = await rt.GetStatusAsync(A, default);
        Assert.Equal("provider_not_available", st.Chat.Reason);
        Assert.Equal("provider_not_available", st.Analytics.Reason);
    }

    // ---- ролі, моделі ----

    [Fact]
    public async Task roles_use_their_own_models_and_one_key_serves_both_roles_on_one_provider()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => { r.ChatModel = "claude-opus-5-5"; r.AnalyticsModel = "claude-haiku-5-5"; });
        w.SetOwnKey(A, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        var st = await sc.Runtime.GetStatusAsync(A, default);
        Assert.True(st.Chat.IsReady && st.Analytics.IsReady);

        await sc.Client.CompleteAsync(Req(AiRoles.Chat), default);
        await sc.Client.CompleteAsync(Req(AiRoles.Analytics), default);

        Assert.Equal(["claude-opus-5-5", "claude-haiku-5-5"], w.Anthropic.Calls.Select(c => c.Model).ToList());
        Assert.Equal(["chat", "analytics"], w.Anthropic.Calls.Select(c => c.Role).ToList());
        Assert.Equal(2, w.Usage.For(A).Count); // role в обліку
        Assert.Contains(w.Usage.For(A), u => u.Role == "analytics" && u.Model == "claude-haiku-5-5");
    }

    [Fact]
    public async Task disabled_or_unknown_stored_model_falls_back_to_role_default()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => { r.ChatModel = "claude-fable-5-1"; r.AnalyticsModel = "gpt-made-up"; });
        w.SetOwnKey(A, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(AiRoles.Chat), default);
        await sc.Client.CompleteAsync(Req(AiRoles.Analytics), default);
        Assert.Equal(["claude-haiku-5-5", "claude-sonnet-5-5"], w.Anthropic.Calls.Select(c => c.Model).ToList());
    }

    [Fact]
    public async Task unknown_role_is_rejected()
    {
        var w = new AiWorld();
        await Assert.ThrowsAsync<ArgumentException>(() => w.Scope(A).Client.CompleteAsync(Req("admin"), default));
    }

    // ---- ліміти ----

    [Fact]
    public async Task max_output_tokens_is_min_of_tenant_model_and_hard_cap()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.MaxOutputTokens = 64);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        await w.Scope(A).Client.CompleteAsync(Req(), default);
        Assert.Equal(64, w.Anthropic.Calls.Last().MaxOutputTokens);

        w.SetSettings(A, r => r.MaxOutputTokens = 8192);
        await w.Scope(A).Client.CompleteAsync(Req(), default);
        Assert.Equal(4096, w.Anthropic.Calls.Last().MaxOutputTokens); // модель/серверний cap 4096

        w.Config["Ai:MaxOutputTokensHardCap"] = "100";
        w.Rebuild();
        await w.Scope(A).Client.CompleteAsync(Req(), default);
        Assert.Equal(100, w.Anthropic.Calls.Last().MaxOutputTokens);
    }

    [Fact]
    public async Task request_limit_blocks_after_L_and_provider_not_called_beyond()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.MonthlyRequestLimit = 2);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(), default);
        await sc.Client.CompleteAsync(Req(), default);
        var ex = await Assert.ThrowsAsync<AiQuotaExceededException>(() => sc.Client.CompleteAsync(Req(), default));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ex.RetryAfter);
        Assert.Equal(2, w.Anthropic.Calls.Count);
        Assert.Equal(AiRoleStatus.No("quota_exceeded"), (await sc.Runtime.GetStatusAsync(A, default)).Chat);
    }

    [Fact]
    public async Task token_limit_counts_input_plus_output_and_checks_before_the_call()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.MonthlyTokenLimit = 25); // кожен виклик = 12 + 8 = 20
        w.SetOwnKey(A, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(), default); // 0 < 25
        await sc.Client.CompleteAsync(Req(), default); // 20 < 25
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => sc.Client.CompleteAsync(Req(), default)); // 40 >= 25
        Assert.Equal(2, w.Anthropic.Calls.Count);
    }

    [Fact]
    public async Task platform_effective_limit_is_the_smaller_of_tenant_limit_and_platform_quota()
    {
        var w = new AiWorld();
        w.EnablePlatform(AiWorld.NewApiKey(), tokens: 1_000_000, requests: 3);
        w.SetSettings(A, r => { r.KeySource = "platform"; r.MonthlyRequestLimit = 10; }); // менше -> квота платформи 3
        w.SetSettings(B, r => { r.KeySource = "platform"; r.MonthlyRequestLimit = 1; });  // менше -> ліміт tenant 1
        for (var i = 0; i < 3; i++) await w.Scope(A).Client.CompleteAsync(Req(), default);
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => w.Scope(A).Client.CompleteAsync(Req(), default));
        await w.Scope(B).Client.CompleteAsync(Req(), default);
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => w.Scope(B).Client.CompleteAsync(Req(), default));
    }

    [Fact]
    public async Task limit_is_shared_across_roles_and_resets_with_utc_month()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.MonthlyRequestLimit = 2);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(AiRoles.Analytics), default);
        await sc.Client.CompleteAsync(Req(AiRoles.Chat), default);
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => sc.Client.CompleteAsync(Req(AiRoles.Chat), default));

        // 1-ше число наступного місяця UTC: лічильник новий (23:30 -05:00 31.10 = 04:30 UTC 01.11)
        w.Clock.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-5));
        await sc.Client.CompleteAsync(Req(AiRoles.Chat), default);
        Assert.Contains(w.Usage.For(A), u => u.PeriodStart == new DateOnly(2026, 11, 1));
    }

    [Fact]
    public void period_is_utc_calendar_month()
    {
        Assert.Equal(new DateOnly(2026, 12, 1), AiTenantRuntime.PeriodOf(new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.FromHours(-5))));
        Assert.Equal(new DateOnly(2026, 11, 1), AiTenantRuntime.PeriodOf(new DateTimeOffset(2026, 12, 1, 1, 0, 0, TimeSpan.FromHours(3))));
        Assert.Equal(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), AiTenantRuntime.NextPeriodStart(new DateTimeOffset(2026, 12, 15, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task rate_limit_per_tenant_per_minute_does_not_consume_quota()
    {
        var w = new AiWorld();
        w.Config["Ai:RateLimit:PerTenantPerMinute"] = "2";
        w.Rebuild();
        w.SetSettings(A); w.SetSettings(B);
        w.SetOwnKey(A, AiWorld.NewApiKey()); w.SetOwnKey(B, AiWorld.NewApiKey());
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(), default);
        await sc.Client.CompleteAsync(Req(), default);
        await Assert.ThrowsAsync<AiRateLimitedException>(() => sc.Client.CompleteAsync(Req(), default));
        Assert.Equal(2, w.Usage.For(A).Sum(r => r.RequestCount));
        await w.Scope(B).Client.CompleteAsync(Req(), default); // інший tenant не зачеплений
        w.Clock.Now = w.Clock.Now.AddMinutes(1);
        await sc.Client.CompleteAsync(Req(), default);
    }

    [Fact]
    public async Task concurrent_calls_at_request_limit_L_reach_provider_exactly_L_times()
    {
        var w = new AiWorld();
        w.SetSettings(A, r => r.MonthlyRequestLimit = 5);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        w.Config["Ai:RateLimit:PerTenantPerMinute"] = "1000";
        w.Rebuild();
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            try { await w.Scope(A).Client.CompleteAsync(Req(), default); return true; }
            catch (AiQuotaExceededException) { return false; }
        })));
        Assert.Equal(5, results.Count(r => r));
        Assert.Equal(5, w.Anthropic.Calls.Count);
    }

    // ---- облік без вмісту, помилки провайдера, логи ----

    [Fact]
    public async Task usage_records_role_provider_model_source_and_tokens_without_message_content()
    {
        var w = new AiWorld();
        var key = AiWorld.NewApiKey();
        w.SetSettings(A);
        w.SetOwnKey(A, key);
        var resp = await w.Scope(A).Client.CompleteAsync(Req(), default);

        var row = Assert.Single(w.Usage.For(A));
        Assert.Equal(("chat", "anthropic", "claude-haiku-5-5", "own"), (row.Role, row.Provider, row.Model, row.KeySource));
        Assert.Equal((1, 0, 12L, 8L), (row.RequestCount, row.ErrorCount, row.InputTokens, row.OutputTokens));
        Assert.Equal(new AiUsage(12, 8), resp.Usage);
        var json = JsonSerializer.Serialize(row);
        Assert.DoesNotContain("ТАЄМНИЙ", json);
        Assert.DoesNotContain(key, json);
        Assert.DoesNotContain(key[^4..], json);
    }

    [Fact]
    public async Task provider_error_counts_error_not_tokens_and_leaks_neither_key_nor_message()
    {
        var w = new AiWorld();
        var key = AiWorld.NewApiKey();
        w.SetSettings(A);
        w.SetOwnKey(A, key);
        w.Anthropic.Handler = _ => throw new InvalidOperationException($"boom body-with-key {key}");
        var sc = w.Scope(A);

        var ex = await Assert.ThrowsAsync<AiProviderException>(() => sc.Client.CompleteAsync(Req(), default));

        Assert.Equal(AiProviderError.Unavailable, ex.Error);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(key, ex.ToString());
        Assert.DoesNotContain(key, sc.Log.All);
        Assert.DoesNotContain(key[^4..], sc.Log.All);
        var row = Assert.Single(w.Usage.For(A));
        Assert.Equal((1, 1, 0L), (row.RequestCount, row.ErrorCount, row.InputTokens + row.OutputTokens));
    }

    [Fact]
    public async Task mapped_provider_errors_keep_their_code_and_still_count_errors()
    {
        var w = new AiWorld();
        w.SetSettings(A);
        w.SetOwnKey(A, AiWorld.NewApiKey());
        w.Anthropic.Handler = _ => throw new AiProviderException(AiProviderError.KeyRejected, 401);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => w.Scope(A).Client.CompleteAsync(Req(), default));
        Assert.Equal((AiProviderError.KeyRejected, 401), (ex.Error, ex.HttpStatus));
        Assert.Equal(1, w.Usage.For(A).Single().ErrorCount);
    }

    [Fact]
    public async Task logs_contain_neither_key_mask_prompt_nor_client_text()
    {
        var w = new AiWorld();
        var key = AiWorld.NewApiKey();
        w.SetSettings(A);
        w.SetOwnKey(A, key);
        var sc = w.Scope(A);
        await sc.Client.CompleteAsync(Req(), default);
        w.SetSettings(A, r => r.MonthlyRequestLimit = 1);
        await Assert.ThrowsAsync<AiQuotaExceededException>(() => sc.Client.CompleteAsync(Req(), default));

        var log = sc.Log.All;
        Assert.Contains("ai_call", log);
        foreach (var secret in new[] { key, key[^4..], "ТАЄМНИЙ", "sk-ant" })
            Assert.DoesNotContain(secret, log);
    }

    [Fact]
    public void ai_secret_never_prints_or_serializes_its_value()
    {
        var s = new AiSecret(AiWorld.NewApiKey());
        Assert.Equal("***", s.ToString());
        Assert.Equal("{}", JsonSerializer.Serialize(s));
        var call = new AiProviderCall(Req(), "m", 1, s);
        Assert.DoesNotContain("sk-ant", call.ToString());
        Assert.DoesNotContain("sk-ant", $"{s}");
    }

    // ---- tenant ----

    [Fact]
    public async Task tenant_other_than_scope_throws_TenantMismatchException_not_a_generic_error()
    {
        var w = new AiWorld();
        var sc = w.Scope(A);
        var ex = await Assert.ThrowsAsync<TenantMismatchException>(() => sc.Runtime.GetEffectiveAsync(B, default));
        Assert.IsNotAssignableFrom<InvalidOperationException>(ex);
        Assert.DoesNotContain(A.ToString(), ex.Message);
        Assert.DoesNotContain(B.ToString(), ex.Message);
    }

    [Fact]
    public async Task client_without_tenant_scope_fails_closed()
    {
        var w = new AiWorld();
        var tenant = new BeautyCrm.Infrastructure.Data.Tenancy.TenantContext();
        var rt = new AiTenantRuntime(w.Settings, w.Keys, w.Usage, w.Catalog, w.Registry(), w.Protector, w.Platform, tenant, w.Clock);
        var client = new TenantAiClient(rt, w.Usage, w.Limiter, tenant, w.Clock, new CapturingLogger<TenantAiClient>());
        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => client.CompleteAsync(Req(), default));
        Assert.Equal("tenant_missing", ex.Reason);
    }
}
