using System.Text;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>Тіло заявки (§27.9). tenant і користувач у тілі не читаються: вони береться лише з JWT (Actor).</summary>
public sealed record SubmitFeedbackRequest(string? Type, string? Severity, string? Message, string? PageUrl);

public sealed record FeedbackAcceptedDto(Guid Id);

/// <summary>
/// TASK-820 (§27.9, ADR-018 §3.4): прийом заявок «співпраця / покращити / не працює» від користувача tenant. Рядок
/// <c>beauty_platform_feedback</c> із <c>delivery_status = pending</c> одночасно є чергою доставки (окремої outbox-таблиці немає),
/// тож запис заявки й постановка в чергу - один INSERT (TASK-821 доставляє; тут доставки немає). Tenant і користувач - лише з Actor/scope
/// (RLS FORCE); ліміти 5/год на користувача та 30/добу на tenant рахуються за самою таблицею (переживають рестарт і працюють на кількох
/// інстансах), перевірка й вставка серіалізовані advisory lock-ом tenant. Текст заявки й PII (email) не логуються.
/// </summary>
public sealed class PlatformFeedbackService(
    BeautyDbContext db, TenantContext tenant, TimeProvider clock, ILogger<PlatformFeedbackService> log)
{
    public const int PerUserPerHour = 5, PerTenantPerDay = 30;
    public const int MinMessage = 10, MaxMessage = 2000, MaxPageUrl = 500;
    public static readonly string[] Types = ["cooperation", "improvement", "bug"];
    public static readonly string[] Severities = ["low", "normal", "high", "critical"];
    public const string DefaultSeverity = "normal";

    public async Task<Result<FeedbackAcceptedDto>> SubmitAsync(Actor actor, SubmitFeedbackRequest? req, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);

        // Призупинений tenant (чинні access-токени ще живі) і вимкнений користувач - 403 до будь-якого запису.
        var status = await db.Tenants.AsNoTracking().Where(t => t.Id == actor.TenantId).Select(t => t.Status).FirstOrDefaultAsync(ct);
        if (status != "active") return Error.Forbidden("tenant_suspended", "Tenant is suspended.");
        if (!await db.Users.AsNoTracking().AnyAsync(u => u.Id == actor.UserId && u.IsActive, ct))
            return Error.Forbidden("user_inactive", "User is not active.");

        if (req is null || req.Type is not { } type || Array.IndexOf(Types, type) < 0)
            return Error.Validation("invalid_type", "type must be one of: cooperation, improvement, bug.");
        var severity = req.Severity ?? DefaultSeverity;
        if (Array.IndexOf(Severities, severity) < 0)
            return Error.Validation("invalid_severity", "severity must be one of: low, normal, high, critical.");
        if (NormalizeMessage(req.Message) is not { } message)
            return Error.Validation("invalid_message", $"message must be {MinMessage}..{MaxMessage} characters.");
        if (!TryNormalizePageUrl(req.PageUrl, out var pageUrl))
            return Error.Validation("invalid_page_url", "pageUrl must be a path without query or fragment (up to 500 characters).");

        var now = clock.GetUtcNow();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Один замок на tenant: паралельні запити не обходять ліміт (перевірка + вставка атомарні відносно інших заявок tenant).
        var lockKey = $"platform_feedback:{actor.TenantId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var userTimes = await db.PlatformFeedbacks.AsNoTracking()
            .Where(f => f.UserId == actor.UserId && f.CreatedAt > hourAgo).OrderBy(f => f.CreatedAt)
            .Select(f => f.CreatedAt).ToListAsync(ct);
        if (userTimes.Count >= PerUserPerHour)
            return RateLimited(userTimes[userTimes.Count - PerUserPerHour].AddHours(1) - now);
        var tenantTimes = await db.PlatformFeedbacks.AsNoTracking().Where(f => f.CreatedAt > dayAgo).OrderBy(f => f.CreatedAt)
            .Select(f => f.CreatedAt).ToListAsync(ct);
        if (tenantTimes.Count >= PerTenantPerDay)
            return RateLimited(tenantTimes[tenantTimes.Count - PerTenantPerDay].AddDays(1) - now);

        var row = new PlatformFeedback
        {
            Id = Guid.NewGuid(), UserId = actor.UserId, Type = type, Severity = severity, Message = message, PageUrl = pageUrl,
            DeliveryStatus = "pending", NextAttemptAt = now, CreatedAt = now,
        };
        db.PlatformFeedbacks.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Без тексту заявки, pageUrl і email.
        log.LogInformation("Platform feedback {FeedbackId} accepted: tenant {TenantId}, user {UserId}, type {Type}, severity {Severity}, length {Length}",
            row.Id, actor.TenantId, actor.UserId, type, severity, message.Length);
        return new FeedbackAcceptedDto(row.Id);
    }

    /// <summary>Error.Conflicts несе Retry-After (int, секунди до звільнення місця у вікні, мінімум 1); контролер мапить 429 сам і в тіло його не пише.</summary>
    private static Error RateLimited(TimeSpan wait) =>
        Error.TooMany("rate_limited", "Too many requests. Try again later.") with { Conflicts = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) };

    /// <summary>Trim; довжина в Unicode-символах (як char_length у PostgreSQL); без NUL і керуючих символів, крім \n \r \t.</summary>
    internal static string? NormalizeMessage(string? raw)
    {
        if (raw is null) return null;
        var s = raw.Trim();
        var count = 0;
        foreach (var r in s.EnumerateRunes())
        {
            count++;
            if (r.Value is < 0x20 and not (0x09 or 0x0A or 0x0D) || r.Value == 0x7F || r.Value is >= 0x80 and < 0xA0) return null;
            if (count > MaxMessage) return null;
        }
        return count >= MinMessage ? s : null;
    }

    /// <summary>null/порожній -> null (валідно). Інакше: шлях від '/', без '//' на початку, query, фрагмента, пробілів, керуючих символів.</summary>
    internal static bool TryNormalizePageUrl(string? raw, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (raw.Length > MaxPageUrl || raw[0] != '/' || raw.StartsWith("//", StringComparison.Ordinal)) return false;
        foreach (var c in raw)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                || "/-_.~%!$&'()*+,;=:@".Contains(c);
            if (!ok) return false;
        }
        value = raw;
        return true;
    }
}
