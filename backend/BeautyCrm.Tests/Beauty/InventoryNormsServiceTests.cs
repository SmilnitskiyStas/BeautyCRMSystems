using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-884 (§32.16): норми матеріалів - сервіс на <see cref="FakeNormsStore"/>: права/роль, валідація, scope, порядок перевірок. Час - FakeClock.</summary>
public sealed class InventoryNormsServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Env
    {
        public readonly FakeNormsStore Store = new();
        public readonly FakeClock Clock = new(Noon);
        public readonly InventoryNormsService Svc;
        public readonly LocationRow Loc, Loc2;
        public readonly ServiceRow Service;
        public readonly ProductDto Dye;
        public Env()
        {
            Svc = new InventoryNormsService(Store, Clock);
            Loc = Store.AddLocation("Main");
            Loc2 = Store.AddLocation("Second");
            Service = Store.AddService();
            Dye = Store.AddProduct("Dye");
        }
    }

    private static Actor Admin() => Actor.ForTest(Roles.Admin);

    private static Actor With(string role, Guid? specialist, params (string Key, string Level)[] overrides)
    {
        var p = PermissionPresets.For(role).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, l) in overrides) p[k] = l;
        return Actor.ForTest(role, specialist, permissions: p);
    }

    private static MaterialLineRequest L(Guid p, decimal q) => new(p, q);
    private static PutMaterialsRequest Put(Guid? loc, params MaterialLineRequest[] lines) => new(loc, lines);

    private static void AssertErr<T>(Result<T> r, string code, ErrorKind kind, int? line = null)
    {
        Assert.False(r.IsOk, "expected error " + code);
        Assert.Equal(code, r.Error!.Code);
        Assert.Equal(kind, r.Error.Kind);
        if (line is { } l) Assert.Equal(l, (int)r.Error.Extras!["line"]!);
    }

    [Fact]
    public async Task Put_network_set_then_get_returns_it_with_updatedBy_and_no_locations()
    {
        var e = new Env();
        var water = e.Store.AddProduct("Water", "l");
        var r = await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 30), L(water.Id, 0.5m)), default);

        Assert.True(r.IsOk);
        Assert.Null(r.Value!.LocationId);
        Assert.Equal(["Dye", "Water"], r.Value.Lines.Select(l => l.Product.Name));
        Assert.Equal(Noon, r.Value.UpdatedAt);

        var g = await e.Svc.GetAsync(Admin(), e.Service.Id, default);
        Assert.NotNull(g.Value!.Network);
        Assert.Empty(g.Value.Locations);
        Assert.Equal(e.Service.Name, g.Value.ServiceName);
    }

    [Fact]
    public async Task Put_replaces_the_whole_set_and_is_idempotent()
    {
        var e = new Env();
        var water = e.Store.AddProduct("Water", "l");
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 30), L(water.Id, 1)), default);
        var again = Put(null, L(water.Id, 2));
        await e.Svc.PutAsync(Admin(), e.Service.Id, again, default);
        await e.Svc.PutAsync(Admin(), e.Service.Id, again, default);

        var set = Assert.Single(e.Store.Sets);
        Assert.Equal([water.Id], set.Lines.Select(l => l.ProductId));
    }

    [Fact]
    public async Task Override_with_empty_lines_is_allowed_but_network_empty_is_not()
    {
        var e = new Env();
        var ok = await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc.Id), default);
        Assert.True(ok.IsOk);
        Assert.Empty(ok.Value!.Lines);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null), default), "invalid_lines", ErrorKind.Validation);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, new PutMaterialsRequest(null, null), default), "invalid_lines", ErrorKind.Validation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100000.001")]
    [InlineData("1.2345")]
    public async Task Invalid_quantity_is_422_with_line(string qty)
    {
        var e = new Env();
        var water = e.Store.AddProduct("Water", "l");
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(water.Id, 1), L(e.Dye.Id, decimal.Parse(qty, System.Globalization.CultureInfo.InvariantCulture))), default),
            "invalid_quantity", ErrorKind.Validation, 1);
    }

    [Fact]
    public async Task Quantity_boundaries_100000_and_three_decimals_pass()
    {
        var e = new Env();
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 100000m)), default)).IsOk);
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 0.001m)), default)).IsOk);
    }

    [Fact]
    public async Task Fractional_quantity_for_integer_unit_is_invalid()
    {
        var e = new Env();
        var gloves = e.Store.AddProduct("Gloves", "pcs");
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(gloves.Id, 1.5m)), default), "invalid_quantity", ErrorKind.Validation, 0);
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(gloves.Id, 2m)), default)).IsOk);
    }

    [Fact]
    public async Task More_than_50_lines_and_duplicates_are_rejected()
    {
        var e = new Env();
        var many = Enumerable.Range(0, 51).Select(i => L(e.Store.AddProduct("P" + i).Id, 1)).ToArray();
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, many), default), "too_many_lines", ErrorKind.Validation);
        var fifty = many.Take(50).ToArray();
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, fifty), default)).IsOk);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1), L(e.Dye.Id, 2)), default), "duplicate_line", ErrorKind.Validation, 1);
    }

    [Fact]
    public async Task Sale_only_and_inactive_products_are_rejected_with_line()
    {
        var e = new Env();
        var sale = e.Store.AddProduct("Retail", purpose: "sale");
        var off = e.Store.AddProduct("Old", active: false);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1), L(sale.Id, 1)), default), "product_not_for_salon_use", ErrorKind.Validation, 1);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(off.Id, 1)), default), "product_not_for_salon_use", ErrorKind.Validation, 0);
        var salon = e.Store.AddProduct("Salon", purpose: "salon_use");
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(salon.Id, 1)), default)).IsOk);
    }

    [Fact]
    public async Task Product_disabled_in_location_blocks_only_that_locations_override()
    {
        var e = new Env();
        e.Store.Disabled.Add((e.Loc.Id, e.Dye.Id));
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc.Id, L(e.Dye.Id, 1)), default), "product_disabled_for_location", ErrorKind.Validation, 0);
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc2.Id, L(e.Dye.Id, 1)), default)).IsOk);
        Assert.True((await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1)), default)).IsOk);
    }

    [Fact]
    public async Task Not_found_service_location_product_and_check_order()
    {
        var e = new Env();
        AssertErr(await e.Svc.PutAsync(Admin(), Guid.NewGuid(), Put(null, L(e.Dye.Id, 1)), default), "service_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(Guid.NewGuid(), L(e.Dye.Id, 1)), default), "location_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1), L(Guid.NewGuid(), 1)), default), "product_not_found", ErrorKind.NotFound, 1);
        // форма (422) раніше за існування послуги (404)
        AssertErr(await e.Svc.PutAsync(Admin(), Guid.NewGuid(), Put(null), default), "invalid_lines", ErrorKind.Validation);
    }

    [Fact]
    public async Task Rejection_under_lock_is_mapped_to_the_same_codes()
    {
        var e = new Env();
        e.Store.UnderLock = () => ("product_not_for_salon_use", 0);
        AssertErr(await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1)), default), "product_not_for_salon_use", ErrorKind.Validation, 0);
        Assert.Empty(e.Store.Sets);
    }

    [Fact]
    public async Task Only_owner_admin_with_manage_all_may_change()
    {
        var e = new Env();
        Assert.True((await e.Svc.PutAsync(Actor.ForTest(Roles.Owner), e.Service.Id, Put(null, L(e.Dye.Id, 1)), default)).IsOk);
        var deputy = With(Roles.Deputy, null, ("inventory.manage", "all"));
        AssertErr(await e.Svc.PutAsync(deputy, e.Service.Id, Put(null, L(e.Dye.Id, 1)), default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.DeleteAsync(deputy, e.Service.Id, null, default), "forbidden_role", ErrorKind.Forbidden);
        var adminOwn = With(Roles.Admin, null, ("inventory.manage", "own"));
        AssertErr(await e.Svc.PutAsync(adminOwn, e.Service.Id, Put(null, L(e.Dye.Id, 1)), default), "forbidden_role", ErrorKind.Forbidden);
        var spec = With(Roles.Specialist, Guid.NewGuid());
        AssertErr(await e.Svc.PutAsync(spec, e.Service.Id, Put(null, L(e.Dye.Id, 1)), default), "forbidden_role", ErrorKind.Forbidden);
    }

    [Fact]
    public async Task View_requires_inventory_view_own()
    {
        var e = new Env();
        var none = With(Roles.Admin, null, ("inventory.view", "none"));
        AssertErr(await e.Svc.GetAsync(none, e.Service.Id, default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.ListAsync(none, false, default), "forbidden_role", ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Own_scope_sees_network_and_only_own_location_overrides()
    {
        var e = new Env();
        var specId = Guid.NewGuid();
        e.Store.SpecLocations[specId] = [e.Loc.Id];
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1)), default);
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc.Id, L(e.Dye.Id, 2)), default);
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc2.Id, L(e.Dye.Id, 3)), default);

        var spec = With(Roles.Specialist, specId);
        var own = (await e.Svc.GetAsync(spec, e.Service.Id, default)).Value!;
        Assert.NotNull(own.Network);
        Assert.Equal([e.Loc.Id], own.Locations.Select(l => l.LocationId!.Value));
        Assert.Equal("Main", own.Locations[0].LocationName);

        var all = (await e.Svc.GetAsync(Admin(), e.Service.Id, default)).Value!;
        Assert.Equal(2, all.Locations.Count);
    }

    [Fact]
    public async Task List_returns_only_services_with_sets_sorted_and_filters_inactive()
    {
        var e = new Env();
        var b = e.Store.AddService("beta");
        var a = e.Store.AddService("Alpha");
        var off = e.Store.AddService("Zeta", active: false);
        e.Store.AddService("Empty");
        foreach (var s in new[] { b, a, off }) await e.Svc.PutAsync(Admin(), s.Id, Put(null, L(e.Dye.Id, 1)), default);

        var active = (await e.Svc.ListAsync(Admin(), false, default)).Value!.Items;
        Assert.Equal(["Alpha", "beta"], active.Select(i => i.ServiceName));
        var all = (await e.Svc.ListAsync(Admin(), true, default)).Value!.Items;
        Assert.Equal(["Alpha", "beta", "Zeta"], all.Select(i => i.ServiceName));
    }

    [Fact]
    public async Task Get_unknown_service_and_empty_service_shape()
    {
        var e = new Env();
        AssertErr(await e.Svc.GetAsync(Admin(), Guid.NewGuid(), default), "service_not_found", ErrorKind.NotFound);
        var g = (await e.Svc.GetAsync(Admin(), e.Service.Id, default)).Value!;
        Assert.Null(g.Network);
        Assert.Empty(g.Locations);
    }

    [Fact]
    public async Task Delete_removes_set_and_missing_set_is_404()
    {
        var e = new Env();
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(null, L(e.Dye.Id, 1)), default);
        await e.Svc.PutAsync(Admin(), e.Service.Id, Put(e.Loc.Id), default);

        Assert.True((await e.Svc.DeleteAsync(Admin(), e.Service.Id, e.Loc.Id, default)).IsOk);
        Assert.Single(e.Store.Sets);
        AssertErr(await e.Svc.DeleteAsync(Admin(), e.Service.Id, e.Loc.Id, default), "materials_not_found", ErrorKind.NotFound);
        Assert.True((await e.Svc.DeleteAsync(Admin(), e.Service.Id, null, default)).IsOk);
        AssertErr(await e.Svc.DeleteAsync(Admin(), Guid.NewGuid(), null, default), "service_not_found", ErrorKind.NotFound);
        AssertErr(await e.Svc.DeleteAsync(Admin(), e.Service.Id, Guid.NewGuid(), default), "location_not_found", ErrorKind.NotFound);
    }
}
