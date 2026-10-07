using BeautyCrm.Application.Features.BeautyAuth;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.Data.Auth;

/// <summary>
/// bcrypt (BCrypt.Net-Next) з «enhanced» режимом: пароль попередньо хешується SHA-384, тож обмеження bcrypt
/// у 72 байти не усікає довгі паролі. Cost з Auth:BcryptWorkFactor (за замовчуванням 12; мінімум 4 лише для тестів).
/// </summary>
public sealed class BcryptPasswordHasher(IConfiguration config) : IPasswordHasher
{
    private readonly int _workFactor = Math.Clamp(int.TryParse(config["Auth:BcryptWorkFactor"], out var wf) ? wf : 12, 4, 16);

    public string Hash(string password) => BCrypt.Net.BCrypt.EnhancedHashPassword(password, _workFactor);

    public bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.EnhancedVerify(password, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or BCrypt.Net.SaltParseException)
        {
            return false; // пошкоджений хеш = невдала перевірка, не 500
        }
    }
}
