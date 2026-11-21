using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Media;
using BeautyCrm.Tests.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BeautyCrm.Tests.PublicBooking;
using System.Collections.Concurrent;

namespace BeautyCrm.Tests.Beauty;

/// <summary>ADR-027 §2.9, §33.22.9 п.6: очищувач сиріт на реальній БД (tenant-и з диска, живі ключі під RLS).</summary>
public sealed class MediaOrphanSweeperTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<string> _dirs = [];
    public void Dispose() { foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { } }

    private sealed record Rig(string Dir, FileProductImageStorage Storage, MediaOrphanSweeper Sweeper, ConcurrentQueue<string> Logs, MediaOptions Options);

    private Rig NewRig(MediaSweepMode mode)
    {
        var dir = Path.Combine(Path.GetTempPath(), "beauty-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var options = new MediaOptions { RootPath = dir, PublicBaseUrl = "https://x.test", SweepMode = mode };
        var logs = new ConcurrentQueue<string>();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = fx.Db.AppConnectionString }).Build();
        var sp = new ServiceCollection().AddSingleton<IConfiguration>(cfg).AddLogging().AddBeautyData().BuildServiceProvider();
        var storage = new FileProductImageStorage(options);
        var sweeper = new MediaOrphanSweeper(sp.GetRequiredService<IServiceScopeFactory>(), storage, options, TimeProvider.System,
            new LoggerFactory([new CapturingLogProvider(logs)]).CreateLogger<MediaOrphanSweeper>());
        return new Rig(dir, storage, sweeper, logs, options);
    }

    private static string Key() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    private static readonly ImageVariantData[] Variants = [new(400, 1, 1, [1, 2, 3]), new(1600, 1, 1, [1, 2, 3])];

    /// <summary>Пише маркер і варіанти ключа, mtime усіх файлів ключа - age тому.</summary>
    private static async Task<string> PutAsync(Rig r, Guid tenant, TimeSpan age, string? key = null)
    {
        key ??= Key();
        Assert.True(await r.Storage.TryWriteAsync(tenant, key, Variants, default));
        var t = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(Path.Combine(r.Dir, "t", tenant.ToString("D"), key), t);
        foreach (var f in Directory.GetFiles(Path.Combine(r.Dir, "p", key[..2]), key + "*")) File.SetLastWriteTimeUtc(f, t);
        return key;
    }

    private static bool Exists(Rig r, Guid tenant, string key) =>
        File.Exists(Path.Combine(r.Dir, "t", tenant.ToString("D"), key)) || File.Exists(Path.Combine(r.Dir, "p", key[..2], key + "-400.webp"));

    private async Task<(AuthApiFixture.TenantSeed T, Guid Product)> TenantWithProductAsync()
    {
        var t = await fx.CreateTenantAsync();
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        var p = new Product { Name = "P", Unit = ProductUnit.Pcs, Purpose = ProductPurpose.Sale, SalePrice = 1, UpdatedByUserId = t.OwnerId };
        ctx.Products.Add(p);
        await ctx.SaveChangesAsync();
        return (t, p.Id);
    }

    private async Task AddRowAsync(AuthApiFixture.TenantSeed t, Guid product, string key)
    {
        await using var ctx = fx.Db.CreateContext(t.TenantId);
        ctx.ProductImages.Add(new ProductImage { ProductId = product, StorageKey = key, SizeBytes = 6, Width = 1, Height = 1, SortOrder = (short)Random.Shared.Next(0, 90), CreatedByUserId = t.OwnerId });
        await ctx.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Off_and_dry_run_delete_nothing_delete_removes_only_old_orphans()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (t, product) = await TenantWithProductAsync();
        foreach (var mode in new[] { MediaSweepMode.Off, MediaSweepMode.DryRun })
        {
            var r0 = NewRig(mode);
            var o = await PutAsync(r0, t.TenantId, TimeSpan.FromHours(30));
            await r0.Sweeper.RunOnceAsync(default);
            Assert.True(Exists(r0, t.TenantId, o), mode.ToString());
        }

        var r = NewRig(MediaSweepMode.Delete);
        var oldOrphan = await PutAsync(r, t.TenantId, TimeSpan.FromHours(30));
        var youngOrphan = await PutAsync(r, t.TenantId, TimeSpan.FromHours(2));
        var live = await PutAsync(r, t.TenantId, TimeSpan.FromHours(30));
        await AddRowAsync(t, product, live);
        var tmpOld = Path.Combine(r.Dir, "p", "ab", "x.webp.tmp"); Directory.CreateDirectory(Path.GetDirectoryName(tmpOld)!);
        File.WriteAllText(tmpOld, "x"); File.SetLastWriteTimeUtc(tmpOld, DateTime.UtcNow.AddHours(-2));
        var tmpNew = Path.Combine(r.Dir, "p", "ab", "y.webp.tmp"); File.WriteAllText(tmpNew, "x");
        var odd = Path.Combine(r.Dir, "p", "ab", "README.txt"); File.WriteAllText(odd, "x"); File.SetLastWriteTimeUtc(odd, DateTime.UtcNow.AddDays(-5));
        var markerless = Key(); Directory.CreateDirectory(Path.Combine(r.Dir, "p", markerless[..2]));
        var ml = Path.Combine(r.Dir, "p", markerless[..2], markerless + "-400.webp"); File.WriteAllText(ml, "x"); File.SetLastWriteTimeUtc(ml, DateTime.UtcNow.AddDays(-8)); // grace strays 7 днів

        await r.Sweeper.RunOnceAsync(default);
        Assert.False(Exists(r, t.TenantId, oldOrphan));
        Assert.True(Exists(r, t.TenantId, youngOrphan));
        Assert.True(Exists(r, t.TenantId, live));
        Assert.False(File.Exists(tmpOld));
        Assert.True(File.Exists(tmpNew));
        Assert.True(File.Exists(odd)); // невалідне ім'я не чіпається
        Assert.False(File.Exists(ml));
    }

    [SkippableFact]
    public async Task Invisible_tenant_and_empty_live_set_with_many_markers_delete_nothing_and_missing_files_are_logged()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var r = NewRig(MediaSweepMode.Delete);
        var ghost = Guid.NewGuid(); // немає в БД: невидимий під RLS
        var g = await PutAsync(r, ghost, TimeSpan.FromHours(30));
        var (t, product) = await TenantWithProductAsync();
        var keys = new List<string>();
        for (var i = 0; i < 25; i++) keys.Add(await PutAsync(r, t.TenantId, TimeSpan.FromHours(30))); // 100% кандидатів при > 20 маркерів
        await r.Sweeper.RunOnceAsync(default);
        Assert.True(Exists(r, ghost, g));
        Assert.All(keys, k => Assert.True(Exists(r, t.TenantId, k)));
        Assert.Contains(r.Logs, l => l.Contains("media_sweep_aborted"));

        await AddRowAsync(t, product, Key()); // рядок без файлів
        await r.Sweeper.RunOnceAsync(default);
        Assert.Contains(r.Logs, l => l.Contains("media_missing_files"));
    }

    private static string PutStray(Rig r, TimeSpan age)
    {
        var k = Key();
        Directory.CreateDirectory(Path.Combine(r.Dir, "p", k[..2]));
        var f = Path.Combine(r.Dir, "p", k[..2], k + "-400.webp");
        File.WriteAllText(f, "x"); File.SetLastWriteTimeUtc(f, DateTime.UtcNow - age);
        return f;
    }

    [SkippableFact]
    public async Task Strays_younger_than_7_days_are_kept()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (t, _) = await TenantWithProductAsync();
        var r = NewRig(MediaSweepMode.Delete);
        await PutAsync(r, t.TenantId, TimeSpan.FromHours(1)); // t/ існує, маркери є
        var young = PutStray(r, TimeSpan.FromDays(3));
        await r.Sweeper.RunOnceAsync(default);
        Assert.True(File.Exists(young));
    }

    [SkippableFact]
    public async Task Missing_markers_dir_skips_strays_phase_and_logs_error()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var r = NewRig(MediaSweepMode.Delete);
        var f = PutStray(r, TimeSpan.FromDays(30));
        await r.Sweeper.RunOnceAsync(default);
        Assert.True(File.Exists(f));
        Assert.Contains(r.Logs, l => l.Contains("media_sweep_aborted") && l.Contains("markers_root_missing"));
    }

    [SkippableFact]
    public async Task Zero_markers_with_existing_variants_skips_strays_phase()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var r = NewRig(MediaSweepMode.Delete);
        Directory.CreateDirectory(Path.Combine(r.Dir, "t", Guid.NewGuid().ToString("D"))); // t/ є, маркерів немає
        var f = PutStray(r, TimeSpan.FromDays(30));
        await r.Sweeper.RunOnceAsync(default);
        Assert.True(File.Exists(f));
        Assert.Contains(r.Logs, l => l.Contains("media_sweep_aborted") && l.Contains("no_markers"));
    }

    [SkippableFact]
    public async Task Strays_exceeding_half_of_variants_are_not_deleted()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var (t, product) = await TenantWithProductAsync();
        var r = NewRig(MediaSweepMode.Delete);
        var live = await PutAsync(r, t.TenantId, TimeSpan.FromDays(30));
        await AddRowAsync(t, product, live);
        var strays = Enumerable.Range(0, 25).Select(_ => PutStray(r, TimeSpan.FromDays(30))).ToList();
        await r.Sweeper.RunOnceAsync(default);
        Assert.All(strays, f => Assert.True(File.Exists(f)));
        Assert.Contains(r.Logs, l => l.Contains("media_sweep_aborted") && l.Contains("stray_ratio"));
    }

    [SkippableFact]
    public async Task Run_limit_over_500_candidates_aborts_without_any_deletion()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        var r = NewRig(MediaSweepMode.Delete);
        var all = new List<(Guid, string)>();
        for (var n = 0; n < 2; n++)
        {
            var (t, _) = await TenantWithProductAsync();
            for (var i = 0; i < 1000; i++) // 300 старих (30%) + 700 молодих: нижче порога tenant-а, разом 600 > 500
                all.Add((t.TenantId, await PutAsync(r, t.TenantId, i < 300 ? TimeSpan.FromHours(30) : TimeSpan.FromHours(1))));
        }
        await r.Sweeper.RunOnceAsync(default);
        Assert.All(all, x => Assert.True(Exists(r, x.Item1, x.Item2)));
        Assert.Contains(r.Logs, l => l.Contains("media_sweep_aborted") && l.Contains("run_limit"));
    }
}
