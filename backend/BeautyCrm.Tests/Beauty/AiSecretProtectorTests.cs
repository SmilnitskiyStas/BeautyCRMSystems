using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BeautyCrm.Api.Infrastructure;
using BeautyCrm.Infrastructure.AI.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-714: протектор ключів ШІ v2 (AES-256-GCM, kid, AAD tenant|provider), keyring, перешифрування. Без БД.</summary>
public class AiSecretProtectorTests
{
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    // Синтетичний «ключ провайдера»: збирається в рантаймі, у репозиторії значення немає.
    private static string NewApiKey() => "synthetic-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static AiSecretKeyring Ring(string? active, params (string Kid, string Value)[] keys)
    {
        var d = new Dictionary<string, string?>();
        if (active is not null) d["Ai:Secrets:ActiveKeyId"] = active;
        foreach (var (kid, value) in keys) d[$"Ai:Secrets:Keys:{kid}"] = value;
        return new AiSecretKeyring(new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    private static AesGcmAiSecretProtector Protector(AiSecretKeyring ring) => new(ring);

    private static AesGcmAiSecretProtector Ready(out string key)
    {
        key = NewKey();
        return Protector(Ring("k1", ("k1", key)));
    }

    private static AiSecretError Fails(Action a) => Assert.Throws<AiSecretException>(a).Error;

    [Fact]
    public void protect_roundtrip_returns_plaintext_and_v2_format()
    {
        var p = Ready(out _);
        var apiKey = NewApiKey();
        var enc = p.Protect(apiKey, TenantA, "anthropic");

        Assert.Matches(new Regex("^v2:k1:[A-Za-z0-9+/]+=*$"), enc);
        Assert.DoesNotContain(apiKey, enc);
        Assert.Equal(apiKey, p.Unprotect(enc, TenantA, "anthropic"));
        Assert.Equal("k1", p.TryGetKeyId(enc));
    }

    [Fact]
    public void protect_uses_fresh_nonce_each_time()
    {
        var p = Ready(out _);
        var a = p.Protect("same-value-123", TenantA, "anthropic");
        var b = p.Protect("same-value-123", TenantA, "anthropic");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void payload_layout_is_nonce_tag_ciphertext()
    {
        var p = Ready(out _);
        var enc = p.Protect("abc", TenantA, "anthropic");
        var raw = Convert.FromBase64String(enc.Split(':')[2]);
        Assert.Equal(12 + 16 + 3, raw.Length);
    }

    [Fact]
    public void unprotect_fails_under_other_tenant()
    {
        var p = Ready(out _);
        var enc = p.Protect(NewApiKey(), TenantA, "anthropic");
        Assert.Equal(AiSecretError.DecryptionFailed, Fails(() => p.Unprotect(enc, TenantB, "anthropic")));
    }

    [Fact]
    public void unprotect_fails_under_other_provider()
    {
        var p = Ready(out _);
        var enc = p.Protect(NewApiKey(), TenantA, "anthropic");
        Assert.Equal(AiSecretError.DecryptionFailed, Fails(() => p.Unprotect(enc, TenantA, "openai")));
    }

    [Fact]
    public void rotation_decrypts_with_old_kid_and_encrypts_with_active()
    {
        var k1 = NewKey();
        var k2 = NewKey();
        var apiKey = NewApiKey();
        var oldEnc = Protector(Ring("k1", ("k1", k1))).Protect(apiKey, TenantA, "anthropic");

        var rotated = Protector(Ring("k2", ("k1", k1), ("k2", k2)));
        Assert.Equal(apiKey, rotated.Unprotect(oldEnc, TenantA, "anthropic"));
        var newEnc = rotated.Protect(apiKey, TenantA, "anthropic");
        Assert.Equal("k2", rotated.TryGetKeyId(newEnc));

        // Після видалення старого kid із keyring старий шифротекст нечитаний (UnknownKeyId), новий читається.
        var onlyNew = Protector(Ring("k2", ("k2", k2)));
        Assert.Equal(AiSecretError.UnknownKeyId, Fails(() => onlyNew.Unprotect(oldEnc, TenantA, "anthropic")));
        Assert.Equal(apiKey, onlyNew.Unprotect(newEnc, TenantA, "anthropic"));
    }

    [Fact]
    public void swapped_kid_fails_to_decrypt()
    {
        var p = Protector(Ring("k1", ("k1", NewKey()), ("k2", NewKey())));
        var enc = p.Protect(NewApiKey(), TenantA, "anthropic");
        var forged = enc.Replace("v2:k1:", "v2:k2:", StringComparison.Ordinal);
        Assert.Equal(AiSecretError.DecryptionFailed, Fails(() => p.Unprotect(forged, TenantA, "anthropic")));
    }

    [Fact]
    public void unknown_kid_is_rejected()
    {
        var p = Ready(out _);
        var enc = p.Protect(NewApiKey(), TenantA, "anthropic").Replace("v2:k1:", "v2:zz:", StringComparison.Ordinal);
        Assert.Equal(AiSecretError.UnknownKeyId, Fails(() => p.Unprotect(enc, TenantA, "anthropic")));
    }

    [Fact]
    public void truncated_or_corrupted_ciphertext_is_rejected_without_leak()
    {
        var p = Ready(out _);
        var enc = p.Protect(NewApiKey(), TenantA, "anthropic");
        var payload = enc.Split(':')[2];
        var raw = Convert.FromBase64String(payload);

        var flipped = (byte[])raw.Clone();
        flipped[^1] ^= 0x01;
        var tagFlipped = (byte[])raw.Clone();
        tagFlipped[14] ^= 0x01;

        var cases = new[]
        {
            ("truncated-half", $"v2:k1:{Convert.ToBase64String(raw[..(raw.Length / 2)])}", AiSecretError.DecryptionFailed),
            ("only-nonce", $"v2:k1:{Convert.ToBase64String(raw[..12])}", AiSecretError.MalformedCiphertext),
            ("body-flipped", $"v2:k1:{Convert.ToBase64String(flipped)}", AiSecretError.DecryptionFailed),
            ("tag-flipped", $"v2:k1:{Convert.ToBase64String(tagFlipped)}", AiSecretError.DecryptionFailed),
            ("not-base64", "v2:k1:%%%not-base64%%%", AiSecretError.MalformedCiphertext),
            ("empty-payload", "v2:k1:", AiSecretError.MalformedCiphertext),
            ("wrong-version", $"v1:k1:{payload}", AiSecretError.MalformedCiphertext),
            ("legacy-no-prefix", payload, AiSecretError.MalformedCiphertext),
            ("extra-segment", $"{enc}:x", AiSecretError.MalformedCiphertext),
            ("empty", "", AiSecretError.MalformedCiphertext),
        };
        foreach (var (name, value, expected) in cases)
        {
            var ex = Assert.Throws<AiSecretException>(() => p.Unprotect(value, TenantA, "anthropic"));
            Assert.True(expected == ex.Error, $"{name}: expected {expected}, got {ex.Error}");
            Assert.DoesNotContain(payload, ex.ToString());
        }
    }

    [Fact]
    public void missing_keyring_fails_closed_without_throwing_on_construction()
    {
        var ring = Ring(null);
        Assert.Equal(AiSecretsState.NotConfigured, ring.State);
        var p = Protector(ring);
        Assert.Equal(AiSecretError.SecretsUnavailable, Fails(() => p.Protect("value-123", TenantA, "anthropic")));
        Assert.Equal(AiSecretError.SecretsUnavailable, Fails(() => p.Unprotect("v2:k1:AAAA", TenantA, "anthropic")));
    }

    [Fact]
    public void empty_placeholder_values_count_as_not_configured()
    {
        var ring = Ring("", ("replace_with_kid", ""));
        Assert.Equal(AiSecretsState.NotConfigured, ring.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("not base64 !!!")]
    [InlineData("")]
    public void invalid_key_material_makes_whole_keyring_unavailable(string bad)
    {
        var good = NewKey();
        var badKey = bad == "short" ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)) : bad;
        var ring = Ring("k1", ("k1", good), ("k2", badKey));
        if (string.IsNullOrEmpty(bad))
        {
            // порожнє значення = ключ відсутній, решта валідна
            Assert.Equal(AiSecretsState.Ready, ring.State);
            return;
        }
        Assert.Equal(AiSecretsState.Invalid, ring.State);
        Assert.NotNull(ring.Problem);
        Assert.DoesNotContain(good, ring.Problem);
        Assert.Equal(AiSecretError.SecretsUnavailable, Fails(() => Protector(ring).Protect("value-123", TenantA, "anthropic")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("BAD KID!")]
    public void active_kid_must_exist_in_keyring(string active)
    {
        var ring = Ring(active, ("k1", NewKey()));
        Assert.Equal(AiSecretsState.Invalid, ring.State);
        Assert.Equal(AiSecretError.SecretsUnavailable, Fails(() => Protector(ring).Protect("value-123", TenantA, "anthropic")));
    }

    [Fact]
    public void invalid_kid_format_in_keyring_is_rejected()
    {
        Assert.Equal(AiSecretsState.Invalid, Ring("k_1", ("k_1", NewKey())).State);
        Assert.Equal(AiSecretsState.Invalid, Ring("a", ("a", NewKey()), (new string('a', 33), NewKey())).State);
    }

    [Fact]
    public void invalid_arguments_are_rejected()
    {
        var p = Ready(out _);
        Assert.Equal(AiSecretError.InvalidArgument, Fails(() => p.Protect("", TenantA, "anthropic")));
        Assert.Equal(AiSecretError.InvalidArgument, Fails(() => p.Protect("x-value-123", Guid.Empty, "anthropic")));
        Assert.Equal(AiSecretError.InvalidArgument, Fails(() => p.Protect("x-value-123", TenantA, "Anthropic|x")));
    }

    [Fact]
    public void nothing_leaks_via_tostring_exceptions_or_keyring()
    {
        var keyB64 = NewKey();
        var ring = Ring("k1", ("k1", keyB64));
        var p = Protector(ring);
        var apiKey = NewApiKey();
        var enc = p.Protect(apiKey, TenantA, "anthropic");
        var payload = enc.Split(':')[2];

        var ex = Assert.Throws<AiSecretException>(() => p.Unprotect(enc, TenantB, "anthropic"));
        var texts = new[]
        {
            ring.ToString(), p.ToString() ?? "", ex.ToString(), ex.Message, new AiSecretRow("anthropic", enc).ToString(),
            ring.Problem ?? "",
        };
        foreach (var t in texts)
        {
            Assert.DoesNotContain(apiKey, t);
            Assert.DoesNotContain(keyB64, t);
            Assert.DoesNotContain(payload, t);
        }
        Assert.Null(ex.InnerException);
    }

    // ---- логування ----

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
    }

    [Fact]
    public async Task startup_check_never_throws_and_logs_without_values()
    {
        var keyB64 = NewKey();
        foreach (var ring in new[] { Ring(null), Ring("zz", ("k1", keyB64)), Ring("k1", ("k1", keyB64)), Ring("k1", ("k1", "bad-bad")) })
        {
            var log = new CaptureLogger<AiSecretsStartupCheck>();
            await new AiSecretsStartupCheck(ring, log).StartAsync(CancellationToken.None);
            Assert.Single(log.Lines);
            Assert.DoesNotContain(keyB64, log.Lines[0]);
            Assert.DoesNotContain("bad-bad", log.Lines[0]);
        }
    }

    // ---- перешифрування ----

    private sealed class FakeStore : IAiSecretRowStore
    {
        public Dictionary<(Guid Tenant, string Provider), string> Rows { get; } = [];
        public Guid? FailTenant { get; set; }
        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Guid>>(Rows.Keys.Select(k => k.Tenant).Distinct().ToList());
        public Task<IReadOnlyList<AiSecretRow>> ListRowsAsync(Guid tenantId, CancellationToken ct)
        {
            if (tenantId == FailTenant) throw new InvalidOperationException("boom");
            return Task.FromResult<IReadOnlyList<AiSecretRow>>(
                Rows.Where(r => r.Key.Tenant == tenantId).Select(r => new AiSecretRow(r.Key.Provider, r.Value)).ToList());
        }
        public Task<bool> ReplaceAsync(Guid tenantId, string provider, string expectedOld, string newValue, string newKeyId, CancellationToken ct)
        {
            var k = (tenantId, provider);
            if (!Rows.TryGetValue(k, out var cur) || cur != expectedOld) return Task.FromResult(false);
            Rows[k] = newValue;
            return Task.FromResult(true);
        }
        public Task<bool> DeleteAsync(Guid tenantId, string provider, string expectedOld, CancellationToken ct)
        {
            var k = (tenantId, provider);
            if (!Rows.TryGetValue(k, out var cur) || cur != expectedOld) return Task.FromResult(false);
            Rows.Remove(k);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task reencrypt_moves_rows_to_active_kid_and_is_idempotent()
    {
        var k1 = NewKey();
        var k2 = NewKey();
        var old = Protector(Ring("k1", ("k1", k1)));
        var apiA = NewApiKey();
        var apiB = NewApiKey();
        var store = new FakeStore();
        store.Rows[(TenantA, "anthropic")] = old.Protect(apiA, TenantA, "anthropic");
        store.Rows[(TenantB, "anthropic")] = old.Protect(apiB, TenantB, "anthropic");

        var p = Protector(Ring("k2", ("k1", k1), ("k2", k2)));
        var log = new CaptureLogger<AiSecretReencryptor>();
        var svc = new AiSecretReencryptor(p, store, log);

        var first = await svc.RunAsync(new AiReencryptOptions(), CancellationToken.None);
        Assert.Equal(new AiReencryptReport(2, 2, 2, 0, 0, 0, 0, 0), first);
        Assert.True(first.Complete);
        Assert.All(store.Rows.Values, v => Assert.Equal("k2", p.TryGetKeyId(v)));
        Assert.Equal(apiA, p.Unprotect(store.Rows[(TenantA, "anthropic")], TenantA, "anthropic"));
        Assert.Equal(apiB, p.Unprotect(store.Rows[(TenantB, "anthropic")], TenantB, "anthropic"));

        var snapshot = store.Rows.ToDictionary(kv => kv.Key, kv => kv.Value);
        var second = await svc.RunAsync(new AiReencryptOptions(), CancellationToken.None);
        Assert.Equal(new AiReencryptReport(2, 2, 0, 2, 0, 0, 0, 0), second);
        Assert.Equal(snapshot, store.Rows);

        // старий kid можна прибрати: усе читається лише новим
        var onlyNew = Protector(Ring("k2", ("k2", k2)));
        Assert.Equal(apiA, onlyNew.Unprotect(store.Rows[(TenantA, "anthropic")], TenantA, "anthropic"));
    }

    [Fact]
    public async Task reencrypt_single_tenant_leaves_others_untouched()
    {
        var k1 = NewKey();
        var old = Protector(Ring("k1", ("k1", k1)));
        var store = new FakeStore();
        store.Rows[(TenantA, "anthropic")] = old.Protect(NewApiKey(), TenantA, "anthropic");
        store.Rows[(TenantB, "anthropic")] = old.Protect(NewApiKey(), TenantB, "anthropic");
        var before = store.Rows[(TenantB, "anthropic")];

        var p = Protector(Ring("k2", ("k1", k1), ("k2", NewKey())));
        var report = await new AiSecretReencryptor(p, store, new CaptureLogger<AiSecretReencryptor>())
            .RunAsync(new AiReencryptOptions(TenantA), CancellationToken.None);

        Assert.Equal(1, report.Reencrypted);
        Assert.Equal(before, store.Rows[(TenantB, "anthropic")]);
    }

    [Fact]
    public async Task reencrypt_reports_unreadable_and_drops_only_on_request()
    {
        var k1 = NewKey();
        var old = Protector(Ring("k1", ("k1", k1)));
        var store = new FakeStore();
        // шифротекст переставлено між tenants: читається лише під своїм AAD
        store.Rows[(TenantA, "anthropic")] = old.Protect(NewApiKey(), TenantB, "anthropic");
        store.Rows[(TenantB, "anthropic")] = old.Protect(NewApiKey(), TenantB, "anthropic");

        var p = Protector(Ring("k2", ("k1", k1), ("k2", NewKey())));
        var svc = new AiSecretReencryptor(p, store, new CaptureLogger<AiSecretReencryptor>());

        var r1 = await svc.RunAsync(new AiReencryptOptions(), CancellationToken.None);
        Assert.Equal(1, r1.Reencrypted);
        Assert.Equal(1, r1.Unreadable);
        Assert.Equal(1, r1.Remaining);
        Assert.False(r1.Complete);
        Assert.Equal(2, store.Rows.Count);

        var r2 = await svc.RunAsync(new AiReencryptOptions(DropUnreadable: true), CancellationToken.None);
        Assert.Equal(1, r2.Dropped);
        Assert.True(r2.Complete);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task reencrypt_tenant_failure_is_counted_and_logged_without_details()
    {
        var k1 = NewKey();
        var old = Protector(Ring("k1", ("k1", k1)));
        var store = new FakeStore { FailTenant = TenantA };
        store.Rows[(TenantA, "anthropic")] = old.Protect(NewApiKey(), TenantA, "anthropic");
        store.Rows[(TenantB, "anthropic")] = old.Protect(NewApiKey(), TenantB, "anthropic");
        var log = new CaptureLogger<AiSecretReencryptor>();

        var report = await new AiSecretReencryptor(Protector(Ring("k2", ("k1", k1), ("k2", NewKey()))), store, log)
            .RunAsync(new AiReencryptOptions(), CancellationToken.None);

        Assert.Equal(1, report.FailedTenants);
        Assert.Equal(1, report.Reencrypted);
        Assert.False(report.Complete);
        Assert.Single(log.Lines);
        Assert.DoesNotContain("boom", log.Lines[0]);
        Assert.DoesNotContain(TenantA.ToString(), log.Lines[0]);
    }

    [Fact]
    public async Task reencrypt_without_keyring_throws_secrets_unavailable()
    {
        var svc = new AiSecretReencryptor(Protector(Ring(null)), new FakeStore(), new CaptureLogger<AiSecretReencryptor>());
        var ex = await Assert.ThrowsAsync<AiSecretException>(() => svc.RunAsync(new AiReencryptOptions(), CancellationToken.None));
        Assert.Equal(AiSecretError.SecretsUnavailable, ex.Error);
    }

    [Fact]
    public async Task cli_prints_counts_only_and_returns_exit_codes()
    {
        var k1 = NewKey();
        var old = Protector(Ring("k1", ("k1", k1)));
        var apiKey = NewApiKey();
        var store = new FakeStore();
        store.Rows[(TenantA, "anthropic")] = old.Protect(apiKey, TenantA, "anthropic");

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Secrets:ActiveKeyId"] = "k2",
            ["Ai:Secrets:Keys:k1"] = k1,
            ["Ai:Secrets:Keys:k2"] = NewKey(),
        }).Build();
        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(cfg)
            .AddLogging()
            .AddBeautyAiSecrets()
            .AddSingleton<IAiSecretRowStore>(store) // перевизначає Npgsql-реалізацію (остання реєстрація виграє)
            .BuildServiceProvider();

        var sw = new StringWriter();
        var code = await AiSecretsCli.RunAsync(services, ["reencrypt"], sw);
        Assert.Equal(0, code);
        Assert.Contains("reencrypted=1", sw.ToString());
        Assert.DoesNotContain(apiKey, sw.ToString());
        Assert.DoesNotContain(TenantA.ToString(), sw.ToString());

        Assert.Equal(2, await AiSecretsCli.RunAsync(services, [], new StringWriter()));
        Assert.Equal(2, await AiSecretsCli.RunAsync(services, ["reencrypt", "--tenant", "not-a-guid"], new StringWriter()));
    }
}
