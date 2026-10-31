using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace BeautyCrm.Infrastructure.Data.Beauty;

/// <summary>Значення <c>beauty_clients.phone_source</c> (§25.3). NULL = спадковий/невідомий.</summary>
public static class PhoneSources
{
    public const string TelegramContact = "telegram_contact";
    public const string ManualChat = "manual_chat";
    public const string WebForm = "web_form";
    public const string Staff = "staff";
}

public enum PhoneSaveOutcome
{
    /// <summary>Телефону не було: збережено (з джерелом і, якщо була згода, <c>call_consent_at</c>).</summary>
    Saved,
    /// <summary>Той самий номер (за цифрами): телефон без змін, згода оновлена, якщо порожня.</summary>
    Unchanged,
    /// <summary>Є інший номер: новий лежить у <c>phone_pending</c>, наявний не змінено, розмова позначена «потрібна відповідь».</summary>
    PendingConflict,
    InvalidPhone,
    InvalidSource,
    ClientNotFound,
    /// <summary>Розмова не існує в tenant або належить іншому клієнту.</summary>
    ConversationMismatch,
}

/// <param name="DuplicateLinked">Після збереження виставлено м'який зв'язок <c>duplicate_of_client_id</c> (рівно один інший клієнт з тим самим номером).</param>
public sealed record PhoneSaveResult(PhoneSaveOutcome Outcome, bool DuplicateLinked = false)
{
    public bool IsSuccess => Outcome is PhoneSaveOutcome.Saved or PhoneSaveOutcome.Unchanged or PhoneSaveOutcome.PendingConflict;
}

public enum PhoneRequestState
{
    /// <summary>Телефон уже є: питати не треба.</summary>
    HasPhone,
    /// <summary>Телефону немає, пауза після останнього питання минула (або питання не було).</summary>
    Allowed,
    /// <summary>Телефону немає, питали менше ніж <c>ChatPhone:CooldownDays</c> тому.</summary>
    Cooldown,
    ClientNotFound,
}

/// <summary>Відповідь <c>POST /clients/{id}/phone-pending</c> (§25.7): <c>phonePending</c> завжди null.</summary>
public sealed record PhonePendingResolvedDto(string? Phone, string? PhoneSource, DateTimeOffset? CallConsentAt, object? PhonePending = null);

/// <summary>Тіло <c>POST /clients/{id}/phone-pending</c>: <c>action</c> = accept | reject.</summary>
public sealed record PhonePendingRequest(string? Action);

public static class ClientPhoneErrors
{
    public const string ClientNotFound = "client_not_found";
    public const string NoPendingPhone = "no_pending_phone";
    public const string InvalidAction = "invalid_action";
    public const string ForbiddenRole = "forbidden_role";
}

/// <summary>
/// TASK-769 (§25.5.3, §25.7, ADR-014 ч. B): збереження телефону клієнта (для конвеєра TASK-770), cooldown питання, рішення
/// адміністратора щодо <c>phone_pending</c>. Tenant лише з scope (RLS FORCE) і явно переданий tenantId має збігатися.
/// Усі зміни номера - у транзакції під <c>pg_advisory_xact_lock('client-phone:{tenant}:{client}')</c> (якщо виклик уже в транзакції,
/// використовується вона). Наявний номер ніколи не перезаписується мовчки. Номер не логується й не потрапляє в аудит: лише id і коди.
/// </summary>
public sealed class ClientPhoneService(
    BeautyDbContext db, TenantContext tenant, IConfiguration config, TimeProvider clock, ILogger<ClientPhoneService> log)
{
    private int CooldownDays => Math.Clamp(int.TryParse(config["ChatPhone:CooldownDays"], out var n) ? n : 30, 1, 365);

    private static string LockKey(Guid tenantId, Guid clientId) => $"client-phone:{tenantId}:{clientId}";

    private async Task<T> InTransactionAsync<T>(Guid tenantId, Guid clientId, Func<Task<T>> body, CancellationToken ct)
    {
        var ambient = db.Database.CurrentTransaction is not null;
        await using var tx = ambient ? null : await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({LockKey(tenantId, clientId)}, 0))", ct);
        var result = await body();
        if (tx is not null) await tx.CommitAsync(ct);
        return result;
    }

    // ------------------------------------------------------------------ для конвеєра (TASK-770)

    /// <summary>
    /// Правила (§25.5.3): порожній номер -> зберегти; той самий (за цифрами) -> оновити згоду; інший -> <c>phone_pending</c>, наявний не
    /// змінюється. <paramref name="phone"/> - сирий або E.164 (нормалізується тут); <paramref name="source"/> - telegram_contact | manual_chat.
    /// <paramref name="conversationId"/> (якщо задано) мусить належати цьому клієнту; при конфлікті розмова отримує awaiting_staff_since.
    /// </summary>
    public async Task<PhoneSaveResult> SavePhoneAsync(
        Guid tenantId, Guid clientId, string? phone, string source, bool consent, Guid? conversationId = null, CancellationToken ct = default)
    {
        TenantGuard.Ensure(tenant, tenantId);
        if (source is not (PhoneSources.TelegramContact or PhoneSources.ManualChat)) return new PhoneSaveResult(PhoneSaveOutcome.InvalidSource);
        var e164 = PhoneNormalizer.Normalize(phone);
        if (e164 is null) return new PhoneSaveResult(PhoneSaveOutcome.InvalidPhone);

        var result = await InTransactionAsync(tenantId, clientId, async () =>
        {
            var client = await db.Clients.AsNoTracking().Where(c => c.Id == clientId && c.DeletedAt == null)
                .Select(c => new { c.Phone, c.CallConsentAt }).FirstOrDefaultAsync(ct);
            if (client is null) return new PhoneSaveResult(PhoneSaveOutcome.ClientNotFound);
            if (conversationId is { } cv
                && !await db.Conversations.AsNoTracking().AnyAsync(c => c.Id == cv && c.ClientId == clientId, ct))
                return new PhoneSaveResult(PhoneSaveOutcome.ConversationMismatch);

            var now = clock.GetUtcNow();
            if (string.IsNullOrWhiteSpace(client.Phone))
            {
                DateTimeOffset? consentAt = consent ? now : null;
                await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Phone, e164).SetProperty(c => c.PhoneSource, source)
                    .SetProperty(c => c.CallConsentAt, consentAt).SetProperty(c => c.UpdatedAt, now), ct);
                var linked = await RecomputeDuplicateAsync(clientId, e164, ct);
                return new PhoneSaveResult(PhoneSaveOutcome.Saved, linked);
            }
            if (SameNumber(client.Phone, e164))
            {
                if (consent && client.CallConsentAt is null)
                    await db.Clients.Where(c => c.Id == clientId && c.CallConsentAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(c => c.CallConsentAt, now).SetProperty(c => c.UpdatedAt, now), ct);
                return new PhoneSaveResult(PhoneSaveOutcome.Unchanged);
            }

            await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.PhonePending, e164).SetProperty(c => c.PhonePendingSource, source)
                .SetProperty(c => c.PhonePendingAt, now).SetProperty(c => c.UpdatedAt, now), ct);
            if (conversationId is { } conv)
                await db.Conversations.Where(c => c.Id == conv)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.AwaitingStaffSince, c => c.AwaitingStaffSince ?? now), ct);
            return new PhoneSaveResult(PhoneSaveOutcome.PendingConflict);
        }, ct);

        log.LogInformation("client_phone_saved client={ClientId} result={Result} source={Source}", clientId, result.Outcome, source);
        return result;
    }

    /// <summary>Чи можна питати номер: телефон є / поза cooldown / у cooldown (<c>ChatPhone:CooldownDays</c>, за замовч. 30).</summary>
    public async Task<PhoneRequestState> GetRequestStateAsync(Guid tenantId, Guid clientId, CancellationToken ct = default)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var c = await db.Clients.AsNoTracking().Where(x => x.Id == clientId && x.DeletedAt == null)
            .Select(x => new { x.Phone, x.PhoneRequestedAt }).FirstOrDefaultAsync(ct);
        if (c is null) return PhoneRequestState.ClientNotFound;
        if (!string.IsNullOrWhiteSpace(c.Phone)) return PhoneRequestState.HasPhone;
        return c.PhoneRequestedAt is { } at && at > clock.GetUtcNow().AddDays(-CooldownDays) ? PhoneRequestState.Cooldown : PhoneRequestState.Allowed;
    }

    /// <summary>Фіксує показ питання (<c>phone_requested_at = now</c>, основа cooldown). false = клієнта немає в tenant.</summary>
    public async Task<bool> MarkPhoneRequestedAsync(Guid tenantId, Guid clientId, CancellationToken ct = default)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var now = clock.GetUtcNow();
        return await db.Clients.Where(c => c.Id == clientId && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PhoneRequestedAt, now).SetProperty(c => c.UpdatedAt, now), ct) > 0;
    }

    // ------------------------------------------------------------------ рішення адміністратора (§25.7)

    public async Task<Result<PhonePendingResolvedDto>> ResolvePendingAsync(Actor actor, Guid clientId, string? action, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, actor.TenantId);
        if (actor.Role is not (Roles.Owner or Roles.Admin))
            return Error.Forbidden(ClientPhoneErrors.ForbiddenRole, "This action is not available for your role.");
        var act = action?.Trim().ToLowerInvariant();
        if (act is not ("accept" or "reject"))
            return Error.Validation(ClientPhoneErrors.InvalidAction, "action must be accept or reject.");

        var result = await InTransactionAsync<Result<PhonePendingResolvedDto>>(actor.TenantId, clientId, async () =>
        {
            var c = await db.Clients.AsNoTracking().Where(x => x.Id == clientId && x.DeletedAt == null)
                .Select(x => new { x.Phone, x.PhoneSource, x.CallConsentAt, x.PhonePending, x.PhonePendingSource, x.PhonePendingAt })
                .FirstOrDefaultAsync(ct);
            if (c is null) return Error.NotFound(ClientPhoneErrors.ClientNotFound, "Client not found.");
            if (c.PhonePending is null || c.PhonePendingSource is null || c.PhonePendingAt is null)
                return Error.Conflict(ClientPhoneErrors.NoPendingPhone, "The client has no pending phone number.");

            var now = clock.GetUtcNow();
            if (act == "accept")
            {
                var pending = c.PhonePending;
                var src = c.PhonePendingSource;
                var at = c.PhonePendingAt;
                await db.Clients.Where(x => x.Id == clientId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Phone, pending).SetProperty(x => x.PhoneSource, src).SetProperty(x => x.CallConsentAt, at)
                    .SetProperty(x => x.PhonePending, (string?)null).SetProperty(x => x.PhonePendingSource, (string?)null)
                    .SetProperty(x => x.PhonePendingAt, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, now), ct);
                await RecomputeDuplicateAsync(clientId, pending, ct);
            }
            else
            {
                await db.Clients.Where(x => x.Id == clientId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.PhonePending, (string?)null).SetProperty(x => x.PhonePendingSource, (string?)null)
                    .SetProperty(x => x.PhonePendingAt, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, now), ct);
            }

            // Подія в Inbox: розмови клієнта знімаються з «потрібна відповідь», якщо причиною був лише номер
            // (AI активний і немає утримуваних вхідних). Розмови в handoff/з held залишаються.
            await db.Conversations.Where(x => x.ClientId == clientId && x.AwaitingStaffSince != null && x.AiState == "active"
                    && !db.Messages.Any(m => m.ConversationId == x.Id && m.Status == "held"))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AwaitingStaffSince, (DateTimeOffset?)null).SetProperty(x => x.UpdatedAt, now), ct);

            var after = await db.Clients.AsNoTracking().Where(x => x.Id == clientId)
                .Select(x => new PhonePendingResolvedDto(x.Phone, x.PhoneSource, x.CallConsentAt, null)).FirstAsync(ct);
            return after;
        }, ct);

        if (result.IsOk)
            log.LogInformation("client_phone_pending_resolved client={ClientId} user={UserId} action={Action}", clientId, actor.UserId, act);
        return result;
    }

    // ------------------------------------------------------------------ допоміжне

    /// <summary>Порівняння за цифрами зі спадковими форматами (§25.5.3).</summary>
    internal static bool SameNumber(string? a, string? b)
    {
        var left = PhoneNormalizer.MatchCandidates(a);
        return PhoneNormalizer.MatchCandidates(b).Any(left.Contains);
    }

    /// <summary>
    /// Рівно один ІНШИЙ активний клієнт з тими самими цифрами -> <c>duplicate_of_client_id</c> на нього (без злиття); 0 або ≥ 2 -> зв'язок
    /// знімається. Взаємного посилання не створюється (оригінал, який вже вказує на цього клієнта, не отримує зворотного).
    /// </summary>
    private async Task<bool> RecomputeDuplicateAsync(Guid clientId, string phone, CancellationToken ct)
    {
        var others = await ClientPhoneQueries.FindClientIdsAsync(db, phone, clientId, 2, ct);
        Guid? target = null;
        if (others.Count == 1)
        {
            var other = others[0];
            var points = await db.Clients.AsNoTracking().AnyAsync(c => c.Id == other && c.DuplicateOfClientId == clientId, ct);
            if (!points) target = other;
        }
        await db.Clients.Where(c => c.Id == clientId).ExecuteUpdateAsync(s => s.SetProperty(c => c.DuplicateOfClientId, target), ct);
        return target is not null;
    }
}

/// <summary>Пошук активних клієнтів за номером (за цифрами) з детермінованим пріоритетом оригіналу (§25.7).</summary>
internal static class ClientPhoneQueries
{
    /// <summary>
    /// Порядок: <c>duplicate_of_client_id IS NULL DESC, created_at, id</c> - оригінал, далі найстарший. <paramref name="excludeId"/> виключає
    /// самого клієнта. Порожній набір цифр -> порожній результат.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> FindClientIdsAsync(BeautyDbContext db, string? phone, Guid? excludeId, int limit, CancellationToken ct)
    {
        var candidates = PhoneNormalizer.MatchCandidates(phone).ToArray();
        if (candidates.Length == 0) return [];
        var hasExclude = excludeId is not null;
        var exclude = excludeId ?? Guid.Empty;
        return await db.Database.SqlQuery<Guid>($"""
            SELECT c.id AS "Value"
            FROM beauty_clients c
            WHERE c.deleted_at IS NULL AND c.phone IS NOT NULL
              AND regexp_replace(c.phone, '\D', '', 'g') = ANY({candidates})
              AND ({hasExclude} = false OR c.id <> {exclude})
            ORDER BY (c.duplicate_of_client_id IS NULL) DESC, c.created_at, c.id
            LIMIT {limit}
            """).ToListAsync(ct);
    }
}

public sealed partial class EfBeautyStore
{
    private Task<IReadOnlyList<Guid>> FindClientIdsByPhoneAsync(string phone, Guid? excludeId, int limit, CancellationToken ct) =>
        ClientPhoneQueries.FindClientIdsAsync(db, phone, excludeId, limit, ct);
}
