using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BeautyCrm.Api.Auth;

public sealed class JwtSettings
{
    public required byte[] SigningKey { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    /// <summary>Auth:JwtSigningKey = base64, щонайменше 32 байти (openssl rand -base64 48). Немає/короткий ключ -> старт відхиляється.</summary>
    public static JwtSettings From(IConfiguration config)
    {
        var raw = config["Auth:JwtSigningKey"];
        byte[] key;
        try
        {
            key = string.IsNullOrWhiteSpace(raw) ? [] : Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            key = [];
        }
        if (key.Length < 32)
            throw new InvalidOperationException("Auth:JwtSigningKey (env Auth__JwtSigningKey) must be base64 of at least 32 bytes.");
        return new JwtSettings
        {
            SigningKey = key,
            Issuer = config["Auth:Issuer"] ?? "beautycrm",
            Audience = config["Auth:Audience"] ?? "beautycrm-api",
        };
    }
}

/// <summary>Access JWT (HS256): sub, tenant_id, role, [specialist_id]. Без PII у claims.</summary>
public sealed class JwtTokenIssuer(JwtSettings settings, AuthOptions options) : ITokenIssuer
{
    public const string TenantClaim = "tenant_id";
    public const string SpecialistClaim = "specialist_id";
    public const string RoleClaim = "role";

    public IssuedAccessToken Issue(UserRecord user, DateTimeOffset now)
    {
        var expires = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            [TenantClaim] = user.TenantId.ToString(),
            [RoleClaim] = user.Role,
        };
        if (user.SpecialistId is { } specialistId) claims[SpecialistClaim] = specialistId.ToString();

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha256),
        };
        return new IssuedAccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
