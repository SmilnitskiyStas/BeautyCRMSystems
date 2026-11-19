using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Непрозорі токени (refresh, запрошення): <c>{tenantId:N}.{32 випадкові байти base64url}</c>.
/// tenant_id у токені не секрет — він лише дозволяє задати RLS-контекст до пошуку; автентичність дає
/// збіг SHA-256 хеша всього токена з рядком у БД (токен ніколи не зберігається відкритим).
/// </summary>
public static class TokenCodec
{
    public sealed record Parsed(Guid TenantId, string Hash);

    public static (string Token, string Hash) Generate(Guid tenantId)
    {
        var token = $"{tenantId:N}.{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        return (token, Hash(token));
    }

    public static Parsed? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200) return null;
        var dot = token.IndexOf('.');
        if (dot != 32 || token.Length < 44) return null;
        if (!Guid.TryParseExact(token.AsSpan(0, 32), "N", out var tenantId) || tenantId == Guid.Empty) return null;
        return new Parsed(tenantId, Hash(token));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Правила ролей: хто кого може запрошувати й ким керувати. Власник недоторканний через API.</summary>
public static class RolePolicy
{
    public static bool CanInvite(string actorRole, string targetRole) => CanManage(actorRole, targetRole);

    /// <summary>§30.1: <c>rank(actor) &gt; rank(target) &amp;&amp; target != owner</c> (owner недоторканний; нові пари owner/admin -> deputy).</summary>
    public static bool CanManage(string actorRole, string targetRole) =>
        targetRole != Roles.Owner && Roles.Rank(actorRole) > Roles.Rank(targetRole) && Roles.IsTenantRole(targetRole);
}
