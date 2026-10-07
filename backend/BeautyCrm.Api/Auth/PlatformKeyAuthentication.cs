using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BeautyCrm.Api.Auth;

/// <summary>
/// Автентифікація оператора платформи за ключем із .env (Auth:PlatformKey, заголовок X-Platform-Key).
/// Ключ коротший за 32 символи = ендпоінти оператора вимкнені (fail closed). Порівняння — у сталий час.
/// </summary>
public sealed class PlatformKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "PlatformKey";
    public const string HeaderName = "X-Platform-Key";
    public const int MinKeyLength = 32;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = config["Auth:PlatformKey"];
        if (string.IsNullOrEmpty(expected) || expected.Length < MinKeyLength) return Task.FromResult(AuthenticateResult.NoResult());
        if (!Request.Headers.TryGetValue(HeaderName, out var provided) || provided.Count != 1)
            return Task.FromResult(AuthenticateResult.NoResult());

        // Хешування вирівнює довжини, щоб FixedTimeEquals не розкривав довжину ключа.
        var ok = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
        if (!ok) return Task.FromResult(AuthenticateResult.Fail("Invalid platform key."));

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.PlatformOperator)], Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
