using System.Text.RegularExpressions;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Спільні правила tenant для /api/platform/tenants і /api/control/v1/tenants (ADR-018: одна доменна операція,
/// без копії логіки): slug і перелік модулів.
/// </summary>
public static partial class TenantRules
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$")]
    private static partial Regex SlugPattern();

    /// <summary>Trim + lower; null, якщо slug не відповідає формату (3-64 символи, a-z, 0-9, дефіс).</summary>
    public static string? NormalizeSlug(string? slug)
    {
        var s = slug?.Trim().ToLowerInvariant();
        return s is not null && SlugPattern().IsMatch(s) ? s : null;
    }

    /// <summary>Trim + lower + без дублікатів; null = список відсутній або містить невідомий модуль.</summary>
    public static IReadOnlyList<string>? NormalizeModules(IReadOnlyList<string>? modules)
    {
        if (modules is null) return null;
        var list = modules.Select(m => (m ?? "").Trim().ToLowerInvariant()).Distinct().ToList();
        return list.All(m => BeautyModules.Known.Contains(m)) ? list : null;
    }
}
