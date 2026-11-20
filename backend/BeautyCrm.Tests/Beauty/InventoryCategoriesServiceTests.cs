using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Дерево категорій товарів складу (ADR-025, §32.27): сервіс на <see cref="FakeInventoryStore"/> (глибина, цикли, дублікати, архів, видалення, товар з categoryId).</summary>
public sealed class InventoryCategoriesServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class Env
    {
        public readonly FakeInventoryStore Store = new();
        public readonly InventoryService Svc;
        public Env() => Svc = new InventoryService(Store, new FakeClock(Noon), new InventoryOptions());
    }

    private static Actor Owner() => Actor.ForTest(Roles.Owner);

    private static Actor Specialist()
    {
        var p = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        return Actor.ForTest(Roles.Specialist, Guid.NewGuid(), permissions: p);
    }

    private static CategoryRequest Req(string? name, Guid? parent = null, int? sort = null) => new(name, parent, sort);

    private static void AssertErr<T>(Result<T> r, string code, ErrorKind? kind = null)
    {
        Assert.False(r.IsOk, "expected error " + code);
        Assert.Equal(code, r.Error!.Code);
        if (kind is { } k) Assert.Equal(k, r.Error.Kind);
    }

    private static async Task<CategoryDto> Create(Env e, string name, Guid? parent = null)
    {
        var r = await e.Svc.CreateCategoryAsync(Owner(), Req(name, parent), default);
        Assert.True(r.IsOk, r.Error?.Code);
        return r.Value!;
    }

    [Fact]
    public async Task Create_root_child_grandchild_and_fourth_level_is_rejected()
    {
        var e = new Env();
        var hair = await Create(e, "  Волосся ");
        Assert.Equal("Волосся", hair.Name);
        Assert.Null(hair.ParentId);
        var dye = await Create(e, "Фарби", hair.Id);
        var brand = await Create(e, "Бренд", dye.Id);
        Assert.Equal(dye.Id, brand.ParentId);
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("Тон", brand.Id), default), "category_depth", ErrorKind.Validation);
        Assert.Equal(3, e.Store.Categories.Count);
    }

    [Fact]
    public async Task Create_validates_name_sort_and_parent()
    {
        var e = new Env();
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req(null), default), "invalid_name");
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("   "), default), "invalid_name");
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req(new string('x', 101)), default), "invalid_name");
        Assert.True((await e.Svc.CreateCategoryAsync(Owner(), Req(new string('x', 100)), default)).IsOk);
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("S", sort: 2_000_000), default), "invalid_sort_order");
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("P", Guid.NewGuid()), default), "category_not_found", ErrorKind.NotFound);
        var archived = e.Store.AddCategory("Arch", archived: true);
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("Child", archived.Id), default), "category_archived");
    }

    [Fact]
    public async Task Duplicate_name_is_case_insensitive_per_parent_but_allowed_under_different_parents()
    {
        var e = new Env();
        var a = await Create(e, "Волосся");
        var b = await Create(e, "Обличчя");
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("ВОЛОССЯ"), default), "category_name_taken", ErrorKind.Conflict);
        await Create(e, "Фарби", a.Id);
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("фарби", a.Id), default), "category_name_taken");
        Assert.True((await e.Svc.CreateCategoryAsync(Owner(), Req("Фарби", b.Id), default)).IsOk); // інший батько
        Assert.True((await e.Svc.CreateCategoryAsync(Owner(), Req("Фарби"), default)).IsOk); // корінь
    }

    [Fact]
    public async Task Rename_checks_siblings_not_itself_and_keeps_parent()
    {
        var e = new Env();
        var a = await Create(e, "Волосся");
        var child = await Create(e, "Фарби", a.Id);
        await Create(e, "Шампуні", a.Id);
        var same = await e.Svc.UpdateCategoryAsync(Owner(), child.Id, Req("ФАРБИ", sort: 5), default); // зміна регістру власного імені - ОК
        Assert.True(same.IsOk);
        Assert.Equal("ФАРБИ", same.Value!.Name);
        Assert.Equal(5, same.Value.SortOrder);
        Assert.Equal(a.Id, same.Value.ParentId);
        AssertErr(await e.Svc.UpdateCategoryAsync(Owner(), child.Id, Req("шампуні"), default), "category_name_taken");
        AssertErr(await e.Svc.UpdateCategoryAsync(Owner(), child.Id, Req(""), default), "invalid_name");
        AssertErr(await e.Svc.UpdateCategoryAsync(Owner(), Guid.NewGuid(), Req("X"), default), "category_not_found", ErrorKind.NotFound);
        // sortOrder не передано - лишається
        Assert.Equal(5, (await e.Svc.UpdateCategoryAsync(Owner(), child.Id, Req("Фарби"), default)).Value!.SortOrder);
    }

    [Fact]
    public async Task Move_rejects_cycles_and_overdeep_subtrees_and_accepts_valid_moves()
    {
        var e = new Env();
        var a = await Create(e, "A");
        var b = await Create(e, "B", a.Id);
        var c = await Create(e, "C", b.Id);
        var other = await Create(e, "Other");

        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), a.Id, new(a.Id, null), default), "category_cycle", ErrorKind.Validation);   // сам під себе
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), a.Id, new(c.Id, null), default), "category_cycle");                          // предок під нащадка
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), a.Id, new(b.Id, null), default), "category_cycle");
        // піддерево A (висота 3) під Other (рівень 1) = 4 рівні
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), a.Id, new(other.Id, null), default), "category_depth");
        // B (висота 2) під Other = 3 рівні - ОК
        var moved = await e.Svc.MoveCategoryAsync(Owner(), b.Id, new(other.Id, 7), default);
        Assert.True(moved.IsOk);
        Assert.Equal(other.Id, moved.Value!.ParentId);
        Assert.Equal(7, moved.Value.SortOrder);
        Assert.Equal(1, moved.Value.ChildCount); // C їде разом із B
        Assert.Equal(b.Id, e.Store.Categories[c.Id].ParentId);
        // у корінь
        var root = await e.Svc.MoveCategoryAsync(Owner(), c.Id, new(null, null), default);
        Assert.True(root.IsOk);
        Assert.Null(root.Value!.ParentId);
        // піддерево Other (Other > B) під A (рівень 1): висота 2 + 1 = 3 - ОК; A > Other > B
        Assert.True((await e.Svc.MoveCategoryAsync(Owner(), other.Id, new(a.Id, null), default)).IsOk);
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), Guid.NewGuid(), new(null, null), default), "category_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), c.Id, new(Guid.NewGuid(), null), default), "category_not_found");
    }

    [Fact]
    public async Task Move_checks_duplicate_in_new_parent_and_archived_parent()
    {
        var e = new Env();
        var a = await Create(e, "A");
        var b = await Create(e, "B");
        await Create(e, "Same", a.Id);
        var s2 = await Create(e, "same", b.Id);
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), s2.Id, new(a.Id, null), default), "category_name_taken", ErrorKind.Conflict);
        var arch = e.Store.AddCategory("Arch", archived: true);
        AssertErr(await e.Svc.MoveCategoryAsync(Owner(), s2.Id, new(arch.Id, null), default), "category_archived");
        // той самий архівний батько не блокує зміну sortOrder
        var inArch = e.Store.AddCategory("InArch", arch.Id);
        Assert.True((await e.Svc.MoveCategoryAsync(Owner(), inArch.Id, new(arch.Id, 3), default)).IsOk);
    }

    [Fact]
    public async Task Archive_and_unarchive_are_idempotent_do_not_cascade_and_hide_from_default_list()
    {
        var e = new Env();
        var a = await Create(e, "A");
        var child = await Create(e, "Child", a.Id);

        var r = await e.Svc.SetCategoryArchivedAsync(Owner(), a.Id, true, default);
        Assert.True(r.Value!.IsArchived);
        Assert.True((await e.Svc.SetCategoryArchivedAsync(Owner(), a.Id, true, default)).IsOk);
        Assert.False(e.Store.Categories[child.Id].IsArchived); // без каскаду

        var visible = (await e.Svc.ListCategoryNodesAsync(Owner(), false, default)).Value!;
        Assert.Equal([child.Id], visible.Select(x => x.Id).ToArray());
        Assert.Equal(2, (await e.Svc.ListCategoryNodesAsync(Owner(), true, default)).Value!.Count);

        Assert.False((await e.Svc.SetCategoryArchivedAsync(Owner(), a.Id, false, default)).Value!.IsArchived);
        AssertErr(await e.Svc.SetCategoryArchivedAsync(Owner(), Guid.NewGuid(), true, default), "category_not_found", ErrorKind.NotFound);
    }

    [Fact]
    public async Task List_is_ordered_by_parent_sortOrder_name_with_counts()
    {
        var e = new Env();
        var z = await Create(e, "Zeta");
        await e.Svc.UpdateCategoryAsync(Owner(), z.Id, Req("Zeta", sort: 1), default);
        var b = await Create(e, "Beta");
        var a = await Create(e, "alpha");
        await Create(e, "Kid", b.Id);
        var list = (await e.Svc.ListCategoryNodesAsync(Owner(), false, default)).Value!;
        Assert.Equal(["alpha", "Beta", "Zeta", "Kid"], list.Select(x => x.Name).ToArray()); // корені (sort 0: alpha, Beta; sort 1: Zeta), далі діти
        Assert.Equal(1, list.Single(x => x.Id == b.Id).ChildCount);
        Assert.Equal(0, list.Single(x => x.Id == a.Id).ChildCount);
    }

    [Fact]
    public async Task Delete_refuses_non_empty_and_removes_empty_and_not_found()
    {
        var e = new Env();
        var a = await Create(e, "A");
        var child = await Create(e, "Child", a.Id);
        AssertErr(await e.Svc.DeleteCategoryAsync(Owner(), a.Id, default), "category_not_empty", ErrorKind.Conflict);
        var p = e.Store.AddProduct("Dye");
        e.Store.Products[p.Id] = p with { CategoryId = child.Id, IsActive = false }; // навіть неактивний товар блокує
        AssertErr(await e.Svc.DeleteCategoryAsync(Owner(), child.Id, default), "category_not_empty");
        e.Store.Products.Remove(p.Id);
        Assert.True((await e.Svc.DeleteCategoryAsync(Owner(), child.Id, default)).IsOk);
        Assert.True((await e.Svc.DeleteCategoryAsync(Owner(), a.Id, default)).IsOk);
        AssertErr(await e.Svc.DeleteCategoryAsync(Owner(), a.Id, default), "category_not_found", ErrorKind.NotFound);
    }

    [Fact]
    public async Task Limit_of_500_categories_per_tenant()
    {
        var e = new Env();
        for (var i = 0; i < InventoryService.MaxCategories; i++) e.Store.AddCategory("C" + i);
        AssertErr(await e.Svc.CreateCategoryAsync(Owner(), Req("One more"), default), "category_limit", ErrorKind.Validation);
        // переміщення й перейменування існуючих працюють на ліміті
        Assert.True((await e.Svc.UpdateCategoryAsync(Owner(), e.Store.Categories.Keys.First(), Req("Renamed"), default)).IsOk);
    }

    [Fact]
    public async Task Permissions_read_needs_view_write_needs_manage_all()
    {
        var e = new Env();
        var a = await Create(e, "A");
        var spec = Specialist(); // view=own, manage=none
        Assert.True((await e.Svc.ListCategoryNodesAsync(spec, false, default)).IsOk);
        AssertErr(await e.Svc.CreateCategoryAsync(spec, Req("X"), default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.UpdateCategoryAsync(spec, a.Id, Req("X"), default), "forbidden_role");
        AssertErr(await e.Svc.MoveCategoryAsync(spec, a.Id, new(null, null), default), "forbidden_role");
        AssertErr(await e.Svc.SetCategoryArchivedAsync(spec, a.Id, true, default), "forbidden_role");
        AssertErr(await e.Svc.DeleteCategoryAsync(spec, a.Id, default), "forbidden_role");
        var none = Actor.ForTest(Roles.Specialist, Guid.NewGuid(), permissions: PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Key == "inventory.view" ? "none" : kv.Value));
        AssertErr(await e.Svc.ListCategoryNodesAsync(none, false, default), "forbidden_role");
    }

    // ---------- товар із categoryId ----------

    private static ProductRequest Prod(string name, Guid? categoryId, string? text = null) =>
        new(name, text, "pcs", null, "both", null, null, 10m, null, null, null, null, categoryId);

    [Fact]
    public async Task Product_with_category_validates_existence_archive_and_returns_path_and_text_category_still_works()
    {
        var e = new Env();
        var hair = await Create(e, "Волосся");
        var dye = await Create(e, "Фарби", hair.Id);

        var ok = await e.Svc.CreateProductAsync(Owner(), Prod("Dye 1", dye.Id, text: "Hair"), default);
        Assert.True(ok.IsOk, ok.Error?.Code);
        Assert.Equal(dye.Id, ok.Value!.CategoryId);
        Assert.Equal("Волосся › Фарби", ok.Value.CategoryPath);
        Assert.Equal("Hair", ok.Value.Category); // текстова категорія лишається як є
        Assert.Equal(1, (await e.Svc.ListCategoryNodesAsync(Owner(), true, default)).Value!.Single(x => x.Id == dye.Id).ProductCount);

        var noCat = await e.Svc.CreateProductAsync(Owner(), Prod("Plain", null, text: "Hair"), default);
        Assert.Null(noCat.Value!.CategoryId);
        Assert.Null(noCat.Value.CategoryPath);

        AssertErr(await e.Svc.CreateProductAsync(Owner(), Prod("Ghost", Guid.NewGuid()), default), "category_not_found", ErrorKind.Validation);

        // архівна: призначити не можна; товар, що вже в ній, редагується без зміни categoryId
        await e.Svc.SetCategoryArchivedAsync(Owner(), dye.Id, true, default);
        AssertErr(await e.Svc.CreateProductAsync(Owner(), Prod("Dye 2", dye.Id), default), "category_archived", ErrorKind.Validation);
        var edit = await e.Svc.UpdateProductAsync(Owner(), ok.Value.Id, Prod("Dye 1 renamed", dye.Id), default);
        Assert.True(edit.IsOk, edit.Error?.Code);
        Assert.Equal("Волосся › Фарби", edit.Value!.CategoryPath); // шлях архівної категорії теж повертається
        // переведення в іншу архівну - ні; зняття категорії - так
        var arch2 = e.Store.AddCategory("Arch2", archived: true);
        AssertErr(await e.Svc.UpdateProductAsync(Owner(), ok.Value.Id, Prod("Dye 1 renamed", arch2.Id), default), "category_archived");
        var cleared = await e.Svc.UpdateProductAsync(Owner(), ok.Value.Id, Prod("Dye 1 renamed", null), default);
        Assert.Null(cleared.Value!.CategoryId);
    }
}
