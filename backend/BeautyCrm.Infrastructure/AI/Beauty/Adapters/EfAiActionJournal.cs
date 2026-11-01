using System.Text.Json;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using DbAiStatus = BeautyCrm.Infrastructure.Data.Entities.AiActionStatus;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

/// <summary>Читання журналу для API (GET /ai/actions).</summary>
public interface IAiActionReader
{
    Task<IReadOnlyList<AiActionRecord>> ListAsync(int take, string? status, CancellationToken ct);
}

public static class TenantGuard
{
    /// <summary>
    /// Порти AI приймають tenantId явно, але tenant scope має бути вже встановлений автентифікацією (JWT middleware, канал
    /// webhook після перевірки підпису): він має збігатися із заданим. Порожній scope = помилка (L3): шлях, що не встановив
    /// tenant, не може обрати його сам із параметра. Платформні/воркерні шляхи використовують <see cref="EnsureOrSetForTrustedJob"/>.
    /// </summary>
    public static void Ensure(TenantContext tenant, Guid tenantId)
    {
        if (tenant.TenantId is { } current)
        {
            // TenantMismatchException (не InvalidOperationException): AiToolExecutor його не маскує як invalid_arguments, запуск переривається.
            if (current != tenantId) throw new TenantMismatchException(current, tenantId);
            return;
        }
        throw new TenantScopeMissingException();
    }

    /// <summary>
    /// Лише для довірених платформних/воркерних шляхів (джоби, CLI), де tenant береться з перевіреного джерела, а не із запиту.
    /// Порожній scope -> встановлює tenant; непорожній -> як <see cref="Ensure"/>. НЕ викликати з HTTP/webhook/асистента.
    /// </summary>
    public static void EnsureOrSetForTrustedJob(TenantContext tenant, Guid tenantId)
    {
        if (tenant.TenantId is null) { tenant.SetTenant(tenantId); return; }
        Ensure(tenant, tenantId);
    }
}

/// <summary>
/// beauty_ai_actions: {action -> tool_name, payload -> payload}. Поля, яких немає в схемі (target, status AI-шару,
/// revertible, mode), лежать у jsonb result як конверт; колонка status — грубий зріз для індексів.
/// </summary>
public sealed class EfAiActionJournal(BeautyDbContext db, TenantContext tenant) : IAiActionJournal, IAiActionReader
{
    private sealed record Envelope(string Target, string Status, bool Revertible, string Mode);

    public async Task AppendAsync(AiActionRecord r, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, r.TenantId);
        var e = new AiAction { Id = r.Id, CreatedAt = r.CreatedAt };
        Apply(e, r);
        db.AiActions.Add(e);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AiActionRecord?> GetAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var e = await db.AiActions.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return e is null ? null : ToRecord(e);
    }

    public async Task UpdateAsync(AiActionRecord r, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, r.TenantId);
        var e = await db.AiActions.FirstAsync(a => a.Id == r.Id, ct);
        Apply(e, r);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryTransitionAsync(Guid tenantId, Guid id, string from, string to, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var column = ColumnStatus(to) switch
        {
            DbAiStatus.Proposed => "proposed",
            DbAiStatus.Rejected => "rejected",
            _ => "executed",
        };
        // Умовний UPDATE по статусу в конверті result: рівно один із паралельних викликів отримає 1 рядок.
        var n = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE beauty_ai_actions SET result = jsonb_set(result, '{{Status}}', to_jsonb({to}::text)), status = {column}, updated_at = now() WHERE id = {id} AND result->>'Status' = {from}",
            ct);
        return n == 1;
    }

    public async Task<IReadOnlyList<AiActionRecord>> ListAsync(int take, string? status, CancellationToken ct)
    {
        // Службовий облік запитів LLM чату (chat_llm_usage) - не дія для менеджера.
        var rows = await db.AiActions.AsNoTracking().Where(a => a.ToolName != Conversations.ChatJournal.LlmUsage)
            .OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var list = rows.Select(ToRecord);
        return (status is null ? list : list.Where(r => r.Status == status)).ToList();
    }

    private static void Apply(AiAction e, AiActionRecord r)
    {
        e.ToolName = r.Action;
        if (r.ConversationId is not null) e.ConversationId = r.ConversationId;
        e.Payload = IsJson(r.PayloadJson) ? r.PayloadJson : JsonSerializer.Serialize(r.PayloadJson);
        e.Status = ColumnStatus(r.Status);
        e.Result = JsonSerializer.Serialize(new Envelope(r.Target, r.Status, r.Revertible, r.Mode.ToString()));
    }

    private static DbAiStatus ColumnStatus(string status) => status switch
    {
        Beauty.AiActionStatus.PendingConfirmation or Beauty.AiActionStatus.Drafted => DbAiStatus.Proposed,
        Beauty.AiActionStatus.Rejected => DbAiStatus.Rejected,
        _ => DbAiStatus.Executed,
    };

    private static AiActionRecord ToRecord(AiAction e)
    {
        var env = e.Result is null ? null : JsonSerializer.Deserialize<Envelope>(e.Result);
        var mode = env is not null && Enum.TryParse<AiMode>(env.Mode, out var m) ? m : AiMode.Confirm;
        return new AiActionRecord(e.Id, e.TenantId, e.ToolName, env?.Target ?? "-", e.CreatedAt,
            env?.Status ?? Beauty.AiActionStatus.Done, env?.Revertible ?? false, mode, e.Payload, e.ConversationId);
    }

    private static bool IsJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }
}
