using System.Net;
using System.Text;
using System.Text.Json;
using BeautyCrm.Api.Auth;
using BeautyCrm.Api.Auth.ControlPlane;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Auth;

/// <summary>Тестовий ендпоінт групи /api/control/v1 (справжні ендпоінти керування - TASK-818). Лише в цій збірці тестів.</summary>
[ApiController]
[Route("api/control/v1/probe")]
[Authorize(Policy = AuthPolicies.ControlPlane)]
public sealed class ControlPlaneProbeController : ControllerBase
{
    [HttpGet, HttpPost, HttpPut]
    public async Task<IActionResult> Echo()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        return Ok(new { kid = User.FindFirst(ControlPlaneHmacHandler.KidClaim)?.Value, bodyLength = ms.Length });
    }
}

public sealed class FrozenTime(long unix) : TimeProvider
{
    public long Unix { get; set; } = unix;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Unix);
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = [];
    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.Lines) owner.Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception));
        }
    }
}

/// <summary>Вектор ADR-018 (.ai/artifacts/contracts/ADR-018-hmac-test-vector.json).</summary>
public sealed record HmacVector(string Name, string Method, string Path, string Query, string Timestamp, string Nonce, byte[] Body,
    string BodySha256Hex, string Canonical, string ExpectedSignature, string TrailingSpaceSignature)
{
    public static string FilePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var f = System.IO.Path.Combine(dir.FullName, ".ai", "artifacts", "contracts", "ADR-018-hmac-test-vector.json");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("ADR-018-hmac-test-vector.json not found above " + AppContext.BaseDirectory);
    }

    public static (string Kid, string SecretBase64, string Fingerprint, IReadOnlyList<HmacVector> Vectors) Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath()));
        var root = doc.RootElement;
        var key = root.GetProperty("key");
        var list = root.GetProperty("vectors").EnumerateArray().Select(v => new HmacVector(
            v.GetProperty("name").GetString()!, v.GetProperty("method").GetString()!, v.GetProperty("path").GetString()!,
            v.GetProperty("query").GetString()!, v.GetProperty("timestamp").GetInt64().ToString(), v.GetProperty("nonce").GetString()!,
            Encoding.UTF8.GetBytes(v.GetProperty("bodyUtf8").GetString()!), v.GetProperty("bodySha256Hex").GetString()!,
            v.GetProperty("canonicalString").GetString()!, v.GetProperty("expectedSignature").GetString()!,
            v.GetProperty("negative").GetProperty("bodyWithTrailingSpaceSignature").GetString()!)).ToList();
        return (key.GetProperty("kid").GetString()!, key.GetProperty("secretBase64").GetString()!, key.GetProperty("fingerprint").GetString()!, list);
    }
}

/// <summary>TASK-817: схема ControlPlaneHmac (ADR-018, §27.2). Юніт-частина без БД; HTTP-частина - реальний PostgreSQL.</summary>
public sealed class ControlPlaneHmacTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>
{
    private const string Kid = "test-k1";
    private const string RetiringKid = "test-k0";
    private const string SecondKid = "test-k2";
    private const long T0 = 1793700000; // timestamp першого вектора
    private static readonly string RetiringSecret = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1)).ToArray());
    private static readonly string SecondSecret = Convert.ToBase64String(Enumerable.Range(0, 40).Select(i => (byte)(255 - i)).ToArray());

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private static byte[] Raw(string b64) => Convert.FromBase64String(b64);
    private static string Nonce() => "N" + Guid.NewGuid().ToString("N");

    // ---------- юніт: вектор ----------

    [Fact]
    public void vector_signatures_match_byte_for_byte()
    {
        var (kid, secretB64, fingerprint, vectors) = HmacVector.Load();
        var key = Raw(secretB64);
        Assert.Equal(3, vectors.Count);
        Assert.Equal(fingerprint, ControlPlaneSignature.Fingerprint(key));
        foreach (var v in vectors)
        {
            Assert.Equal(v.BodySha256Hex, ControlPlaneSignature.BodySha256Hex(v.Body));
            var canonical = ControlPlaneSignature.Canonical(kid, v.Timestamp, v.Nonce, v.Method, v.Path, v.Query, v.Body);
            Assert.Equal(v.Canonical, canonical);
            Assert.Equal(v.ExpectedSignature, ControlPlaneSignature.Sign(key, canonical));

            // зміна одного байта тіла (зайвий пробіл) -> інший підпис, рівний вектору "negative"
            var tampered = ControlPlaneSignature.Canonical(kid, v.Timestamp, v.Nonce, v.Method, v.Path, v.Query, [.. v.Body, (byte)' ']);
            var tamperedSig = ControlPlaneSignature.Sign(key, tampered);
            Assert.Equal(v.TrailingSpaceSignature, tamperedSig);
            Assert.False(ControlPlaneSignature.SignaturesEqual(v.ExpectedSignature, tamperedSig));
        }
    }

    [Fact]
    public void vector_every_canonical_field_changes_the_signature()
    {
        var (kid, secretB64, _, vectors) = HmacVector.Load();
        var key = Raw(secretB64);
        var v = vectors[0];
        string Sig(string k = "", string ts = "", string n = "", string m = "", string p = "", string q = "x")
        {
            var c = ControlPlaneSignature.Canonical(k.Length > 0 ? k : kid, ts.Length > 0 ? ts : v.Timestamp, n.Length > 0 ? n : v.Nonce,
                m.Length > 0 ? m : v.Method, p.Length > 0 ? p : v.Path, q == "x" ? v.Query : q, v.Body);
            return ControlPlaneSignature.Sign(key, c);
        }
        Assert.Equal(v.ExpectedSignature, Sig());
        var changed = new[]
        {
            Sig(k: "test-k9"), Sig(ts: "1793700001"), Sig(n: v.Nonce + "x"), Sig(m: "PUT"), Sig(p: v.Path + "/"), Sig(q: "a=1"),
        };
        Assert.All(changed, s => Assert.NotEqual(v.ExpectedSignature, s));
        Assert.Equal(changed.Length, changed.Distinct().Count());
    }

    [Fact]
    public void key_is_decoded_base64_bytes_not_the_base64_text()
    {
        var (kid, secretB64, _, vectors) = HmacVector.Load();
        var v = vectors[0];
        var canonical = ControlPlaneSignature.Canonical(kid, v.Timestamp, v.Nonce, v.Method, v.Path, v.Query, v.Body);
        Assert.NotEqual(v.ExpectedSignature, ControlPlaneSignature.Sign(Encoding.UTF8.GetBytes(secretB64), canonical));
    }

    [Fact]
    public void signatureComparison_is_fixed_time_api_and_rejects_any_difference()
    {
        var (_, secretB64, _, vectors) = HmacVector.Load();
        var good = vectors[0].ExpectedSignature;
        Assert.True(ControlPlaneSignature.SignaturesEqual(good, good));
        char Flip(char c) => c == 'A' ? 'B' : 'A';
        Assert.False(ControlPlaneSignature.SignaturesEqual(good, Flip(good[0]) + good[1..]));
        Assert.False(ControlPlaneSignature.SignaturesEqual(good, good[..^1] + Flip(good[^1])));
        Assert.False(ControlPlaneSignature.SignaturesEqual(good, good[..^1]));
        Assert.False(ControlPlaneSignature.SignaturesEqual(good, good + "A"));
        Assert.False(ControlPlaneSignature.SignaturesEqual(good, ""));
        // Порівняння реалізоване через CryptographicOperations.FixedTimeEquals - перевіряємо, що жодна гілка не використовує string ==.
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "BeautyCrm.Api", "Auth", "ControlPlane", "ControlPlaneSignature.cs"));
        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
        var handler = File.ReadAllText(Path.Combine(RepoRoot(), "backend", "BeautyCrm.Api", "Auth", "ControlPlane", "ControlPlaneHmacHandler.cs"));
        Assert.Contains("SignaturesEqual(expected, signature)", handler);
        Assert.DoesNotContain("expected == ", handler);
        Assert.DoesNotContain("signature == ", handler);
        _ = secretB64;
    }

    private static string RepoRoot() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(HmacVector.FilePath())!, "..", "..", ".."));

    // ---------- юніт: конфіг ----------

    private static ControlPlaneInboundOptions Opts(params (string Kid, string? Secret, string? Status)[] keys)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, s, st) in keys)
        {
            if (s is not null) d[$"ControlPlane:Inbound:Keys:{k}:Secret"] = s;
            if (st is not null) d[$"ControlPlane:Inbound:Keys:{k}:Status"] = st;
        }
        return ControlPlaneInboundOptions.From(new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    [Fact]
    public void options_disabled_without_valid_active_key_and_ignore_invalid_keys()
    {
        var good = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var short31 = Convert.ToBase64String(new byte[31].Select((_, i) => (byte)(i + 1)).ToArray());

        Assert.False(Opts().Enabled);
        Assert.False(Opts((Kid, null, null)).Enabled);
        Assert.False(Opts((Kid, "", "active")).Enabled);
        Assert.False(Opts((Kid, short31, "active")).Enabled);               // < 32 байт
        Assert.False(Opts((Kid, "***not-base64***", "active")).Enabled);
        Assert.False(Opts((Kid, good, null)).Enabled);                      // немає Status
        Assert.False(Opts((Kid, good, "enabled")).Enabled);                 // невідомий Status
        Assert.False(Opts(("ab", good, "active")).Enabled);                 // kid < 3
        Assert.False(Opts((Kid, good, "retiring")).Enabled);                // лише retiring = нуль активних

        var ok = Opts((Kid, good, "active"), (RetiringKid, good, "retiring"), ("bad-k", short31, "active"));
        Assert.True(ok.Enabled);
        Assert.True(ok.TryGetKey(Kid, out var k1) && k1.Status == ControlPlaneKeyStatus.Active);
        Assert.True(ok.TryGetKey(RetiringKid, out var k0) && k0.Status == ControlPlaneKeyStatus.Retiring);
        Assert.False(ok.TryGetKey("bad-k", out _)); // короткий ключ відкинуто, решта працює
        Assert.Equal(12, k1.Fingerprint.Length);
        Assert.DoesNotContain(good, k1.ToString());
        Assert.True(Opts(("TEST-K1", good, "ACTIVE")).Enabled); // регістр kid/status з env
    }

    [Fact]
    public void rateLimiter_allows_60_per_minute_per_kid_then_retryAfter()
    {
        var clock = new FrozenTime(120);
        var limiter = new ControlPlaneRateLimiter(clock);
        for (var i = 0; i < 60; i++) Assert.True(limiter.TryAcquire("a-kid", out _));
        Assert.False(limiter.TryAcquire("a-kid", out var retry));
        Assert.InRange(retry, 1, 60);
        Assert.True(limiter.TryAcquire("b-kid", out _));
        clock.Unix += 60;
        Assert.True(limiter.TryAcquire("a-kid", out _));
    }

    // ---------- HTTP ----------

    private (WebApplicationFactory<Program> Factory, FrozenTime Clock, CapturingLoggerProvider Log) Build(
        Action<IWebHostBuilder>? configure = null, bool withKeys = true, long now = T0)
    {
        var (_, secretB64, _, _) = HmacVector.Load();
        var clock = new FrozenTime(now);
        var log = new CapturingLoggerProvider();
        var f = fx.Factory!.WithWebHostBuilder(b =>
        {
            if (withKeys)
            {
                b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Secret", secretB64);
                b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Status", "active");
                b.UseSetting($"ControlPlane:Inbound:Keys:{RetiringKid}:Secret", RetiringSecret);
                b.UseSetting($"ControlPlane:Inbound:Keys:{RetiringKid}:Status", "retiring");
                b.UseSetting($"ControlPlane:Inbound:Keys:{SecondKid}:Secret", SecondSecret);
                b.UseSetting($"ControlPlane:Inbound:Keys:{SecondKid}:Status", "active");
            }
            configure?.Invoke(b);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
                s.AddSingleton<ILoggerProvider>(log);
                s.AddControllers().AddApplicationPart(typeof(ControlPlaneProbeController).Assembly);
            });
        });
        return (f, clock, log);
    }

    private static string KeyFor(string kid) => kid switch
    {
        Kid => HmacVector.Load().SecretBase64,
        RetiringKid => RetiringSecret,
        SecondKid => SecondSecret,
        _ => throw new ArgumentException(kid),
    };

    /// <summary>Підписаний запит; <paramref name="mutate"/> змінює заголовки/параметри ПІСЛЯ підпису.</summary>
    private static HttpRequestMessage Signed(HttpMethod method, string path, string kid, long ts, byte[]? body = null, string? nonce = null,
        string? signPath = null, string? signMethod = null, byte[]? signBody = null, string? signKid = null, Action<HttpRequestMessage>? mutate = null)
    {
        nonce ??= Nonce();
        var canonical = ControlPlaneSignature.Canonical(signKid ?? kid, ts.ToString(), nonce, signMethod ?? method.Method, signPath ?? path, "",
            signBody ?? body ?? []);
        var sig = ControlPlaneSignature.Sign(Raw(KeyFor(kid)), canonical);
        var r = new HttpRequestMessage(method, path);
        if (body is { Length: > 0 })
        {
            r.Content = new ByteArrayContent(body);
            r.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        r.Headers.Add(ControlPlaneSignature.KeyIdHeader, kid);
        r.Headers.Add(ControlPlaneSignature.TimestampHeader, ts.ToString());
        r.Headers.Add(ControlPlaneSignature.NonceHeader, nonce);
        r.Headers.Add(ControlPlaneSignature.SignatureHeader, sig);
        mutate?.Invoke(r);
        return r;
    }

    private const string Probe = "/api/control/v1/probe";

    private async Task<long> NonceCount(string nonce)
    {
        await using var c = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM control_plane_nonces WHERE nonce = @n", c);
        cmd.Parameters.AddWithValue("n", nonce);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    private static string Shape(HttpResponseMessage r, string body) =>
        $"{(int)r.StatusCode}|{r.Content.Headers.ContentType}|{body}|" + string.Join(";", r.Headers.Where(h => h.Key != "Date")
            .OrderBy(h => h.Key).Select(h => h.Key + "=" + string.Join(",", h.Value)));

    [SkippableFact]
    public async Task vector_requests_authenticate_over_http_then_replay_is_rejected()
    {
        NeedDb();
        var (kid, _, _, vectors) = HmacVector.Load();
        var (f, clock, _) = Build(b => b.UseSetting("ControlPlane:PublicAppUrl", "https://app.example.test")); // create із вектора видає запрошення
        using var _f = f;
        var client = f.CreateClient();
        foreach (var v in vectors)
        {
            clock.Unix = long.Parse(v.Timestamp); // годинник заморожено на timestamp вектора
            HttpRequestMessage Make(byte[]? bodyOverride = null)
            {
                var r = new HttpRequestMessage(new HttpMethod(v.Method), v.Path);
                if (v.Body.Length > 0)
                {
                    r.Content = new ByteArrayContent(bodyOverride ?? v.Body);
                    r.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
                }
                r.Headers.Add(ControlPlaneSignature.KeyIdHeader, kid);
                r.Headers.Add(ControlPlaneSignature.TimestampHeader, v.Timestamp);
                r.Headers.Add(ControlPlaneSignature.NonceHeader, v.Nonce);
                r.Headers.Add(ControlPlaneSignature.SignatureHeader, v.ExpectedSignature);
                return r;
            }

            // змінене тіло (зайвий пробіл) з підписом оригіналу -> 401, nonce НЕ записано
            if (v.Body.Length > 0)
            {
                var bad = await client.SendAsync(Make([.. v.Body, (byte)' ']));
                Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
                Assert.Equal(0, await NonceCount(v.Nonce));
            }

            // справжній вектор: підпис пройшов (TASK-818: create -> 201, get/suspend невідомого sg-test-123 -> 404; головне - не 401)
            var ok = await client.SendAsync(Make());
            Assert.Contains(ok.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.NotFound }); // 200 = ідемпотентний повтор create
            Assert.Equal(1, await NonceCount(v.Nonce));
            Assert.Equal(v.Timestamp, ok.Headers.GetValues(ControlPlaneSignature.ServerTimeHeader).Single());

            // replay того самого запиту -> 401
            var replay = await client.SendAsync(Make());
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
            Assert.Equal(ControlPlaneHmacHandler.FailedBody, await Body(replay));
        }
    }

    [SkippableFact]
    public async Task valid_request_passes_with_server_time_and_no_store_and_body_is_readable_downstream()
    {
        NeedDb();
        var (f, clock, _) = Build();
        using var _f = f;
        var body = Encoding.UTF8.GetBytes("{\"a\":\"Привіт\"}");
        var r = await f.CreateClient().SendAsync(Signed(HttpMethod.Post, Probe, Kid, T0, body));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = JsonDocument.Parse(await Body(r)).RootElement;
        Assert.Equal(Kid, j.GetProperty("kid").GetString());
        Assert.Equal(body.Length, j.GetProperty("bodyLength").GetInt64()); // тіло перемотано після підписування
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal(clock.Unix.ToString(), r.Headers.GetValues(ControlPlaneSignature.ServerTimeHeader).Single());
        foreach (var m in new[] { HttpMethod.Get, HttpMethod.Put })
            Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().SendAsync(Signed(m, Probe, Kid, T0))).StatusCode);
    }

    [SkippableFact]
    public async Task replay_is_rejected_and_nonce_is_unique_per_key()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        var n = Nonce();
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, T0, nonce: n))).StatusCode);
        var replay = await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, T0, nonce: n));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(ControlPlaneHmacHandler.FailedBody, await Body(replay));
        // той самий nonce під іншим kid - окремий простір (PK key_id + nonce)
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, SecondKid, T0, nonce: n))).StatusCode);
        Assert.Equal(2, await NonceCount(n));
    }

    [SkippableFact]
    public async Task timestamp_window_is_plus_minus_300_seconds()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        foreach (var delta in new long[] { -300, 300, 0 })
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, T0 + delta))).StatusCode);
        foreach (var delta in new long[] { -301, 301, -86400, 86400 })
        {
            var n = Nonce();
            var r = await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, T0 + delta, nonce: n));
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
            Assert.Equal(0, await NonceCount(n));
        }
    }

    [SkippableFact]
    public async Task retiring_key_is_accepted_and_unknown_kid_is_rejected()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        var r = await c.SendAsync(Signed(HttpMethod.Get, Probe, RetiringKid, T0));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(RetiringKid, JsonDocument.Parse(await Body(r)).RootElement.GetProperty("kid").GetString());

        // kid, якого немає в конфігу: підписаний будь-яким ключем
        var n = Nonce();
        var unknown = Signed(HttpMethod.Get, Probe, Kid, T0, nonce: n, signKid: "test-k9",
            mutate: m => { m.Headers.Remove(ControlPlaneSignature.KeyIdHeader); m.Headers.Add(ControlPlaneSignature.KeyIdHeader, "test-k9"); });
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(unknown)).StatusCode);
        Assert.Equal(0, await NonceCount(n));
    }

    [SkippableFact]
    public async Task group_is_disabled_without_valid_active_key_even_for_a_retiring_key()
    {
        NeedDb();
        // жодних ключів
        var (f1, _, _) = Build(withKeys: false);
        using var _f1 = f1;
        Assert.Equal(HttpStatusCode.Unauthorized, (await f1.CreateClient().SendAsync(Signed(HttpMethod.Get, Probe, RetiringKid, T0))).StatusCode);

        // лише retiring + короткий active
        var (f2, _, _) = Build(b =>
        {
            b.UseSetting($"ControlPlane:Inbound:Keys:{RetiringKid}:Secret", RetiringSecret);
            b.UseSetting($"ControlPlane:Inbound:Keys:{RetiringKid}:Status", "retiring");
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Secret", Convert.ToBase64String(new byte[16]));
            b.UseSetting($"ControlPlane:Inbound:Keys:{Kid}:Status", "active");
        }, withKeys: false);
        using var _f2 = f2;
        var n = Nonce();
        var r = await f2.CreateClient().SendAsync(Signed(HttpMethod.Get, Probe, RetiringKid, T0, nonce: n));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(ControlPlaneHmacHandler.FailedBody, await Body(r));
        Assert.Equal(0, await NonceCount(n));
    }

    [SkippableFact]
    public async Task invalid_requests_share_one_401_form_and_never_write_the_nonce()
    {
        NeedDb();
        var (f, _, log) = Build();
        using var _f = f;
        var c = f.CreateClient();
        var body = Encoding.UTF8.GetBytes("{\"x\":1}");

        void Header(HttpRequestMessage m, string name, string? value)
        {
            m.Headers.Remove(name);
            if (value is not null) m.Headers.TryAddWithoutValidation(name, value);
        }

        var cases = new List<(string Name, string Nonce, HttpRequestMessage Req)>();
        void Add(string name, Func<string, HttpRequestMessage> make) { var n = Nonce(); cases.Add((name, n, make(n))); }

        Add("bad_signature_garbage", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.SignatureHeader, "v1=" + new string('A', 43))));
        Add("signed_with_other_key", n => Signed(HttpMethod.Post, Probe, SecondKid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.KeyIdHeader, Kid)));
        Add("tampered_body", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, signBody: Encoding.UTF8.GetBytes("{\"x\":2}")));
        Add("tampered_path", n => Signed(HttpMethod.Post, Probe + "/", Kid, T0, body, n, signPath: Probe));
        Add("tampered_method", n => Signed(HttpMethod.Put, Probe, Kid, T0, body, n, signMethod: "POST"));
        Add("tampered_timestamp_header", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.TimestampHeader, (T0 + 1).ToString())));
        Add("tampered_nonce_header", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.NonceHeader, n + "x")));
        Add("signature_wrong_prefix", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m =>
            Header(m, ControlPlaneSignature.SignatureHeader, "v2=" + m.Headers.GetValues(ControlPlaneSignature.SignatureHeader).Single()[3..])));
        Add("signature_wrong_length", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.SignatureHeader, "v1=abc")));
        Add("missing_kid", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.KeyIdHeader, null)));
        Add("missing_signature", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.SignatureHeader, null)));
        Add("missing_nonce", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.NonceHeader, null)));
        Add("missing_timestamp", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.TimestampHeader, null)));
        Add("short_nonce", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, "short", mutate: null));
        Add("timestamp_leading_zero", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.TimestampHeader, "0" + T0)));
        Add("bad_kid_format", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, mutate: m => Header(m, ControlPlaneSignature.KeyIdHeader, "TEST_K1")));
        Add("unsupported_method", n => Signed(HttpMethod.Delete, Probe, Kid, T0, null, n));
        Add("expired", n => Signed(HttpMethod.Post, Probe, Kid, T0 - 301, body, n));
        Add("unknown_kid", n => Signed(HttpMethod.Post, Probe, Kid, T0, body, n, signKid: "test-k9", mutate: m => Header(m, ControlPlaneSignature.KeyIdHeader, "test-k9")));
        Add("no_headers_at_all", n => new HttpRequestMessage(HttpMethod.Get, Probe));
        Add("x_platform_key_only", n =>
        {
            var m = new HttpRequestMessage(HttpMethod.Get, Probe);
            m.Headers.Add("X-Platform-Key", PlatformKey);
            return m;
        });
        Add("jwt_bearer_only", n =>
        {
            var m = new HttpRequestMessage(HttpMethod.Get, Probe);
            m.Headers.Authorization = new("Bearer", "eyJhbGciOiJIUzI1NiJ9.e30.x");
            return m;
        });
        Add("body_too_large", n => Signed(HttpMethod.Post, Probe, Kid, T0, new byte[ControlPlaneSignature.MaxBodyBytes + 1], n));

        string? first = null;
        foreach (var (name, nonce, req) in cases)
        {
            var r = await c.SendAsync(req);
            var b = await Body(r);
            Assert.True(r.StatusCode == HttpStatusCode.Unauthorized, $"{name}: expected 401, got {(int)r.StatusCode}");
            Assert.Equal(ControlPlaneHmacHandler.FailedBody, b);
            first ??= Shape(r, b);
            Assert.True(first == Shape(r, b), $"{name}: response differs from the common 401 shape");
            Assert.Equal(0, await NonceCount(nonce)); // nonce не записується при невалідному підписі/форматі/вікні
        }
        Assert.Contains("\"code\":\"control_auth_failed\"", first);
        Assert.Contains("no-store", first);

        // Причини є в логу, але без підпису/секрету/тіла.
        string[] lines;
        lock (log.Lines) lines = log.Lines.ToArray();
        var joined = string.Join("\n", lines);
        Assert.Contains("bad_signature", joined);
        Assert.Contains("timestamp_skew", joined);
        Assert.Contains("unknown_key", joined);
        Assert.Contains("missing_header", joined);
        Assert.Contains("body_too_large", joined);
        Assert.DoesNotContain(HmacVector.Load().SecretBase64, joined);
        Assert.DoesNotContain(RetiringSecret, joined);
        Assert.DoesNotContain("v1=", joined);
        Assert.DoesNotContain(PlatformKey, joined);
    }

    private async Task<List<(string Reason, string? Kid, string? KeyId, string Actor, string Details)>> FailedAudit()
    {
        await using var c = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT details->>'reason', details->>'kid', key_id, actor_type, details::text FROM platform_audit_log WHERE action = 'control_auth.failed' ORDER BY id", c);
        await using var rd = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string?, string?, string, string)>();
        while (await rd.ReadAsync())
            rows.Add((rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetString(3), rd.GetString(4)));
        return rows;
    }

    [SkippableFact]
    public async Task failed_auth_is_audited_at_most_once_per_minute_per_ip_and_reason_without_secrets()
    {
        NeedDb();
        var (f, clock, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        var body = Encoding.UTF8.GetBytes("{\"x\":1}");
        var before = (await FailedAudit()).Count;

        HttpRequestMessage BadSig() => Signed(HttpMethod.Post, Probe, Kid, clock.Unix, body, signBody: Encoding.UTF8.GetBytes("{\"x\":2}"));
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(BadSig())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, Probe))).StatusCode); // missing_header, інша причина

        var rows = (await FailedAudit()).Skip(before).ToList();
        Assert.Equal(2, rows.Count);
        var bad = Assert.Single(rows, r => r.Reason == "bad_signature");
        Assert.Equal(Kid, bad.Kid);
        Assert.Equal(Kid, bad.KeyId);
        Assert.Equal("system", bad.Actor);
        Assert.Contains(rows, r => r.Reason == "missing_header" && r.Kid is null);

        // нове вікно: наступна невдача пишеться й несе кількість пропущених (2)
        clock.Unix += 61;
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(BadSig())).StatusCode);
        var after = (await FailedAudit()).Skip(before).Where(r => r.Reason == "bad_signature").ToList();
        Assert.Equal(2, after.Count);
        Assert.Contains("\"suppressed\": 2", after[1].Details);

        // без PII і секретів
        var all = string.Join("\n", (await FailedAudit()).Skip(before).Select(r => r.Details));
        Assert.DoesNotContain(HmacVector.Load().SecretBase64, all);
        Assert.DoesNotContain("v1=", all);
        Assert.DoesNotContain("{\"x\"", all);
    }

    [SkippableFact]
    public async Task body_limit_64KB_boundary()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        var exact = new byte[ControlPlaneSignature.MaxBodyBytes];
        Array.Fill(exact, (byte)'a');
        var r = await c.SendAsync(Signed(HttpMethod.Post, Probe, Kid, T0, exact));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(exact.Length, JsonDocument.Parse(await Body(r)).RootElement.GetProperty("bodyLength").GetInt32());

        var n = Nonce();
        var over = await c.SendAsync(Signed(HttpMethod.Post, Probe, Kid, T0, [.. exact, (byte)'a'], n));
        Assert.Equal(HttpStatusCode.Unauthorized, over.StatusCode);
        Assert.Equal(0, await NonceCount(n));
    }

    [SkippableFact]
    public async Task nonEmpty_query_is_400_before_signature_check()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        // без жодних заголовків і з валідним підписом (query не входить у підпис) - однаково 400
        var anon = await c.SendAsync(new HttpRequestMessage(HttpMethod.Get, Probe + "?a=1"));
        Assert.Equal(HttpStatusCode.BadRequest, anon.StatusCode);
        Assert.Equal("{\"code\":\"query_not_allowed\"}", await Body(anon));
        var n = Nonce();
        var signed = Signed(HttpMethod.Get, Probe + "?x=y", Kid, T0, nonce: n, signPath: Probe);
        var r = await c.SendAsync(signed);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, await NonceCount(n));
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.True(r.Headers.Contains(ControlPlaneSignature.ServerTimeHeader));
        // порожній query ('?' без параметрів) = порожній рядок у canonical
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe + "?", Kid, T0, signPath: Probe))).StatusCode);
    }

    [SkippableFact]
    public async Task unknown_route_in_group_is_still_401_without_signature()
    {
        NeedDb();
        var (f, _, _) = Build();
        using var _f = f;
        var r = await f.CreateClient().GetAsync("/api/control/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(ControlPlaneHmacHandler.FailedBody, await Body(r));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.CreateClient().GetAsync("/api/control/v2/anything")).StatusCode);
    }

    [SkippableFact]
    public async Task rateLimit_60_per_minute_per_kid_returns_429_after_valid_signature()
    {
        NeedDb();
        var (f, clock, _) = Build();
        using var _f = f;
        var c = f.CreateClient();
        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, clock.Unix))).StatusCode);

        var over = await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, clock.Unix));
        Assert.Equal((HttpStatusCode)429, over.StatusCode);
        Assert.Equal("{\"code\":\"rate_limited\"}", await Body(over));
        Assert.InRange(int.Parse(over.Headers.GetValues("Retry-After").Single()), 1, 60);

        // невалідний підпис при вичерпаному ліміті - усе ще 401 (підпис раніше за 429), не 429
        var bad = await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, clock.Unix, mutate: m =>
        {
            m.Headers.Remove(ControlPlaneSignature.SignatureHeader);
            m.Headers.Add(ControlPlaneSignature.SignatureHeader, "v1=" + new string('B', 43));
        }));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        // інший kid не зачеплено
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, SecondKid, clock.Unix))).StatusCode);

        clock.Unix += 60; // наступне вікно
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Signed(HttpMethod.Get, Probe, Kid, clock.Unix))).StatusCode);
    }

    [SkippableFact]
    public async Task schemes_do_not_cross_over_platformKey_jwt_and_hmac()
    {
        NeedDb();
        var t = await fx.CreateTenantAsync();
        var jwt = (await fx.LoginAsync(t.Slug, t.OwnerEmail)).AccessToken;
        var (f, _, _) = Build();
        using var _f = f;
        var c = f.CreateClient();

        // валідний JWT і X-Platform-Key не відкривають /api/control
        var withJwt = new HttpRequestMessage(HttpMethod.Get, Probe);
        withJwt.Headers.Authorization = new("Bearer", jwt);
        var r1 = await c.SendAsync(withJwt);
        Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);
        Assert.Equal(ControlPlaneHmacHandler.FailedBody, await Body(r1));
        var withKey = new HttpRequestMessage(HttpMethod.Get, Probe);
        withKey.Headers.Add("X-Platform-Key", PlatformKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(withKey)).StatusCode);

        // валідний HMAC не відкриває /api/platform/* (X-Platform-Key обов'язковий)
        var platform = Signed(HttpMethod.Post, "/api/platform/tenants", Kid, T0,
            Encoding.UTF8.GetBytes("{\"name\":\"S\",\"slug\":\"" + Unique("salon") + "\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(platform)).StatusCode);
    }

    [SkippableFact]
    public async Task platformKey_flag_defaults_off_outside_development_and_can_be_enabled()
    {
        NeedDb();
        var body = new { name = "S", slug = Unique("salon"), owner = new { email = "o@x.test", fullName = "O", password = Password } };

        // явно вимкнено (Production) -> 401 навіть із правильним ключем
        using var off = fx.Factory!.WithWebHostBuilder(b => b.UseSetting("Auth:PlatformKeyEnabled", "false"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey, client: off.CreateClient())).StatusCode);

        // прапорець не заданий: у Production (дефолт) вимкнено
        using var unset = fx.Factory!.WithWebHostBuilder(b => b.UseSetting("Auth:PlatformKeyEnabled", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey, client: unset.CreateClient())).StatusCode);

        // у Development без прапорця працює; ввімкнений явно - працює
        using var dev = fx.Factory!.WithWebHostBuilder(b => { b.UseEnvironment("Development"); b.UseSetting("Auth:PlatformKeyEnabled", null); });
        Assert.Equal(HttpStatusCode.Created, (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body, platformKey: PlatformKey, client: dev.CreateClient())).StatusCode);
        var body2 = new { name = "S", slug = Unique("salon"), owner = new { email = "o2@x.test", fullName = "O", password = Password } };
        Assert.Equal(HttpStatusCode.Created, (await fx.Send(HttpMethod.Post, "/api/platform/tenants", body2, platformKey: PlatformKey)).StatusCode);
    }
}
