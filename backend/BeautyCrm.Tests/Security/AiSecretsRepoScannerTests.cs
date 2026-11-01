using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeautyCrm.Tests.Security;

/// <summary>
/// TASK-714: сканер репозиторію. Значення <c>Ai:Secrets:Keys</c> (майстер-ключі) і схожі на ключі провайдерів
/// (<c>sk-ant-…</c>, <c>sk-…</c>) не мають потрапляти в tracked/нові незігноровані файли й appsettings.
/// Тест-файли виключено з вмісту сканування (фікстури будують синтетичні значення в рантаймі).
/// </summary>
public class AiSecretsRepoScannerTests
{
    private static readonly Regex ProviderKeyLike = new(@"\bsk-(?:ant-)?[A-Za-z0-9_\-]{20,}", RegexOptions.Compiled);
    // Ai__Secrets__Keys__{kid}=value  /  Ai:Secrets:Keys:{kid}: value
    private static readonly Regex EnvStyle = new(
        @"Ai(?:__|:)Secrets(?:__|:)Keys(?:__|:)(?<kid>[A-Za-z0-9_\-]+)\s*[=:]\s*(?<v>[^\s#]*)", RegexOptions.Compiled);
    private static readonly string[] ConfigLike = [".json", ".yml", ".yaml", ".config", ".toml", ".ini", ".properties"];

    /// <summary>Повертає опис проблем без самих значень.</summary>
    internal static List<string> ScanContent(string path, string text)
    {
        var problems = new List<string>();
        var name = Path.GetFileName(path);
        var isConfig = name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                       || ConfigLike.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        if (isConfig && ProviderKeyLike.IsMatch(text))
            problems.Add($"{path}: provider-key-like value (sk-…) in config file");

        foreach (Match m in EnvStyle.Matches(text))
        {
            var v = m.Groups["v"].Value.Trim('"', '\'', ',');
            if (IsPlaceholder(v)) continue;
            problems.Add($"{path}: non-empty value for Ai:Secrets:Keys:{m.Groups["kid"].Value}");
        }

        if (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            problems.AddRange(ScanAppSettings(path, text));
        return problems;
    }

    private static List<string> ScanAppSettings(string path, string text)
    {
        var result = new List<string>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { result.Add($"{path}: appsettings is not parseable JSON"); return result; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            // Секція Ai:Secrets:Keys (будь-який регістр) не повинна мати непорожніх значень.
            if (TryGet(doc.RootElement, "Ai", out var ai) && TryGet(ai, "Secrets", out var secrets)
                && TryGet(secrets, "Keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in keys.EnumerateObject())
                {
                    var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                    if (!IsPlaceholder(v)) result.Add($"{path}: non-empty value for Ai:Secrets:Keys:{p.Name}");
                }
            }
            // Плоский ключ "Ai:Secrets:Keys:kid": "value"
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.StartsWith("Ai:Secrets:Keys:", StringComparison.OrdinalIgnoreCase) && !IsPlaceholder(p.Value.ToString()))
                    result.Add($"{path}: non-empty value for {p.Name}");
        }
        return result;
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    // Порожнє значення або явний плейсхолдер у кутових дужках / {…} / ${…} / REPLACE_… дозволені.
    private static bool IsPlaceholder(string? v) =>
        string.IsNullOrWhiteSpace(v) || v.StartsWith('<') || v.StartsWith('{') || v.StartsWith("${", StringComparison.Ordinal)
        || v.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);

    // ---- збір файлів репозиторію ----

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, "BEAUTY_CRM_PROMPT.md")))
                return dir.FullName;
        return null;
    }

    private static List<string> ListFiles(string root)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "ls-files --cached --others --exclude-standard -z")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode == 0 && output.Length > 0)
                return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }

        // Без git: обхід каталогу без артефактів збірки.
        string[] skip = ["node_modules", ".git", "bin", "obj", ".next", "dist", "build", "out", "coverage"];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f))
            .Where(f => !f.Split(Path.DirectorySeparatorChar, '/').Any(s => skip.Contains(s)))
            .ToList();
    }

    private static bool IsExcluded(string rel)
    {
        var p = rel.Replace('\\', '/');
        return p.StartsWith("backend/BeautyCrm.Tests/", StringComparison.Ordinal) // фікстури: синтетичні значення
               || p.EndsWith("package-lock.json", StringComparison.Ordinal)
               || p.Contains("/node_modules/", StringComparison.Ordinal);
    }

    [Fact]
    public void repository_has_no_ai_master_keys_or_provider_key_like_values_in_files()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);
        var files = ListFiles(root!).Where(f => !IsExcluded(f)).ToList();
        Assert.NotEmpty(files);
        // Сканер дійсно бачить конфіги (захист від мовчазного «нічого не перевірено»).
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => Path.GetFileName(f) == ".env.example");

        var problems = new List<string>();
        foreach (var rel in files)
        {
            var full = Path.Combine(root!, rel);
            if (!File.Exists(full) || new FileInfo(full).Length > 2_000_000) continue;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(full); } catch (IOException) { continue; }
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 4096)) >= 0) continue; // бінарний
            problems.AddRange(ScanContent(rel, Encoding.UTF8.GetString(bytes)));
        }

        Assert.True(problems.Count == 0, "Secret-like values found in repository files:\n" + string.Join("\n", problems));
    }

    // ---- самоперевірка детектора (значення збираються з фрагментів, у файлі їх немає) ----

    private static string Fake(string prefix) => prefix + new string('a', 12) + new string('B', 12);

    [Fact]
    public void detector_flags_provider_key_like_values_in_config_files()
    {
        var anthropic = Fake("sk-" + "ant-");
        var openai = Fake("sk-");
        Assert.NotEmpty(ScanContent(".env.local.example", $"X={anthropic}"));
        Assert.NotEmpty(ScanContent("backend/Api/appsettings.json", "{\"Any\":{\"Value\":\"" + openai + "\"}}"));
        Assert.NotEmpty(ScanContent("deploy/values.yaml", $"key: {anthropic}"));
    }

    [Fact]
    public void detector_flags_nonempty_master_key_values_in_env_style_and_appsettings()
    {
        var b64 = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 7)).ToArray());
        Assert.NotEmpty(ScanContent(".env", $"Ai__Secrets__Keys__k1={b64}"));
        Assert.NotEmpty(ScanContent("docker-compose.yml", $"  Ai__Secrets__Keys__k1: \"{b64}\""));
        Assert.NotEmpty(ScanContent("appsettings.json", "{\"Ai\":{\"Secrets\":{\"Keys\":{\"k1\":\"" + b64 + "\"}}}}"));
        Assert.NotEmpty(ScanContent("appsettings.Production.json", "{\"ai\":{\"secrets\":{\"keys\":{\"k1\":\"" + b64 + "\"}}}}"));
        Assert.NotEmpty(ScanContent("appsettings.json", "{\"Ai:Secrets:Keys:k1\":\"" + b64 + "\"}"));
    }

    [Fact]
    public void detector_allows_empty_values_placeholders_and_docs_notation()
    {
        Assert.Empty(ScanContent(".env.example", "Ai__Secrets__ActiveKeyId=\nAi__Secrets__Keys__REPLACE_WITH_KID=\n"));
        Assert.Empty(ScanContent(".env.example", "Ai__Secrets__Keys__k1=<base64-32-bytes>"));
        Assert.Empty(ScanContent("docs/x.md", "env `Ai__Secrets__Keys__{kid}` and Ai:Secrets:Keys:{kid}"));
        Assert.Empty(ScanContent("appsettings.json", "{\"Ai\":{\"Secrets\":{\"Keys\":{}}},\"Logging\":{}}"));
        Assert.Empty(ScanContent("appsettings.json", "{\"Ai\":{\"Secrets\":{\"Keys\":{\"k1\":\"\"}}}}"));
        Assert.Empty(ScanContent("README.md", "use " + Fake("sk-") + " as an example")); // не конфіг-файл
    }
}
