using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

// EF-реалізації портів ШІ (читання налаштувань/ключів + облік). Усе під RLS tenant (app.tenant_id виставляє інтерцептор);
// TenantGuard звіряє tenantId зі scope. Запис налаштувань/ключів (PUT/DELETE, аудит) — TASK-716.

public sealed class EfAiTenantSettingsStore(BeautyDbContext db, TenantContext tenant) : IAiTenantSettingsStore
{
    public async Task<AiSettingsRow?> GetAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return await db.AiSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
    }
}

public sealed class EfAiProviderKeyStore(BeautyDbContext db, TenantContext tenant) : IAiProviderKeyStore
{
    public async Task<AiProviderKey?> GetAsync(Guid tenantId, string provider, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return await db.AiProviderKeys.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Provider == provider, ct);
    }
}

public sealed class EfAiUsageStore(BeautyDbContext db, TenantContext tenant) : IAiUsageStore
{
    public async Task<AiUsageTotals> GetTotalsAsync(Guid tenantId, DateOnly period, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return await TotalsAsync(tenantId, period, ct);
    }

    private async Task<AiUsageTotals> TotalsAsync(Guid tenantId, DateOnly period, CancellationToken ct)
    {
        var q = db.AiUsage.AsNoTracking().Where(u => u.TenantId == tenantId && u.PeriodStart == period);
        var requests = await q.SumAsync(u => (long)u.RequestCount, ct);
        var tokens = await q.SumAsync(u => u.InputTokens + u.OutputTokens, ct);
        return new AiUsageTotals(requests, tokens);
    }

    public async Task<bool> TryReserveRequestAsync(
        Guid tenantId, DateOnly period, AiUsageKey key, long? requestLimit, long? tokenLimit, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Серіалізація per (tenant, місяць): перевірка сум і інкремент — одна критична секція (ліміт L => рівно L успіхів).
        var lockKey = $"beauty_ai_usage|{tenantId:D}|{period:yyyy-MM}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        var totals = await TotalsAsync(tenantId, period, ct);
        if ((requestLimit is { } rl && totals.Requests >= rl) || (tokenLimit is { } tl && totals.Tokens >= tl))
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        await UpsertAsync(tenantId, period, key, requests: 1, errors: 0, input: 0, output: 0, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public Task AddTokensAsync(Guid tenantId, DateOnly period, AiUsageKey key, long inputTokens, long outputTokens, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return UpsertAsync(tenantId, period, key, 0, 0, Math.Max(0, inputTokens), Math.Max(0, outputTokens), ct);
    }

    public Task AddErrorAsync(Guid tenantId, DateOnly period, AiUsageKey key, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return UpsertAsync(tenantId, period, key, 0, 1, 0, 0, ct);
    }

    /// <summary>INSERT … ON CONFLICT DO UPDATE SET x = x + excluded.x: атомарний інкремент без read-modify-write. Лише лічильники, без вмісту.</summary>
    private Task<int> UpsertAsync(Guid tenantId, DateOnly period, AiUsageKey k, int requests, int errors, long input, long output, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO beauty_ai_usage (tenant_id, period_start, role, provider, model, key_source, request_count, error_count, input_tokens, output_tokens)
            VALUES ({tenantId}, {period}, {k.Role}, {k.Provider}, {k.Model}, {k.KeySource}, {requests}, {errors}, {input}, {output})
            ON CONFLICT (tenant_id, period_start, role, provider, model, key_source) DO UPDATE SET
                request_count = beauty_ai_usage.request_count + EXCLUDED.request_count,
                error_count = beauty_ai_usage.error_count + EXCLUDED.error_count,
                input_tokens = beauty_ai_usage.input_tokens + EXCLUDED.input_tokens,
                output_tokens = beauty_ai_usage.output_tokens + EXCLUDED.output_tokens,
                updated_at = now()", ct);
}
