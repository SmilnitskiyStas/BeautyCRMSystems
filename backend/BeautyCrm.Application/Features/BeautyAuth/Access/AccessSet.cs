using System.Security.Cryptography;
using System.Text;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Ефективні права користувача на запит (§30.4). Незмінний. Обчислюється один раз middleware:
/// <c>effective = модулі як верхня межа → інваріанти каталогу (roleCeiling, lte, requires)</c>. Fail-closed: відсутній рядок,
/// невідомий ключ чи рівень, невідома роль дають найнижчий рівень. Порушення обмежень знижує ЗАЛЕЖНИЙ ключ (у БД нічого не пишеться).
/// </summary>
public sealed class AccessSet
{
    private readonly IReadOnlyDictionary<string, string> _effective;
    private readonly IReadOnlyDictionary<string, string> _withoutModules;
    private readonly IReadOnlyDictionary<string, string> _granted;
    private readonly HashSet<string> _enabledModules;

    private AccessSet(string? role, IReadOnlyDictionary<string, string> granted, IReadOnlyDictionary<string, string> withoutModules,
        IReadOnlyDictionary<string, string> effective, bool specialistInactive, HashSet<string> enabledModules)
    {
        _enabledModules = enabledModules;
        Role = role;
        _granted = granted;
        _withoutModules = withoutModules;
        _effective = effective;
        SpecialistInactive = specialistInactive;
    }

    /// <summary>Усі ключі найнижчі, <c>own</c> порожній. Значення за відсутності контексту (middleware не відпрацював, фоновий код).</summary>
    public static AccessSet None { get; } = Build(null, null, [], false);

    public string? Role { get; }

    /// <summary>Прив'язаний профіль майстра неактивний (M7): усі <c>own</c>-вибірки порожні, tenant-ендпоінти дають 403 specialist_inactive.</summary>
    public bool SpecialistInactive { get; }

    /// <summary>Усі ключі каталогу з ефективними рівнями (віддається в <c>/auth/me</c>).</summary>
    public IReadOnlyDictionary<string, string> Effective => _effective;

    /// <summary>Видані рівні з БД після валідації (без модулів і обмежень); для owner — максимум.</summary>
    public IReadOnlyDictionary<string, string> Granted => _granted;

    /// <summary>Ефективний рівень; невідомий ключ — порожній рядок (fail-closed, <see cref="AtLeast"/> дає false).</summary>
    public string Level(string key) => _effective.TryGetValue(key, out var l) ? l : string.Empty;

    /// <summary>true, якщо індекс ефективного рівня ≥ індексу <paramref name="level"/>. Невідомі ключ/рівень дають false.</summary>
    public bool AtLeast(string key, string level)
    {
        var def = PermissionCatalog.Find(key);
        if (def is null) return false;
        var need = def.IndexOf(level);
        return need >= 0 && def.IndexOf(Level(key)) >= need;
    }

    /// <summary>
    /// true, якщо рівня немає ЛИШЕ через вимкнений модуль (видане право вистачило б, §30.5 п.8): тоді відповідь 403 module_disabled,
    /// а не forbidden_role. Враховує й випадок, коли залежний ключ знижено вимкненням модуля ключа-передумови.
    /// </summary>
    public bool BlockedByModule(string key, string level)
    {
        var def = PermissionCatalog.Find(key);
        if (def is null) return false;
        var need = def.IndexOf(level);
        return need >= 0 && def.IndexOf(Level(key)) < need
            && _withoutModules.TryGetValue(key, out var l) && def.IndexOf(l) >= need;
    }

    /// <summary>Ключі, чиє видане право вище за ефективне лише через вимкнені модулі (<c>moduleDisabled[]</c>, §30.9.3).</summary>
    public IReadOnlyList<string> ModuleDisabledKeys() =>
    [
        .. PermissionCatalog.All.Where(d => d.IndexOf(_withoutModules[d.Key]) > d.IndexOf(_effective[d.Key])).Select(d => d.Key),
    ];

    /// <summary>
    /// Стеля актора для видачі прав (§30.10 п.11): рівень після інваріантів, але БЕЗ урахування вимкнених модулів. Інакше підвищення
    /// ключа вимкненого модуля давало б 403 permission_ceiling, а не 422 module_disabled (п.13), а права «зникали» б із стелі.
    /// </summary>
    public string Ceiling(string key) => _withoutModules.TryGetValue(key, out var l) ? l : string.Empty;

    /// <summary>true, якщо хоча б один модуль ключа вимкнено для tenant.</summary>
    public bool ModuleOff(string key) => PermissionCatalog.Find(key) is { } d && d.Modules.Any(m => !_enabledModules.Contains(m));

    /// <summary>
    /// Ключі (<c>locked</c>, §30.9.3), де видане ЦІЛІ значення вище за стелю цього актора (<see cref="Ceiling"/>):
    /// актор не може змінити їх ні вгору, ні вниз. Невідомі ключі/рівні цілі ігноруються.
    /// </summary>
    public IReadOnlyList<string> Locked(IReadOnlyDictionary<string, string> targetGranted) =>
    [
        .. PermissionCatalog.All.Where(d =>
            targetGranted.TryGetValue(d.Key, out var t) && d.IndexOf(t) > d.IndexOf(Ceiling(d.Key))).Select(d => d.Key),
    ];

    /// <summary>Знижує залежні ключі повного набору (roleCeiling, lte, requires), як при обчисленні effective (§30.4). Для прийняття запрошення.</summary>
    public static IReadOnlyDictionary<string, string> Normalize(string? role, IReadOnlyDictionary<string, string> values) =>
        ApplyConstraints(role, values);

    /// <summary>
    /// Будує набір. <paramref name="role"/> поза ролями tenant (null/невідома) дає <see cref="None"/>-подібний результат.
    /// <paramref name="granted"/>: рядки user_permissions (ключ точно, ordinal); для owner ігноруються (максимум неявно).
    /// </summary>
    public static AccessSet Build(string? role, IReadOnlyDictionary<string, string>? granted,
        IEnumerable<string> enabledModules, bool specialistInactive)
    {
        var enabled = new HashSet<string>(enabledModules, StringComparer.OrdinalIgnoreCase);
        var validRole = Roles.IsTenantRole(role);

        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in PermissionCatalog.All)
        {
            string value;
            if (!validRole) value = d.Lowest;
            else if (role == Roles.Owner) value = d.Highest;
            else value = granted is not null && granted.TryGetValue(d.Key, out var g) && d.IndexOf(g) >= 0 ? g : d.Lowest;
            raw[d.Key] = value;
        }

        var withoutModules = ApplyConstraints(role, raw);

        var gated = new Dictionary<string, string>(raw, StringComparer.Ordinal);
        foreach (var d in PermissionCatalog.All)
            if (d.Modules.Any(m => !enabled.Contains(m))) gated[d.Key] = d.Lowest;
        var effective = ApplyConstraints(role, gated);

        return new AccessSet(validRole ? role : null, raw, withoutModules, effective, validRole && specialistInactive, enabled);
    }

    /// <summary>Знижує залежні ключі до найнижчого рівня (lte — до рівня передумови), доки набір не стабілізується.</summary>
    private static Dictionary<string, string> ApplyConstraints(string? role, IReadOnlyDictionary<string, string> input)
    {
        var v = new Dictionary<string, string>(input, StringComparer.Ordinal);
        for (var pass = 0; pass < PermissionCatalog.All.Count + 1; pass++)
        {
            var changed = false;
            foreach (var d in PermissionCatalog.All)
            {
                var idx = d.IndexOf(v[d.Key]);
                var target = idx;
                if (d.RoleCeiling is not null && role != Roles.Owner && (role is null || !d.RoleCeiling.Contains(role, StringComparer.Ordinal)))
                    target = 0;
                if (d.LteKey is not null)
                {
                    var other = PermissionCatalog.Require(d.LteKey);
                    target = Math.Min(target, Math.Min(other.IndexOf(v[other.Key]), d.Levels.Count - 1));
                }
                if (d.Requires is not null)
                {
                    var other = PermissionCatalog.Require(d.Requires.Key);
                    if (target > 0 && other.IndexOf(v[other.Key]) < other.IndexOf(d.Requires.Level)) target = 0;
                }
                if (target != idx)
                {
                    v[d.Key] = d.Levels[target];
                    changed = true;
                }
            }
            if (!changed) break;
        }
        return v;
    }
}

/// <summary>Знімок доступу з БД: один запит під RLS (§30.5 п.4).</summary>
/// <param name="SpecialistActive">null — немає прив'язаного профілю; false — профіль неактивний або не видно.</param>
public sealed record AccessSnapshot(
    string Role, bool IsActive, string State, Guid? SpecialistId, int AccessVersion, bool? SpecialistActive,
    IReadOnlyDictionary<string, string> Permissions, IReadOnlyList<string> Modules,
    DateTimeOffset? SessionsValidAfter = null);

/// <summary>Порт читання доступу користувача (реалізація — Infrastructure, під RLS поточного tenant).</summary>
public interface IAccessStore
{
    /// <summary>null — користувача немає (зокрема чужий tenant). Кидає виняток при збої БД (middleware перетворює на 503).</summary>
    Task<AccessSnapshot?> LoadAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Scoped-контекст доступу запиту (§30.5). Завантажується middleware один раз; без middleware <see cref="IsLoaded"/> = false,
/// <see cref="Access"/> = <see cref="AccessSet.None"/>.
/// </summary>
public interface IAccessContext
{
    bool IsLoaded { get; }
    AccessSet Access { get; }
    int AccessVersion { get; }
    /// <summary><c>{access_version}.{hex8 хешу модулів}</c>; збігається із заголовком X-Access-Version.</summary>
    string AccessStamp { get; }
    IReadOnlyList<string> Modules { get; }
}

public static class AccessStamp
{
    /// <summary><c>{version}.{hex8 SHA-256 відсортованого (ordinal, lower) переліку модулів через кому}</c> (§30.9.1).</summary>
    public static string Compute(int accessVersion, IEnumerable<string> modules)
    {
        var joined = string.Join(',', modules.Select(m => m.ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return $"{accessVersion}.{Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant()}";
    }
}
