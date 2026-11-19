using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>Ключі каталогу прав (ADR-023, §30.2). Єдине місце рядків: атрибути й сервіси посилаються на ці константи.</summary>
public static class PermissionKeys
{
    public const string CalendarView = "calendar.view";
    public const string CalendarManage = "calendar.manage";
    public const string ClientsView = "clients.view";
    public const string ClientsEdit = "clients.edit";
    public const string ClientsContacts = "clients.contacts";
    public const string ClientsCalls = "clients.calls";
    public const string RevenueView = "revenue.view";
    public const string RevenueCosts = "revenue.costs";
    public const string ChannelsInbox = "channels.inbox";
    public const string ChannelsSettings = "channels.settings";
    public const string AiUse = "ai.use";
    public const string AiSettings = "ai.settings";
    public const string ServicesCatalog = "services.catalog";
    public const string MarketingCampaigns = "marketing.campaigns";
    public const string StaffAccess = "staff.access";
    public const string LocationsAccess = "locations.access";
    public const string SettingsGeneral = "settings.general";
    public const string TeamManage = "team.manage";
}

/// <summary>Рівні каталогу (порівнюються точно, ordinal; §30.2).</summary>
public static class PermissionLevels
{
    public const string None = "none";
    public const string Own = "own";
    public const string All = "all";
    public const string View = "view";
    public const string Edit = "edit";
    public const string Reply = "reply";
    public const string Use = "use";
    public const string Masked = "masked";
    public const string Full = "full";
    public const string Manage = "manage";
}

/// <summary>Передумова <c>requires other = level</c> (§30.2).</summary>
public sealed record PermissionRequirement(string Key, string Level);

/// <summary>
/// Опис ключа. <see cref="Levels"/> впорядковані за зростанням; перший є найнижчим і діє за замовчуванням (fail-closed).
/// <see cref="Modules"/>: усі мають бути ввімкнені для tenant. <see cref="MenuLevels"/> (Q-C3): рівні, що впливають лише на меню.
/// </summary>
public sealed record PermissionDef(
    string Key, string Block, IReadOnlyList<string> Levels, IReadOnlyList<string> Modules,
    string? LteKey = null, PermissionRequirement? Requires = null, IReadOnlyList<string>? RoleCeiling = null,
    IReadOnlyList<string>? MenuLevels = null)
{
    public string Lowest => Levels[0];
    public string Highest => Levels[^1];

    /// <summary>Індекс рівня (ordinal, без нормалізації регістру); -1 — невідомий.</summary>
    public int IndexOf(string? level)
    {
        if (level is null) return -1;
        for (var i = 0; i < Levels.Count; i++)
            if (string.Equals(Levels[i], level, StringComparison.Ordinal)) return i;
        return -1;
    }
}

public sealed record PermissionBlock(string Block, IReadOnlyList<string> Routes, IReadOnlyList<PermissionDef> Permissions);

// ---------- DTO каталогу (GET /api/access/catalog, §30.9.2) ----------

public sealed record CatalogRoleDto(string Code, int Rank, bool TenantAssignable);

public sealed record CatalogPermissionDto(
    string Key, IReadOnlyList<string> Levels, IReadOnlyList<string> Modules,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? MenuLevels = null);

public sealed record CatalogBlockDto(string Block, IReadOnlyList<string> Routes, IReadOnlyList<CatalogPermissionDto> Permissions);

public sealed record CatalogConstraintDto(
    string Type, string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Other = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Roles = null);

public sealed record AccessCatalogDto(
    int CatalogVersion, IReadOnlyList<CatalogRoleDto> Roles, IReadOnlyList<CatalogBlockDto> Blocks,
    IReadOnlyList<CatalogConstraintDto> Constraints, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Presets,
    IReadOnlyList<string> ReservedToOwner);

/// <summary>
/// Каталог прав (ADR-023, контракт §30.2): 18 ключів, рівні, модулі, обмеження. Живе в коді; новий ключ — лише разом з міграцією
/// backfill за пресетами й збільшенням <see cref="Version"/>.
/// </summary>
public static class PermissionCatalog
{
    public const int Version = 1;

    private static readonly string[] None_Own_All = [PermissionLevels.None, PermissionLevels.Own, PermissionLevels.All];
    private static readonly string[] Own_All_Edit = [PermissionLevels.Own, PermissionLevels.All, PermissionLevels.Edit];
    private static readonly string[] None_View_Edit = [PermissionLevels.None, PermissionLevels.View, PermissionLevels.Edit];
    private static readonly string[] None_Edit = [PermissionLevels.None, PermissionLevels.Edit];

    private const string Booking = "beauty_booking";
    private const string Clients = "beauty_clients";
    private const string Analytics = "beauty_analytics";
    private const string Channels = "beauty_channels";
    private const string Ai = "beauty_ai";
    private const string Catalog = "beauty_catalog";

    private static readonly string[] MenuOwnAll = [PermissionLevels.Own, PermissionLevels.All];

    /// <summary>Блоки й ключі в порядку таблиці §30.2.</summary>
    public static readonly IReadOnlyList<PermissionBlock> Blocks =
    [
        new("calendar", ["/beauty", "/beauty/calendar"],
        [
            new(PermissionKeys.CalendarView, "calendar", None_Own_All, [Booking]),
            new(PermissionKeys.CalendarManage, "calendar", None_Own_All, [Booking], LteKey: PermissionKeys.CalendarView),
        ]),
        new("clients", ["/beauty/clients", "/beauty/calls"],
        [
            new(PermissionKeys.ClientsView, "clients", None_Own_All, [Clients]),
            new(PermissionKeys.ClientsEdit, "clients", None_Own_All, [Clients], LteKey: PermissionKeys.ClientsView),
            new(PermissionKeys.ClientsContacts, "clients", [PermissionLevels.Masked, PermissionLevels.Full], [Clients]),
            new(PermissionKeys.ClientsCalls, "clients", [PermissionLevels.None, PermissionLevels.All], [Clients],
                Requires: new(PermissionKeys.ClientsView, PermissionLevels.All)),
        ]),
        new("revenue", ["/beauty/analytics"],
        [
            new(PermissionKeys.RevenueView, "revenue", None_Own_All, [Analytics]),
            new(PermissionKeys.RevenueCosts, "revenue", None_View_Edit, [Analytics],
                Requires: new(PermissionKeys.RevenueView, PermissionLevels.All)),
        ]),
        new("channels", ["/beauty/inbox", "/beauty/channels"],
        [
            new(PermissionKeys.ChannelsInbox, "channels", [PermissionLevels.None, PermissionLevels.View, PermissionLevels.Reply], [Channels]),
            new(PermissionKeys.ChannelsSettings, "channels", None_Edit, [Channels]),
        ]),
        new("ai", ["/beauty/ai"],
        [
            new(PermissionKeys.AiUse, "ai", [PermissionLevels.None, PermissionLevels.Use], [Ai]),
            new(PermissionKeys.AiSettings, "ai", None_Edit, [Ai]),
        ]),
        new("services", ["/beauty/services", "/beauty/promos"],
        [
            new(PermissionKeys.ServicesCatalog, "services", [PermissionLevels.View, PermissionLevels.Edit], [Catalog]),
        ]),
        new("marketing", ["/beauty/campaigns"],
        [
            new(PermissionKeys.MarketingCampaigns, "marketing", None_Edit, [BeautyModules.Marketing],
                Requires: new(PermissionKeys.ClientsView, PermissionLevels.All)),
        ]),
        new("staff", ["/beauty/staff"],
        [
            new(PermissionKeys.StaffAccess, "staff", Own_All_Edit, [Booking], MenuLevels: MenuOwnAll),
        ]),
        new("locations", ["/beauty/locations"],
        [
            new(PermissionKeys.LocationsAccess, "locations", Own_All_Edit, [Booking], MenuLevels: MenuOwnAll),
        ]),
        new("settings", ["/beauty/settings"],
        [
            new(PermissionKeys.SettingsGeneral, "settings", None_View_Edit, []),
        ]),
        new("team", ["/beauty/team"],
        [
            new(PermissionKeys.TeamManage, "team", [PermissionLevels.None, PermissionLevels.Manage], [], RoleCeiling: [Roles.Admin]),
        ]),
    ];

    public static readonly IReadOnlyList<PermissionDef> All = [.. Blocks.SelectMany(b => b.Permissions)];

    private static readonly Dictionary<string, PermissionDef> ByKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);

    /// <summary>Ключі, зарезервовані за owner (відсутні як ключі каталогу, §30.9.2).</summary>
    public static readonly IReadOnlyList<string> ReservedToOwner =
        ["ai.provider_keys", "ai.mode_auto", "cancellation.policy", "team.admins", "access.audit"];

    /// <summary>Пошук ключа (ordinal, регістр значущий); null — невідомий.</summary>
    public static PermissionDef? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var d) ? d : null;

    public static PermissionDef Require(string key) =>
        Find(key) ?? throw new ArgumentException($"Unknown permission key '{key}'.", nameof(key));

    /// <summary>Перевірка пари ключ+рівень (для атрибутів і тестів). Кидає ArgumentException при невідомих значеннях.</summary>
    public static void Validate(string key, string level)
    {
        var def = Require(key);
        if (def.IndexOf(level) < 0) throw new ArgumentException($"Unknown level '{level}' for permission '{key}'.", nameof(level));
    }

    /// <summary>Обмеження каталогу в порядку ключів (для відповіді каталогу).</summary>
    public static IReadOnlyList<CatalogConstraintDto> Constraints() =>
    [
        .. All.SelectMany(d => new[]
        {
            d.LteKey is null ? null : new CatalogConstraintDto("lte", d.Key, d.LteKey),
            d.Requires is null ? null : new CatalogConstraintDto("requires", d.Key, d.Requires.Key, d.Requires.Level),
            d.RoleCeiling is null ? null : new CatalogConstraintDto("roleCeiling", d.Key, Roles: d.RoleCeiling),
        }).OfType<CatalogConstraintDto>(),
    ];

    /// <summary>Відповідь <c>GET /api/access/catalog</c> (однакова для всіх ролей; §30.9.2).</summary>
    public static AccessCatalogDto ToDto() => new(
        Version,
        [
            new(Roles.Owner, Roles.Rank(Roles.Owner), false),
            new(Roles.Admin, Roles.Rank(Roles.Admin), true),
            new(Roles.Deputy, Roles.Rank(Roles.Deputy), true),
            new(Roles.Specialist, Roles.Rank(Roles.Specialist), true),
        ],
        [.. Blocks.Select(b => new CatalogBlockDto(b.Block, b.Routes,
            [.. b.Permissions.Select(p => new CatalogPermissionDto(p.Key, p.Levels, p.Modules,
                p.MenuLevels is null ? null : "menu", p.MenuLevels))]))],
        Constraints(),
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [Roles.Admin] = PermissionPresets.For(Roles.Admin),
            [Roles.Deputy] = PermissionPresets.For(Roles.Deputy),
            [Roles.Specialist] = PermissionPresets.For(Roles.Specialist),
        },
        ReservedToOwner);
}

/// <summary>
/// Пресети ролей (§30.3). Застосовуються під час прийняття запрошення, зміни ролі й скидання; далі рядки є звичайними значеннями.
/// Для owner права неявні (максимум), рядків немає. Джерелом <c>Actor.Access</c> пресет не є ніколи (B2; архітектурний тест).
/// </summary>
public static class PermissionPresets
{
    // ключ: admin, deputy, specialist
    private static readonly (string Key, string Admin, string Deputy, string Specialist)[] Table =
    [
        (PermissionKeys.CalendarView, "all", "all", "own"),
        (PermissionKeys.CalendarManage, "all", "all", "own"),
        (PermissionKeys.ClientsView, "all", "all", "none"),
        (PermissionKeys.ClientsEdit, "all", "all", "none"),
        (PermissionKeys.ClientsContacts, "full", "full", "masked"),
        (PermissionKeys.ClientsCalls, "all", "all", "none"),
        (PermissionKeys.RevenueView, "all", "none", "none"),
        (PermissionKeys.RevenueCosts, "edit", "none", "none"),
        (PermissionKeys.ChannelsInbox, "reply", "reply", "none"),
        (PermissionKeys.ChannelsSettings, "edit", "none", "none"),
        (PermissionKeys.AiUse, "use", "use", "none"),
        (PermissionKeys.AiSettings, "edit", "none", "none"),
        (PermissionKeys.ServicesCatalog, "edit", "view", "view"),
        (PermissionKeys.MarketingCampaigns, "edit", "none", "none"),
        (PermissionKeys.StaffAccess, "edit", "all", "own"),
        (PermissionKeys.LocationsAccess, "edit", "all", "own"),
        (PermissionKeys.SettingsGeneral, "view", "none", "none"),
        (PermissionKeys.TeamManage, "manage", "none", "none"),
    ];

    private static readonly IReadOnlyDictionary<string, string> AdminPreset = Make(t => t.Admin);
    private static readonly IReadOnlyDictionary<string, string> DeputyPreset = Make(t => t.Deputy);
    private static readonly IReadOnlyDictionary<string, string> SpecialistPreset = Make(t => t.Specialist);
    private static readonly IReadOnlyDictionary<string, string> OwnerPreset =
        PermissionCatalog.All.ToDictionary(d => d.Key, d => d.Highest, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> LowestPreset =
        PermissionCatalog.All.ToDictionary(d => d.Key, d => d.Lowest, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> Make(Func<(string Key, string Admin, string Deputy, string Specialist), string> pick) =>
        Table.ToDictionary(t => t.Key, pick, StringComparer.Ordinal);

    /// <summary>Повний набір (усі ключі каталогу) для ролі. owner — максимум; невідома роль — найнижчі рівні.</summary>
    public static IReadOnlyDictionary<string, string> For(string? role) => role switch
    {
        Roles.Owner => OwnerPreset,
        Roles.Admin => AdminPreset,
        Roles.Deputy => DeputyPreset,
        Roles.Specialist => SpecialistPreset,
        _ => LowestPreset,
    };
}
