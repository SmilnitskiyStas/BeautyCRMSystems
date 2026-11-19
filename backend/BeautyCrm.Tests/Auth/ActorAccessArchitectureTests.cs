using System.Reflection;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Tests.Auth;

/// <summary>
/// TASK-850, B2 (контракт §30.5 «Правила для Application», §30.13 п.3): <c>Actor.Access</c> обов'язковий і ніколи не береться з пресету
/// в production-коді. Перевірки: сигнатура Actor (без дефолту), єдине місце конструювання в Api, джерела <c>PermissionPresets.For</c>.
/// </summary>
public sealed class ActorAccessArchitectureTests
{
    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BeautyCrm.Api"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("backend root not found");
    }

    private static IEnumerable<(string Path, string Text)> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(BackendRoot(), project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void Actor_constructor_requires_Access_without_default()
    {
        foreach (var ctor in typeof(Actor).GetConstructors())
        {
            var p = ctor.GetParameters().SingleOrDefault(x => x.ParameterType == typeof(AccessSet));
            Assert.NotNull(p);
            Assert.False(p!.HasDefaultValue, "Actor.Access must not have a default value (B2).");
        }
        Assert.Empty(typeof(Actor).GetConstructors().Where(c => c.GetParameters().Length < 5));
    }

    [Fact]
    public void Actor_ForTest_is_not_public_production_api()
    {
        var m = typeof(Actor).GetMethod("ForTest", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(m);
        Assert.False(m!.IsPublic);
    }

    [Fact]
    public void Api_constructs_Actor_only_in_ToActor_with_access_from_context()
    {
        var sites = Sources("BeautyCrm.Api")
            .SelectMany(s => Regex.Matches(s.Text, @"new Actor\s*\(").Select(_ => s.Path))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Equal(["AuthSetup.cs"], sites);

        var setup = Sources("BeautyCrm.Api").Single(s => Path.GetFileName(s.Path) == "AuthSetup.cs").Text;
        Assert.Matches(@"new Actor\(userId, tenantId, role!, specialistId, access\)", setup);
        Assert.Contains("AccessSet.None", setup);
        Assert.DoesNotContain("PermissionPresets", setup);
        Assert.DoesNotContain("Actor.ForTest", setup);
    }

    [Fact]
    public void Api_never_overrides_Access_with_a_with_expression()
    {
        foreach (var (path, text) in Sources("BeautyCrm.Api"))
            Assert.False(Regex.IsMatch(text, @"\bwith\s*\{[^}]*\bAccess\s*="), $"{path}: Actor.Access must not be replaced in Api.");
    }

    [Fact]
    public void Presets_are_not_a_source_of_Actor_Access_in_production_code()
    {
        // дозволено: каталог/пресети, фабрика Actor.ForTest (internal), AccessAdminService (запис пресету), AuthService (AcceptInvite),
        // міграційний/backfill-код. TASK-851 додає AccessAdminService.cs (вже в переліку).
        string[] allowed = ["PermissionCatalog.cs", "AuthModels.cs", "AccessAdminService.cs", "AccessAdminModels.cs", "AuthService.cs"];
        var offenders = new List<string>();
        foreach (var project in new[] { "BeautyCrm.Api", "BeautyCrm.Application", "BeautyCrm.Infrastructure" })
            foreach (var (path, text) in Sources(project))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")) continue;
                if (!Regex.IsMatch(text, @"PermissionPresets\s*\.\s*For\s*\(")) continue;
                if (!allowed.Contains(Path.GetFileName(path))) offenders.Add(path);
            }
        Assert.True(offenders.Count == 0, "PermissionPresets.For поза дозволеними місцями: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Production_actor_never_uses_preset_by_role_default()
    {
        var record = Sources("BeautyCrm.Application").Single(s => Path.GetFileName(s.Path) == "AuthModels.cs").Text;
        // єдине входження PermissionPresets.For у AuthModels - тестова фабрика internal
        var matches = Regex.Matches(record, @"PermissionPresets\s*\.\s*For\s*\(").Count;
        Assert.Equal(1, matches);
        Assert.Contains("internal static Actor ForTest", record);
        Assert.True(record.IndexOf("PermissionPresets", StringComparison.Ordinal) > record.IndexOf("internal static Actor ForTest", StringComparison.Ordinal));
    }
}
