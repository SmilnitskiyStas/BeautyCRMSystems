using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyProductImages;

namespace BeautyCrm.Infrastructure.Media;

/// <summary>
/// Файлове сховище зображень товарів (ADR-027 §2.2): {root}/p/{key[0..1]}/{key}-{size}.webp і маркер власника {root}/t/{tenantId}/{key}.
/// Шлях будується лише з валідного ключа (32 hex) і Guid tenant-а; ім'я від клієнта сюди не потрапляє. Оригінал не зберігається.
/// </summary>
public sealed class FileProductImageStorage(MediaOptions options) : IProductImageStorage
{
    private static readonly Regex FileName = new(@"^(?<key>[0-9a-f]{32})-(?<size>400|1600)\.webp$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex KeyName = new(@"^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly int[] Sizes = [MediaOptions.ThumbSize, MediaOptions.FullSize];

    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute; // 0750
    private const UnixFileMode FileMode_ = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead; // 0640

    public static bool IsKey(string? s) => s is { Length: 32 } && KeyName.IsMatch(s);
    public static bool IsVariantName(string name, out string key)
    {
        var m = FileName.Match(name);
        key = m.Success ? m.Groups["key"].Value : "";
        return m.Success;
    }

    // ---------- шляхи ----------

    internal string Root => Path.GetFullPath(options.RootPath ?? throw new InvalidOperationException("Media:RootPath is not configured."));
    internal string VariantsRoot => Path.Combine(Root, "p");
    internal string MarkersRoot => Path.Combine(Root, "t");
    private string Dir(string key) => Path.Combine(VariantsRoot, key[..2]);
    private string Variant(string key, int size) => Path.Combine(Dir(key), $"{key}-{size}.webp");
    private string Marker(Guid tenantId, string key) => Path.Combine(MarkersRoot, tenantId.ToString("D"), key);

    private static void MakeDir(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, DirMode);
    }

    private static FileStream Create(string path)
    {
        var o = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) o.UnixCreateMode = FileMode_;
        return new FileStream(path, o);
    }

    // ---------- роздача ----------

    /// <summary>
    /// Повний шлях існуючого варіанта або null (невалідне ім'я, вихід за root, symlink, файлу немає). Шлях збирається лише з груп regex.
    /// </summary>
    public string? ResolveVariant(string? file, out string? etag)
    {
        etag = null;
        if (!options.Enabled || file is null || file.Length > 64) return null;
        Match m;
        try { m = FileName.Match(file); } catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;
        var key = m.Groups["key"].Value;
        var full = Path.GetFullPath(Path.Combine(VariantsRoot, key[..2], $"{key}-{m.Groups["size"].Value}.webp"));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        var info = new FileInfo(full);
        if (!info.Exists || info.LinkTarget is not null || new DirectoryInfo(Dir(key)).LinkTarget is not null) return null;
        etag = $"\"{key}-{m.Groups["size"].Value}\"";
        return full;
    }

    // ---------- запис / видалення ----------

    public async Task<bool> TryWriteAsync(Guid tenantId, string key, IReadOnlyList<ImageVariantData> variants, CancellationToken ct)
    {
        if (!IsKey(key)) throw new ArgumentException("Invalid storage key.", nameof(key));
        var marker = Marker(tenantId, key);
        if (File.Exists(marker) || variants.Any(v => File.Exists(Variant(key, v.Size)))) return false;

        var created = new List<string>();
        try
        {
            MakeDir(Path.GetDirectoryName(marker)!);
            await using (Create(marker)) { }
            created.Add(marker);
            MakeDir(Dir(key));
            foreach (var v in variants)
            {
                var final = Variant(key, v.Size);
                var tmp = final + ".tmp";
                created.Add(tmp);
                await using (var fs = Create(tmp))
                {
                    await fs.WriteAsync(v.Bytes, ct);
                    fs.Flush(true);
                }
                File.Move(tmp, final, overwrite: false);
                created[^1] = final;
            }
            return true;
        }
        catch
        {
            foreach (var p in created) TryDelete(p);
            throw;
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool Delete(Guid tenantId, string key)
    {
        if (!IsKey(key)) return false;
        var ok = true;
        foreach (var s in Sizes) ok &= TryDelete(Variant(key, s)) & TryDelete(Variant(key, s) + ".tmp");
        return TryDelete(Marker(tenantId, key)) & ok;
    }

    // ---------- для очищувача ----------

    public IReadOnlyList<Guid> MarkerTenants() =>
        Directory.Exists(MarkersRoot)
            ? [.. Directory.EnumerateDirectories(MarkersRoot).Select(d => Path.GetFileName(d)).Where(n => Guid.TryParseExact(n, "D", out _)).Select(Guid.Parse)]
            : [];

    /// <summary>Маркери tenant-а з валідним ім'ям (ключем) і mtime.</summary>
    public IReadOnlyList<(string Key, DateTime MtimeUtc)> Markers(Guid tenantId)
    {
        var dir = Path.Combine(MarkersRoot, tenantId.ToString("D"));
        return Directory.Exists(dir)
            ? [.. new DirectoryInfo(dir).EnumerateFiles().Where(f => IsKey(f.Name)).Select(f => (f.Name, f.LastWriteTimeUtc))]
            : [];
    }

    /// <summary>Ключі всіх маркерів усіх tenant-ів (будь-яка тека в t/).</summary>
    public HashSet<string> AllMarkerKeys() =>
        Directory.Exists(MarkersRoot)
            ? [.. new DirectoryInfo(MarkersRoot).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => IsKey(f.Name)).Select(f => f.Name)]
            : [];

    /// <summary>Файли p/** (повний лістинг).</summary>
    public IEnumerable<FileInfo> VariantFiles() =>
        Directory.Exists(VariantsRoot) ? new DirectoryInfo(VariantsRoot).EnumerateFiles("*", SearchOption.AllDirectories) : [];

    public bool DeleteVariantFiles(string key)
    {
        var ok = true;
        foreach (var s in Sizes) ok &= TryDelete(Variant(key, s));
        return ok;
    }

    public bool DeleteMarker(Guid tenantId, string key) => TryDelete(Marker(tenantId, key));
    public bool DeleteFile(FileInfo f) => TryDelete(f.FullName);
}
