using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyInventory;

// Дерево категорій товарів (ADR-025, контракт §32.27). Права як у каталогу: читання inventory.view (own+), зміни inventory.manage=all.
// Глибина до 3 рівнів (корінь = 1), унікальне ім'я в межах батька без урахування регістру, ліміт 500 вузлів на tenant. Архівування не каскадне.
public sealed partial class InventoryService
{
    public const int MaxCategoryDepth = 3, MaxCategories = 500, MaxCategoryNameLength = 100, MaxSortOrder = 1_000_000;

    private static readonly Error CategoryNotFound = Error.NotFound("category_not_found", "Category not found.");
    private static readonly Error InvalidSort = Error.Validation("invalid_sort_order", "Sort order must be within -1000000..1000000.");

    private static Error CategoryErr(string code, string msg) => Error.Validation(code, msg);

    private static Error NameTaken => Error.Conflict("category_name_taken", "A category with this name already exists under the same parent.");

    private static Result<string> ValidateCategoryName(string? raw)
    {
        var name = raw?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxCategoryNameLength) return V("invalid_name", "Name is required (max 100).");
        return name;
    }

    private static bool ValidSortOrder(int? v) => v is null || (v >= -MaxSortOrder && v <= MaxSortOrder);

    // ---- чисті обчислення над знімком дерева ----

    /// <summary>Рівень вузла (корінь = 1). Захист від циклу в даних (не повинно бути): понад ліміт вважаємо глибше за максимум.</summary>
    private static int DepthOf(IReadOnlyDictionary<Guid, CategoryRow> byId, Guid? id)
    {
        var depth = 0;
        while (id is { } cur && byId.TryGetValue(cur, out var row))
        {
            if (++depth > MaxCategoryDepth + 1) break;
            id = row.ParentId;
        }
        return depth;
    }

    /// <summary>Висота піддерева з коренем id: лише вузол = 1.</summary>
    private static int HeightOf(ILookup<Guid, CategoryRow> children, Guid id, int guard = 0) =>
        guard > MaxCategoryDepth + 1 ? guard : 1 + children[id].Select(c => HeightOf(children, c.Id, guard + 1)).DefaultIfEmpty(0).Max();

    private static bool NameClash(IReadOnlyList<CategoryRow> rows, Guid? parentId, string name, Guid? exceptId) =>
        rows.Any(r => r.ParentId == parentId && r.Id != exceptId && string.Equals(NameKey(r.Name), NameKey(name), StringComparison.Ordinal));

    // ---- читання ----

    public async Task<Result<IReadOnlyList<CategoryDto>>> ListCategoryNodesAsync(Actor actor, bool includeArchived, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var nodes = (await store.ListCategoryNodesAsync(ct)).Where(n => includeArchived || !n.IsArchived)
            .OrderBy(n => n.ParentId.HasValue).ThenBy(n => n.ParentId).ThenBy(n => n.SortOrder).ThenBy(n => NameKey(n.Name), StringComparer.Ordinal).ThenBy(n => n.Id);
        return Result<IReadOnlyList<CategoryDto>>.Ok([.. nodes]);
    }

    // ---- запис ----

    private Result<CategoryDto> MapCategory(CategoryWriteResult r, Guid userId)
    {
        switch (r.Outcome)
        {
            case CategoryWriteOutcome.Ok:
                logger?.LogInformation("inventory_catalog_changed entity=category id={Id} user={User}", r.Category!.Id, userId);
                return r.Category!;
            case CategoryWriteOutcome.Rejected: return r.Error!;
            case CategoryWriteOutcome.NotFound: return CategoryNotFound;
            default: return NameTaken;
        }
    }

    public async Task<Result<CategoryDto>> CreateCategoryAsync(Actor actor, CategoryRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        var name = ValidateCategoryName(req.Name);
        if (!name.IsOk) return name.Error!;
        if (!ValidSortOrder(req.SortOrder)) return InvalidSort;
        var parentId = req.ParentId;
        var sort = req.SortOrder ?? 0;
        var text = name.Value!;
        return MapCategory(await store.WriteCategoryAsync(rows =>
        {
            if (rows.Count >= MaxCategories) return new CategoryReject(CategoryErr("category_limit", "Category limit reached (500)."));
            var byId = rows.ToDictionary(r => r.Id);
            if (parentId is { } pid)
            {
                if (!byId.TryGetValue(pid, out var parent)) return new CategoryReject(CategoryNotFound);
                if (parent.IsArchived) return new CategoryReject(CategoryErr("category_archived", "The parent category is archived."));
                if (DepthOf(byId, pid) + 1 > MaxCategoryDepth) return new CategoryReject(CategoryErr("category_depth", "Categories can be nested up to 3 levels."));
            }
            if (NameClash(rows, parentId, text, null)) return new CategoryReject(NameTaken);
            return new CategoryInsert(parentId, text, sort);
        }, clock.GetUtcNow(), ct), actor.UserId);
    }

    /// <summary>Перейменування (+ sortOrder, якщо передано). Батько не змінюється.</summary>
    public async Task<Result<CategoryDto>> UpdateCategoryAsync(Actor actor, Guid id, CategoryRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (await store.GetCategoryRowAsync(id, ct) is null) return CategoryNotFound;
        var name = ValidateCategoryName(req.Name);
        if (!name.IsOk) return name.Error!;
        if (!ValidSortOrder(req.SortOrder)) return InvalidSort;
        var text = name.Value!;
        return MapCategory(await store.WriteCategoryAsync(rows =>
        {
            if (rows.FirstOrDefault(r => r.Id == id) is not { } cur) return new CategoryReject(CategoryNotFound);
            if (NameClash(rows, cur.ParentId, text, id)) return new CategoryReject(NameTaken);
            return new CategoryUpdate(id, cur.ParentId, text, req.SortOrder ?? cur.SortOrder, cur.IsArchived);
        }, clock.GetUtcNow(), ct), actor.UserId);
    }

    /// <summary>Зміна батька (null = у корінь) і/або sortOrder. Піддерево після переміщення має вкладатися в 3 рівні; нащадок не може стати батьком предка.</summary>
    public async Task<Result<CategoryDto>> MoveCategoryAsync(Actor actor, Guid id, CategoryMoveRequest req, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (await store.GetCategoryRowAsync(id, ct) is null) return CategoryNotFound;
        if (!ValidSortOrder(req.SortOrder)) return InvalidSort;
        var newParent = req.ParentId;
        return MapCategory(await store.WriteCategoryAsync(rows =>
        {
            var byId = rows.ToDictionary(r => r.Id);
            if (!byId.TryGetValue(id, out var cur)) return new CategoryReject(CategoryNotFound);
            if (newParent is { } pid)
            {
                if (!byId.TryGetValue(pid, out var parent)) return new CategoryReject(CategoryNotFound);
                // цикл: сам вузол або будь-який його нащадок (піднімаємось від нового батька до кореня)
                var guard = 0;
                for (Guid? up = pid; up is { } u && byId.TryGetValue(u, out var anc) && guard++ <= MaxCategories; up = anc.ParentId)
                    if (u == id) return new CategoryReject(CategoryErr("category_cycle", "A category cannot be moved under itself or its descendant."));
                if (pid != cur.ParentId && parent.IsArchived) return new CategoryReject(CategoryErr("category_archived", "The parent category is archived."));
                var height = HeightOf(rows.Where(r => r.ParentId.HasValue).ToLookup(r => r.ParentId!.Value), id);
                if (DepthOf(byId, pid) + height > MaxCategoryDepth) return new CategoryReject(CategoryErr("category_depth", "Categories can be nested up to 3 levels."));
            }
            if (NameClash(rows, newParent, cur.Name, id)) return new CategoryReject(NameTaken);
            return new CategoryUpdate(id, newParent, cur.Name, req.SortOrder ?? cur.SortOrder, cur.IsArchived);
        }, clock.GetUtcNow(), ct), actor.UserId);
    }

    /// <summary>Архівування/розархівування вузла (ідемпотентне, без каскаду: діти й товари не змінюються).</summary>
    public async Task<Result<CategoryDto>> SetCategoryArchivedAsync(Actor actor, Guid id, bool archived, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        if (await store.GetCategoryRowAsync(id, ct) is null) return CategoryNotFound;
        return MapCategory(await store.WriteCategoryAsync(rows =>
            rows.FirstOrDefault(r => r.Id == id) is { } cur
                ? new CategoryUpdate(id, cur.ParentId, cur.Name, cur.SortOrder, archived)
                : new CategoryReject(CategoryNotFound), clock.GetUtcNow(), ct), actor.UserId);
    }

    public async Task<Result<bool>> DeleteCategoryAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedManage(actor, PermissionLevels.All) is { } f) return f;
        switch (await store.DeleteCategoryAsync(id, ct))
        {
            case CategoryWriteOutcome.Ok:
                logger?.LogInformation("inventory_catalog_changed entity=category id={Id} user={User}", id, actor.UserId);
                return true;
            case CategoryWriteOutcome.NotFound: return CategoryNotFound;
            default: return Error.Conflict("category_not_empty", "The category has subcategories or products.");
        }
    }
}
