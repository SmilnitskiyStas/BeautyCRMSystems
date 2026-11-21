using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyInventory;

public sealed class InventoryOptions
{
    public const int DefaultExpiringSoonDays = 30;
    private int _days = DefaultExpiringSoonDays;
    /// <summary>Inventory:ExpiringSoonDays, 1..365.</summary>
    public int ExpiringSoonDays { get => _days; set => _days = Math.Clamp(value, 1, 365); }
}

/// <summary>
/// Склад (ADR-025, §32). Уся бізнес-логіка: права й scope (own = заклади прив'язаного профілю, чужий заклад -> 404 без розкриття),
/// валідація §32.7, ідемпотентність, поля цін за inventory.costs, FEFO (через <see cref="FefoPlanner"/>), курсори.
/// Атомарність і блокування - у <see cref="IInventoryStore"/>. Час - лише через <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class InventoryService(IInventoryStore store, TimeProvider clock, InventoryOptions options, ILogger<InventoryService>? logger = null)
{
    public const int MaxLines = 100, MaxCountLines = 500, MaxStocktakeLines = 2000, DefaultTake = 50, MaxTake = 200, MaxRangeDays = 366;
    public const decimal MaxMoney = 1_000_000m, MaxQuantity = 1_000_000m;
    /// <summary>Межа numeric(14,3): рівень залишку (і Σ надходжень на нього) не може її перевищити.</summary>
    public const decimal MaxLevel = 99_999_999_999.999m;

    private static readonly string[] Units = ["pcs", "ml", "g", "l", "kg", "pack"];
    private static readonly string[] Purposes = ["sale", "salon_use", "both"];
    private static readonly string[] Reasons = ["expired", "damaged", "lost", "internal_use", "correction", "other"];

    private static readonly Regex BarcodeShape = new(@"^[0-9A-Za-z._-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PhoneShape = new(@"^[0-9+()\-. ]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ---------- права й scope ----------

    private sealed record Scope(bool All, IReadOnlySet<Guid> Ids)
    {
        public bool Contains(Guid id) => All || Ids.Contains(id);
        public static readonly Scope Empty = new(false, new HashSet<Guid>());
    }

    private async Task<Scope> ScopeOfAsync(Actor actor, string key, CancellationToken ct)
    {
        var level = actor.Access.Level(key);
        if (level == PermissionLevels.All) return new Scope(true, new HashSet<Guid>());
        if (level != PermissionLevels.Own || actor.SpecialistId is not { } sid || actor.Access.SpecialistInactive) return Scope.Empty;
        return new Scope(false, (await store.SpecialistLocationIdsAsync(sid, ct)).ToHashSet());
    }

    private Task<Scope> ViewScopeAsync(Actor actor, CancellationToken ct) => ScopeOfAsync(actor, PermissionKeys.InventoryView, ct);
    private Task<Scope> ManageScopeAsync(Actor actor, CancellationToken ct) => ScopeOfAsync(actor, PermissionKeys.InventoryManage, ct);

    private static Error? NeedView(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryView, PermissionLevels.Own) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryView, PermissionLevels.Own);

    private static Error? NeedManage(Actor a, string level = PermissionLevels.Own) =>
        a.Access.AtLeast(PermissionKeys.InventoryManage, level) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryManage, level);

    private static string CostsLevel(Actor a) => a.Access.Level(PermissionKeys.InventoryCosts);
    private static bool SeesCosts(Actor a) => a.Access.AtLeast(PermissionKeys.InventoryCosts, PermissionLevels.View);
    private static bool CanEditCosts(Actor a) => a.Access.AtLeast(PermissionKeys.InventoryCosts, PermissionLevels.Edit);

    private static Error? NeedCostsEdit(Actor a) =>
        CanEditCosts(a) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryCosts, PermissionLevels.Edit);

    private int ExpiringDays(int? requested) => requested is { } d ? Math.Clamp(d, 1, 365) : options.ExpiringSoonDays;

    private async Task<Dictionary<Guid, LocationRow>> LocationsAsync(CancellationToken ct) =>
        (await store.ListInventoryLocationsAsync(ct)).ToDictionary(l => l.Id);

    private static readonly Error LocationNotFound = Error.NotFound("location_not_found", "Location not found.");
    private static readonly Error ProductNotFound = Error.NotFound("product_not_found", "Product not found.");
    private static readonly Error SupplierNotFound = Error.NotFound("supplier_not_found", "Supplier not found.");

    private static Error WithLine(Error e, int line) =>
        e with { Extras = new Dictionary<string, object?> { ["line"] = line } };

    private static Error V(string code, string message, int? line = null) =>
        line is null ? Error.Validation(code, message) : WithLine(Error.Validation(code, message), line.Value);

    // ---------- чисті перевірки ----------

    private static bool Scale(decimal v, int digits) => decimal.Round(v, digits) == v;
    private static bool IsInteger(string unit) => unit is "pcs" or "pack";

    private static bool ValidMoney(decimal v) => v >= 0 && v <= MaxMoney && Scale(v, 2);

    private static bool ValidStockQuantity(decimal v, string? unit, bool allowZero) =>
        (allowZero ? v >= 0 : v > 0) && v <= MaxQuantity && Scale(v, 3) && (unit is null || !IsInteger(unit) || v == decimal.Truncate(v));

    private static bool ValidMinStock(decimal v, string unit) => v >= 0 && v <= MaxQuantity && Scale(v, 3) && (!IsInteger(unit) || v == decimal.Truncate(v));

    /// <summary>yyyy-MM-dd, рік 2000..2100.</summary>
    internal static bool TryDate(string? s, out DateOnly d) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d) && d.Year is >= 2000 and <= 2100;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------- курсори (keyset, base64url "{частини}|{hash параметрів}", частини екрануються) ----------

    internal static string ParamHash(params object?[] parts)
    {
        var s = string.Join('\u001f', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();
    }

    internal static string EncodeCursor(string hash, params string[] parts)
    {
        var raw = string.Join('|', parts.Select(Uri.EscapeDataString).Append(hash));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// null cursor = початок. Нерозбірний, від інших параметрів або з сміттєвими полями -> invalid_cursor (422), ніколи 500.
    /// kinds - тип кожної частини: n = рядок, g = Guid, t = ticks (long у межах DateTimeOffset), d = дата yyyy-MM-dd, D = дата або порожньо, f = "0"/"1".
    /// Повернуті частини гарантовано парсяться відповідним типом.
    /// </summary>
    internal static Result<string[]?> DecodeCursor(string? cursor, string hash, string kinds)
    {
        if (string.IsNullOrEmpty(cursor)) return Result<string[]?>.Ok(null);
        try
        {
            var b64 = cursor.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length == kinds.Length + 1 && parts[^1] == hash)
            {
                var values = parts[..^1].Select(Uri.UnescapeDataString).ToArray();
                if (values.Zip(kinds).All(x => CursorPartValid(x.First, x.Second))) return Result<string[]?>.Ok(values);
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException or UriFormatException or OverflowException) { }
        return Error.Validation("invalid_cursor", "Cursor is invalid.");
    }

    private static bool CursorPartValid(string v, char kind) => kind switch
    {
        'n' => true,
        'g' => Guid.TryParseExact(v, "D", out _),
        't' => long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t <= DateTimeOffset.MaxValue.UtcTicks,
        'd' => TryDate(v, out _),
        'D' => v.Length == 0 || TryDate(v, out _),
        'f' => v is "0" or "1",
        _ => false,
    };

    private static int Take(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaxTake);

    private static string NameKey(string name) => name.ToLowerInvariant();

    /// <summary>Сторінка з відсортованого за (lower(name), id) списку.</summary>
    private static Result<Page<T>> PageByName<T>(IEnumerable<T> items, Func<T, string> name, Func<T, Guid> id, string? cursor, int? take, string hash)
    {
        var after = DecodeCursor(cursor, hash, "ng");
        if (!after.IsOk) return after.Error!;
        var ordered = items.OrderBy(i => NameKey(name(i)), StringComparer.Ordinal).ThenBy(id).AsEnumerable();
        if (after.Value is { } a)
        {
            var an = a[0]; var aid = Guid.Parse(a[1]);
            ordered = ordered.Where(i =>
            {
                var c = string.CompareOrdinal(NameKey(name(i)), an);
                return c > 0 || (c == 0 && id(i).CompareTo(aid) > 0);
            });
        }
        var n = Take(take);
        var list = ordered.Take(n + 1).ToList();
        var hasMore = list.Count > n;
        if (hasMore) list.RemoveAt(n);
        return new Page<T>(list, hasMore, hasMore ? EncodeCursor(hash, NameKey(name(list[^1])), id(list[^1]).ToString()) : null);
    }

    // ---------- scope ----------

    public async Task<Result<InventoryScopeDto>> GetScopeAsync(Actor actor, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var view = await ViewScopeAsync(actor, ct);
        var manage = await ManageScopeAsync(actor, ct);
        var locs = (await store.ListInventoryLocationsAsync(ct)).Where(l => view.Contains(l.Id)).OrderBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Id)
            .Select(l => new InventoryLocationDto(l.Id, l.Name, l.IsActive, l.Timezone, manage.Contains(l.Id))).ToList();
        return new InventoryScopeDto(locs, CostsLevel(actor), actor.Access.Level(PermissionKeys.InventoryManage) == PermissionLevels.All, options.ExpiringSoonDays);
    }

    // ---------- каталог: товари ----------

    private ProductDto Redact(Actor a, ProductDto p) => SeesCosts(a) ? p : p with { PurchasePrice = null };

    private ProductDetailDto Redact(Actor a, ProductDetailDto p) => SeesCosts(a) ? p : p with { PurchasePrice = null };

    public async Task<Result<Page<ProductDto>>> ListProductsAsync(Actor actor, ProductListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var active = (q.Active ?? "true").ToLowerInvariant();
        if (active is not ("true" or "false" or "all")) return V("invalid_status", "active must be true, false or all.");
        var search = NullIfEmpty(q.Search);
        var items = (await store.ListProductsAsync(ct)).Where(p =>
            (active == "all" || p.IsActive == (active == "true"))
            && (q.Category is null || string.Equals(p.Category, q.Category.Trim(), StringComparison.Ordinal))
            && (q.Purpose is null || p.Purpose == q.Purpose)
            && (q.SupplierId is null || p.Supplier?.Id == q.SupplierId)
            && (search is null || Contains(p.Name, search) || Contains(p.Barcode, search) || Contains(p.Category, search)));
        var r = PageByName(items, p => p.Name, p => p.Id, q.Cursor, q.Take, ParamHash("products", active, search, q.Category, q.Purpose, q.SupplierId));
        if (!r.IsOk) return r.Error!;
        var moved = await store.ProductIdsWithMovementsAsync([.. r.Value!.Items.Select(p => p.Id)], ct);
        return new Page<ProductDto>([.. r.Value.Items.Select(p => Redact(actor, p with { HasMovements = moved.Contains(p.Id) }))], r.Value.HasMore, r.Value.NextCursor);
    }

    private static bool Contains(string? s, string needle) => s is not null && s.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public async Task<Result<ProductDetailDto>> GetProductAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        if (await store.GetProductAsync(id, ct) is not { } p) return ProductNotFound;
        var scope = await ViewScopeAsync(actor, ct);
        var settings = scope.All ? p.LocationSettings : [.. p.LocationSettings.Where(s => scope.Contains(s.LocationId))];
        return Redact(actor, p with { LocationSettings = settings });
    }

    public async Task<Result<ProductDto>> GetProductByBarcodeAsync(Actor actor, string? code, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c) || await store.GetProductByBarcodeAsync(c, ct) is not { } p) return ProductNotFound;
        return Redact(actor, p);
    }

    public async Task<Result<IReadOnlyList<string>>> ListCategoriesAsync(Actor actor, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        return Result<IReadOnlyList<string>>.Ok(await store.ListCategoriesAsync(ct));
    }

    private async Task<Result<ProductData>> ValidateProductAsync(ProductRequest req, bool isCreate, ProductDetailDto? existing, CancellationToken ct)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return V("invalid_name", "Name is required (max 200).");
        var category = NullIfEmpty(req.Category);
        if (category is { Length: > 100 }) return V("invalid_category", "Category is too long (max 100).");
        var unit = req.Unit?.Trim();
        if (unit is null || !Units.Contains(unit, StringComparer.Ordinal)) return V("invalid_unit", "Unit must be one of pcs, ml, g, l, kg, pack.");
        var barcode = NullIfEmpty(req.Barcode);
        if (barcode is not null && !BarcodeShape.IsMatch(barcode)) return V("invalid_barcode", "Barcode must match [0-9A-Za-z._-]{1,64}.");
        var purpose = req.Purpose?.Trim();
        if (purpose is null || !Purposes.Contains(purpose, StringComparer.Ordinal)) return V("invalid_purpose", "Purpose must be sale, salon_use or both.");
        if (req.PurchasePrice is { } pp && !ValidMoney(pp)) return V("invalid_price", "Price must be 0..1000000 with at most 2 decimals.");
        // Роздрібна ціна (ADR-025 §12.x): обов'язкова для sale/both, для salon_use ігнорується (NULL). Без злиття з існуючим: PUT - повна заміна.
        decimal? salePrice = null;
        if (purpose != "salon_use")
        {
            if (req.SalePrice is not { } sp) return V("sale_price_required", "Sale price is required for products intended for sale.");
            if (!ValidMoney(sp)) return V("invalid_price", "Price must be 0..1000000 with at most 2 decimals.");
            salePrice = sp;
        }
        if (req.MinStock is { } ms && !ValidMinStock(ms, unit)) return V("invalid_min_stock", "Minimum stock is invalid for this unit.");
        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        if (existing is not null && existing.HasMovements && !string.Equals(existing.Unit, unit, StringComparison.Ordinal))
            return UnitLocked;
        if (req.SupplierId is { } sid && await store.GetSupplierAsync(sid, ct) is null) return SupplierNotFound;
        // Категорія з дерева (§32.27): має існувати в tenant; архівну не можна призначити, але товар, що вже в ній, редагується без зміни categoryId.
        if (req.CategoryId is { } cid && cid != existing?.CategoryId)
        {
            if (await store.GetCategoryRowAsync(cid, ct) is not { } cat) return CategoryInvalid;
            if (cat.IsArchived) return Error.Validation("category_archived", "The category is archived.");
        }

        return new ProductData(name, category, unit, barcode, purpose, req.SupplierId,
            req.PurchasePrice ?? existing?.PurchasePrice, salePrice, req.MinStock,
            req.TrackExpiry ?? existing?.TrackExpiry ?? false, existing is null ? true : req.IsActive ?? existing.IsActive, note, req.CategoryId);
    }

    private static readonly Error CategoryInvalid = Error.Validation("category_not_found", "Category not found.");

    private static readonly Error UnitLocked = Error.Validation("unit_locked", "The unit cannot be changed: the product already has movements.");

    private Result<ProductDetailDto> MapProduct(Actor actor, ProductWriteResult r, string op, Guid userId) => r.Outcome switch
    {
        CatalogWriteOutcome.Ok => LogCatalog("product", r.Product!.Id, userId, Redact(actor, r.Product!)),
        CatalogWriteOutcome.NotFound => ProductNotFound,
        CatalogWriteOutcome.BarcodeTaken => Error.Conflict("barcode_taken", "A product with this barcode already exists."),
        CatalogWriteOutcome.UnitLocked => UnitLocked,
        CatalogWriteOutcome.PurposeLocked => Error.Validation("purpose_locked", "The product is used in service materials: remove it from the norms first."),
        CatalogWriteOutcome.CategoryNotFound => CategoryInvalid,
        _ => Error.Conflict("barcode_taken", "A product with this barcode already exists."),
    };

    private ProductDetailDto LogCatalog(string entity, Guid id, Guid user, ProductDetailDto dto)
    {
        logger?.LogInformation("inventory_catalog_changed entity={Entity} id={Id} user={User}", entity, id, user);
        return dto;
    }

    public async Task<Result<ProductDetailDto>> CreateProductAsync(Actor actor, ProductRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (req.PurchasePrice is not null && NeedCostsEdit(actor) is { } c) return c;
        var data = await ValidateProductAsync(req, true, null, ct);
        if (!data.IsOk) return data.Error!;
        return MapProduct(actor, await store.CreateProductAsync(data.Value!, actor.UserId, clock.GetUtcNow(), ct), "create", actor.UserId);
    }

    public async Task<Result<ProductDetailDto>> UpdateProductAsync(Actor actor, Guid id, ProductRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (req.PurchasePrice is not null && NeedCostsEdit(actor) is { } c) return c;
        if (await store.GetProductAsync(id, ct) is not { } existing) return ProductNotFound;
        var data = await ValidateProductAsync(req, false, existing, ct);
        if (!data.IsOk) return data.Error!;
        return MapProduct(actor, await store.UpdateProductAsync(id, data.Value!, actor.UserId, clock.GetUtcNow(), ct), "update", actor.UserId);
    }

    public async Task<Result<LocationSettingDto>> PutLocationSettingAsync(Actor actor, Guid productId, Guid locationId, LocationSettingRequest req, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var scope = await ManageScopeAsync(actor, ct);
        if (!scope.Contains(locationId) || !(await LocationsAsync(ct)).ContainsKey(locationId)) return LocationNotFound;
        if (await store.GetProductAsync(productId, ct) is not { } p) return ProductNotFound;
        if (req.MinStock is { } ms && !ValidMinStock(ms, p.Unit)) return V("invalid_min_stock", "Minimum stock is invalid for this unit.");
        var s = await store.UpsertSettingAsync(productId, locationId, req.MinStock, req.IsEnabled ?? true, actor.UserId, clock.GetUtcNow(), ct);
        if (s is null) return ProductNotFound;
        logger?.LogInformation("inventory_catalog_changed entity=setting id={Id} user={User}", productId, actor.UserId);
        return s;
    }

    public async Task<Result<bool>> DeleteLocationSettingAsync(Actor actor, Guid productId, Guid locationId, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var scope = await ManageScopeAsync(actor, ct);
        if (!scope.Contains(locationId) || !(await LocationsAsync(ct)).ContainsKey(locationId)) return LocationNotFound;
        if (await store.GetProductAsync(productId, ct) is null) return ProductNotFound;
        if (!await store.DeleteSettingAsync(productId, locationId, ct)) return Error.NotFound("setting_not_found", "Setting not found.");
        logger?.LogInformation("inventory_catalog_changed entity=setting id={Id} user={User}", productId, actor.UserId);
        return true;
    }

    // ---------- постачальники ----------

    public async Task<Result<Page<SupplierDto>>> ListSuppliersAsync(Actor actor, SupplierListQuery q, CancellationToken ct)
    {
        if (NeedManage(actor) is { } f) return f;
        var active = (q.Active ?? "true").ToLowerInvariant();
        if (active is not ("true" or "false" or "all")) return V("invalid_status", "active must be true, false or all.");
        var search = NullIfEmpty(q.Search);
        var items = (await store.ListSuppliersAsync(ct)).Where(s =>
            (active == "all" || s.IsActive == (active == "true")) && (search is null || Contains(s.Name, search) || Contains(s.ContactName, search)));
        return PageByName(items, s => s.Name, s => s.Id, q.Cursor, q.Take, ParamHash("suppliers", active, search));
    }

    private static Result<SupplierData> ValidateSupplier(SupplierRequest req, bool? current)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) return V("invalid_name", "Name is required (max 200).");
        var contact = NullIfEmpty(req.ContactName);
        if (contact is { Length: > 200 }) return V("invalid_contact_name", "Contact name is too long (max 200).");
        var phone = NullIfEmpty(req.Phone);
        if (phone is not null && (phone.Length > 32 || !PhoneShape.IsMatch(phone))) return V("invalid_phone", "Phone is invalid.");
        var email = NullIfEmpty(req.Email);
        if (email is not null && (email.Length > 320 || !email.Contains('@'))) return V("invalid_email", "Email is invalid.");
        var note = NullIfEmpty(req.Note);
        if (note is { Length: > 500 }) return V("invalid_note", "Note is too long (max 500).");
        return new SupplierData(name, contact, phone, email, note, current is null ? true : req.IsActive ?? current.Value);
    }

    private Result<SupplierDto> MapSupplier(SupplierWriteResult r, Guid userId) => r.Outcome switch
    {
        CatalogWriteOutcome.Ok => LogSupplier(r.Supplier!, userId),
        CatalogWriteOutcome.NotFound => SupplierNotFound,
        _ => Error.Conflict("supplier_name_taken", "A supplier with this name already exists."),
    };

    private SupplierDto LogSupplier(SupplierDto s, Guid user)
    {
        logger?.LogInformation("inventory_catalog_changed entity=supplier id={Id} user={User}", s.Id, user);
        return s;
    }

    public async Task<Result<SupplierDto>> CreateSupplierAsync(Actor actor, SupplierRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        var d = ValidateSupplier(req, null);
        if (!d.IsOk) return d.Error!;
        return MapSupplier(await store.CreateSupplierAsync(d.Value!, clock.GetUtcNow(), ct), actor.UserId);
    }

    public async Task<Result<SupplierDto>> UpdateSupplierAsync(Actor actor, Guid id, SupplierRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (await store.GetSupplierAsync(id, ct) is not { } existing) return SupplierNotFound;
        var d = ValidateSupplier(req, existing.IsActive);
        if (!d.IsOk) return d.Error!;
        return MapSupplier(await store.UpdateSupplierAsync(id, d.Value!, clock.GetUtcNow(), ct), actor.UserId);
    }
}
