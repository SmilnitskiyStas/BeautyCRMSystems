using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BeautyCrm.Api.Auth.ControlPlane;

/// <summary>
/// Автентифікація <c>ControlPlaneHmac</c> v1 для групи <c>/api/control/*</c> (ADR-018, beauty-contracts.md §27.2).
/// Порядок перевірок: група увімкнена -> заголовки й формат -> kid відомий (active|retiring) -> |now - ts| &lt;= 300 с ->
/// тіло &lt;= 64 KB -> підпис (FixedTimeEquals) -> лише тоді запис nonce (0 рядків = replay) -> ліміт 60/хв на kid.
/// Будь-яка невдача підпису/формату/вікна/nonce/розміру = однакова відповідь <c>401 {"code":"control_auth_failed"}</c>;
/// причина — лише в лог (без підпису, секрету, тіла). 429 віддається тільки після валідного підпису й nonce.
/// </summary>
public sealed class ControlPlaneHmacHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder,
    ControlPlaneInboundOptions keys, ControlPlaneRateLimiter limiter, TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string Scheme = "ControlPlaneHmac";
    public const string KidClaim = "cp_kid";

    /// <summary>Items: kid / відбиток / request id успішно автентифікованого запиту (для аудиту в TASK-818).</summary>
    public const string KidItem = "ControlPlane.Kid";
    public const string FingerprintItem = "ControlPlane.KeyFingerprint";
    public const string RequestIdItem = "ControlPlane.RequestId";
    private const string ResultItem = "ControlPlane.AuthResult";
    private const string RetryAfterItem = "ControlPlane.RetryAfter";

    public const string FailedBody = "{\"code\":\"control_auth_failed\"}";
    public const string RateLimitedBody = "{\"code\":\"rate_limited\"}";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Authorization-middleware повторно викликає схему: результат (і nonce) рахується один раз на запит.
        if (Context.Items.TryGetValue(ResultItem, out var cached) && cached is AuthenticateResult prior) return prior;
        var result = await VerifyAsync();
        Context.Items[ResultItem] = result;
        return result;
    }

    private async Task<AuthenticateResult> VerifyAsync()
    {
        var req = Request;
        if (!keys.Enabled) return Reject("disabled");

        var kid = Single(ControlPlaneSignature.KeyIdHeader);
        var timestamp = Single(ControlPlaneSignature.TimestampHeader);
        var nonce = Single(ControlPlaneSignature.NonceHeader);
        var signature = Single(ControlPlaneSignature.SignatureHeader);
        if (kid is null || timestamp is null || nonce is null || signature is null) return Reject("missing_header");
        if (!ControlPlaneSignature.KidFormat.IsMatch(kid) || !ControlPlaneSignature.TimestampFormat.IsMatch(timestamp)
            || !ControlPlaneSignature.NonceFormat.IsMatch(nonce) || !ControlPlaneSignature.SignatureFormat.IsMatch(signature)
            || !ControlPlaneSignature.IsAllowedMethod(req.Method))
            return Reject("missing_header");

        // kid уже у валідному форматі: його безпечно писати в лог.
        if (!keys.TryGetKey(kid, out var key)) return Reject("unknown_key");

        var skew = time.GetUtcNow().ToUnixTimeSeconds() - long.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(skew) > ControlPlaneSignature.WindowSeconds) return Reject("timestamp_skew", kid);

        var body = await ReadBodyAsync();
        if (body is null) return Reject("body_too_large", kid);

        var path = req.PathBase.Add(req.Path).Value ?? "/";
        var canonical = ControlPlaneSignature.Canonical(kid, timestamp, nonce, req.Method, path, query: "", body);
        var expected = ControlPlaneSignature.Sign(key.Secret, canonical);
        if (!ControlPlaneSignature.SignaturesEqual(expected, signature)) return Reject("bad_signature", kid);

        // Підпис валідний: тільки тепер nonce потрапляє в БД (невалідні запити не можуть її наповнювати).
        bool fresh;
        try
        {
            var store = Context.RequestServices.GetRequiredService<IControlPlaneNonceStore>();
            fresh = await store.TryRegisterAsync(kid, nonce, Context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Control plane auth rejected: nonce store failure (kid {Kid})", kid);
            return AuthenticateResult.Fail("nonce_store_error"); // fail closed, відповідь 401 як в усіх
        }
        if (!fresh) return Reject("nonce_replayed", kid);

        if (!limiter.TryAcquire(kid, out var retryAfter))
        {
            Context.Items[RetryAfterItem] = retryAfter;
            Logger.LogWarning("Control plane rate limited: kid {Kid}", kid);
            return AuthenticateResult.Fail("rate_limited");
        }

        var requestId = Single(ControlPlaneSignature.RequestIdHeader);
        Context.Items[KidItem] = kid;
        Context.Items[FingerprintItem] = key.Fingerprint;
        if (requestId is { Length: > 0 and <= 64 }) Context.Items[RequestIdItem] = requestId;

        var identity = new ClaimsIdentity([new Claim(KidClaim, kid)], Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }

    private string? Single(string header) =>
        Request.Headers.TryGetValue(header, out var v) && v.Count == 1 && !string.IsNullOrEmpty(v[0]) ? v[0] : null;

    /// <summary>Тіло як отримано (байти), не більше 64 KB; null — завелике. Потік перемотується для подальших читачів.</summary>
    private async Task<byte[]?> ReadBodyAsync()
    {
        const int limit = ControlPlaneSignature.MaxBodyBytes;
        if (Request.ContentLength is > limit) return null;

        Request.EnableBuffering(limit + 1);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while (ms.Length <= limit && (n = await Request.Body.ReadAsync(buffer, Context.RequestAborted)) > 0)
            ms.Write(buffer, 0, n);
        Request.Body.Position = 0;
        return ms.Length > limit ? null : ms.ToArray();
    }

    private AuthenticateResult Reject(string reason, string? kid = null)
    {
        // Лише причина, kid (формат перевірено) і IP; жодних значень підпису/секрету/тіла.
        Logger.LogWarning("Control plane auth failed: {Reason} (kid {Kid}, ip {Ip})", reason, kid ?? "-", Context.Connection.RemoteIpAddress);
        return AuthenticateResult.Fail(reason);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.CacheControl = "no-store";
        Response.ContentType = "application/json; charset=utf-8";
        if (Context.Items.TryGetValue(RetryAfterItem, out var ra) && ra is int seconds)
        {
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Response.WriteAsync(RateLimitedBody, Encoding.UTF8);
            return;
        }
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Response.WriteAsync(FailedBody, Encoding.UTF8);
    }

    // Forbid для цієї схеми недоступний за змістом (немає ролей); на випадок політик — та сама форма 401.
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => HandleChallengeAsync(properties);
}
