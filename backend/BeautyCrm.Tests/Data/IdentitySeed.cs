using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BeautyCrm.Tests.Data;

/// <summary>
/// Єдиний тестовий хелпер сідування користувача разом з ідентичністю (ADR-024: <c>users.identity_id</c> NOT NULL, тригер узгодженості
/// пошти, без compat-механізму). Три форми для різних місць виклику:
///  1. <see cref="UserSql"/> / <see cref="InsertUserAsync"/> - SQL від імені власника БД (superuser), для сидів через <c>Exec(admin, ...)</c>;
///     ідентичність вставляється напряму (пошта нормалізується, <c>pending</c> лише для NULL-пароля), членство бере її за поштою;
///  2. <see cref="EnsureAsync"/> / <see cref="NewUserAsync"/> - для EF-контексту ролі застосунку (RLS): ті самі SECURITY DEFINER-функції
///     <c>auth_identity_find</c> / <c>auth_identity_create</c>, що й у production-коді.
/// </summary>
public static class IdentitySeed
{
    private static string Lit(string? v) => v is null ? "NULL" : "'" + v.Replace("'", "''") + "'";

    private static string Norm(string email) => email.Trim().ToLowerInvariant();

    /// <summary>
    /// Дві SQL-інструкції: ідентичність (ON CONFLICT DO NOTHING: та сама пошта в кількох tenant = одна людина) і рядок users.
    /// <paramref name="passwordHash"/> = null -> pending-ідентичність (стан членства зазвичай <c>invited</c>).
    /// </summary>
    public static string UserSql(
        Guid tenantId, string email, string fullName, string? passwordHash, string role, Guid? id = null, string? state = null,
        bool? isActive = null, Guid? specialistId = null, bool asApp = false)
    {
        var e = Norm(email);
        var cols = new List<string> { "tenant_id", "email", "full_name", "password_hash", "role", "identity_id" };
        var vals = new List<string>
        {
            $"'{tenantId}'", Lit(email), Lit(fullName), Lit(passwordHash), Lit(role), $"(SELECT i.id FROM identities i WHERE i.email = {Lit(e)})",
        };
        if (id is { } g) { cols.Insert(0, "id"); vals.Insert(0, $"'{g}'"); }
        if (state is not null) { cols.Add("state"); vals.Add(Lit(state)); }
        if (isActive is { } a) { cols.Add("is_active"); vals.Add(a ? "true" : "false"); }
        if (specialistId is { } s) { cols.Add("specialist_id"); vals.Add($"'{s}'"); }
        // asApp: від імені ролі застосунку (RLS, без прав на identities) - ідентичність створюють функції auth_identity_create[_pending]
        var identity = asApp
            ? (passwordHash is null
                ? $"SELECT public.auth_identity_create_pending({Lit(e)}, NULL, 'platform'); "
                : $"SELECT public.auth_identity_create({Lit(e)}, {Lit(passwordHash)}, NULL, 'platform', NULL); ")
            : $"INSERT INTO identities (email, password_hash, state) VALUES ({Lit(e)}, {Lit(passwordHash)}, {(passwordHash is null ? "'pending'" : "'active'")}) ON CONFLICT (email) DO NOTHING; ";
        if (asApp) vals[vals.FindIndex(v => v.StartsWith("(SELECT i.id"))] = $"(SELECT identity_id FROM public.auth_identity_find({Lit(e)}))";
        return identity + $"INSERT INTO users ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});";
    }

    public static async Task InsertUserAsync(
        NpgsqlConnection admin, Guid tenantId, string email, string fullName, string? passwordHash, string role, Guid? id = null,
        string? state = null, bool? isActive = null, Guid? specialistId = null)
    {
        await using var cmd = new NpgsqlCommand(UserSql(tenantId, email, fullName, passwordHash, role, id, state, isActive, specialistId), admin);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>id ідентичності за поштою; якщо немає - створює активну через auth_identity_create (via = platform). Для контексту ролі застосунку.</summary>
    public static async Task<Guid> EnsureAsync(BeautyDbContext ctx, string email, string passwordHash = "x")
    {
        var e = Norm(email);
        var found = await ctx.Database.SqlQuery<Guid>($"SELECT identity_id AS \"Value\" FROM auth_identity_find({e})").ToListAsync();
        if (found.Count > 0) return found[0];
        return (await ctx.Database
            .SqlQuery<Guid>($"SELECT public.auth_identity_create({e}, {passwordHash}, NULL, 'platform', NULL) AS \"Value\"").ToListAsync()).Single();
    }

    /// <summary>Нове членство (не додане до контексту) з уже створеною ідентичністю. Пошта за замовчуванням <c>{id:N}@t.test</c>.</summary>
    public static async Task<User> NewUserAsync(
        BeautyDbContext ctx, string role, string fullName = "Тест", Guid? id = null, string? email = null, string passwordHash = "x")
    {
        var uid = id ?? Guid.NewGuid();
        var mail = email ?? $"{uid:N}@t.test";
        return new User
        {
            Id = uid, Email = mail, FullName = fullName, PasswordHash = passwordHash, Role = role,
            IdentityId = await EnsureAsync(ctx, mail, passwordHash),
        };
    }
}
