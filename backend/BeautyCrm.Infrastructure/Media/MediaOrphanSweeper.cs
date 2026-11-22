using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.Media;

/// <summary>
/// Очищення файлів-сиріт (ADR-027 §2.9, §33.22.7). Працює в API (том монтується лише тут) раз на 6 год, перший запуск через 10 хв, лише за
/// <c>Media:SweepMode</c> ≠ off. Список tenant-ів - з диска (теки t/*), «живі» ключі - з БД під RLS кожного tenant-а окремо.
/// Запобіжники: tenant невидимий або помилка = пропуск; &gt; 50% маркерів tenant-а (за &gt; 20) або &gt; 500 за запуск = нічого не видаляється;
/// grace 24 год (tmp - 1 год); файли з невалідними іменами не чіпаються.
/// </summary>
public sealed class MediaOrphanSweeper(IServiceScopeFactory scopes, FileProductImageStorage storage, MediaOptions options, TimeProvider clock,
    ILogger<MediaOrphanSweeper> log) : BackgroundService
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(24), StrayGrace = TimeSpan.FromDays(7), TmpGrace = TimeSpan.FromHours(1), Interval = TimeSpan.FromHours(6), FirstDelay = TimeSpan.FromMinutes(10);
    public const int MaxPerRun = 500, RatioMinMarkers = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.SweepMode == MediaSweepMode.Off || !options.Enabled) return;
        try
        {
            await Task.Delay(FirstDelay, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "media_sweep_aborted reason=exception"); }
                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed record TenantPlan(Guid Tenant, int Markers, List<string> Keys)
    {
        public int Deleted;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var mode = options.SweepMode;
        if (mode == MediaSweepMode.Off || !options.Enabled) return;

        await using var lockScope = scopes.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<BeautyDbContext>();
        await lockDb.Database.OpenConnectionAsync(ct);
        var locked = (await lockDb.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock(hashtext('media_sweep')) AS \"Value\"").ToListAsync(ct)).Single();
        if (!locked) return;
        try { await SweepAsync(mode, ct); }
        finally
        {
            // best effort: блокування сесії все одно знімається при поверненні з'єднання в пул (DISCARD ALL)
            try { await lockDb.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtext('media_sweep'))", CancellationToken.None); } catch { }
        }
    }

    private async Task SweepAsync(MediaSweepMode mode, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var plans = new List<TenantPlan>();
        var live = new HashSet<string>(StringComparer.Ordinal);

        // 1. кандидати-маркери по tenant-ах
        foreach (var tenant in storage.MarkerTenants())
        {
            try
            {
                var keys = await LiveKeysAsync(tenant, ct);
                if (keys is null) { log.LogWarning("media_sweep tenant={Tenant} skipped: tenant is not visible", tenant); continue; }
                live.UnionWith(keys);
                var markers = storage.Markers(tenant);
                var markerKeys = markers.Select(m => m.Key).ToHashSet();
                var missing = keys.Count(k => !markerKeys.Contains(k));
                if (missing > 0) log.LogWarning("media_missing_files tenant={Tenant} count={Count}", tenant, missing);
                var candidates = markers.Where(m => now - m.MtimeUtc > Grace && !keys.Contains(m.Key)).Select(m => m.Key).ToList();
                if (markers.Count > RatioMinMarkers && candidates.Count > markers.Count * 0.5)
                {
                    log.LogError("media_sweep_aborted reason=tenant_ratio tenant={Tenant} markers={Markers} candidates={Candidates}", tenant, markers.Count, candidates.Count);
                    continue;
                }
                plans.Add(new TenantPlan(tenant, markers.Count, candidates));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "media_sweep tenant={Tenant} skipped: error", tenant); // помилка БД/ФС = пропуск tenant-а
            }
        }

        // 2. файли без власника: *.tmp > 1 год; валідні варіанти без маркера (і не живі) > 24 год; решта не чіпається
        var strays = new List<FileInfo>();
        var variantStrays = new List<FileInfo>();
        var validVariants = 0;
        var allMarkers = new HashSet<string>();
        var invalid = 0;
        try
        {
            allMarkers = storage.AllMarkerKeys();
            foreach (var f in storage.VariantFiles())
            {
                if (f.Name.EndsWith(".tmp", StringComparison.Ordinal)) { if (now - f.LastWriteTimeUtc > TmpGrace) strays.Add(f); }
                else if (FileProductImageStorage.IsVariantName(f.Name, out var key))
                {
                    validVariants++;
                    if (!allMarkers.Contains(key) && !live.Contains(key) && now - f.LastWriteTimeUtc > StrayGrace) variantStrays.Add(f);
                }
                else invalid++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "media_sweep stray scan failed; stray files skipped");
            strays.Clear();
            variantStrays.Clear();
        }
        // fail-safe: неповний/відсутній t/ (часткове відновлення, ручне прибирання) не повинен робити «сиротами» всі живі файли
        string? abort = null;
        if (variantStrays.Count > 0)
        {
            if (!Directory.Exists(storage.MarkersRoot)) abort = "markers_root_missing";
            else if (allMarkers.Count == 0) abort = "no_markers";
            else if (validVariants > RatioMinMarkers && variantStrays.Count > validVariants * 0.5) abort = "stray_ratio";
        }
        if (abort is not null)
        {
            log.LogError("media_sweep_aborted reason={Reason} variants={Variants} strays={Strays}", abort, validVariants, variantStrays.Count);
            variantStrays.Clear();
        }
        strays.AddRange(variantStrays);
        if (invalid > 0) log.LogWarning("media_sweep found {Count} files with unexpected names (left untouched)", invalid);

        // 3. загальний запобіжник
        var total = plans.Sum(p => p.Keys.Count) + strays.Count;
        if (total > MaxPerRun)
        {
            log.LogError("media_sweep_aborted reason=run_limit candidates={Candidates}", total);
            return;
        }

        // 4. видалення (dry-run лише рахує)
        if (mode == MediaSweepMode.Delete)
        {
            foreach (var p in plans)
                foreach (var key in p.Keys)
                    if (storage.DeleteVariantFiles(key) && storage.DeleteMarker(p.Tenant, key)) p.Deleted++;
            foreach (var f in strays) storage.DeleteFile(f);
        }
        foreach (var p in plans)
            log.LogInformation("media_sweep tenant={Tenant} mode={Mode} markers={Markers} candidates={Candidates} deleted={Deleted}",
                p.Tenant, mode, p.Markers, p.Keys.Count, p.Deleted);
        if (strays.Count > 0) log.LogInformation("media_sweep strays mode={Mode} candidates={Candidates}", mode, strays.Count);
    }

    /// <summary>null = tenant невидимий (під RLS власного tenant його рядок не знайдено): нічого не видаляється.</summary>
    private async Task<HashSet<string>?> LiveKeysAsync(Guid tenant, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        var db = scope.ServiceProvider.GetRequiredService<BeautyDbContext>();
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenant, ct)) return null;
        var keys = (await db.ProductImages.AsNoTracking().Select(i => i.StorageKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // логотипи закладів живуть у тому самому сховищі (ключ у beauty_locations.logo_storage_key): без цього очищувач видалив би їх
        keys.UnionWith(await db.Locations.AsNoTracking().Where(l => l.LogoStorageKey != null).Select(l => l.LogoStorageKey!).ToListAsync(ct));
        return keys;
    }
}
