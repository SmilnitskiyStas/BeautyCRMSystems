using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>Дерево категорій товарів складу (ADR-025, §32.27) на реальній PostgreSQL: RLS крос-tenant, права, модуль, глибина/цикл/дублікат, видалення, товар із categoryId.</summary>
public sealed class InventoryCategoriesApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient NewClient()
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.UseSetting("Inventory:WritePerUserPerMinute", "600"));
        _disposables.Add(f);
        return f.CreateClient();
    }

    private sealed record Tn(Guid Id, string Owner, string Admin, string Spec, string Deputy);

    private async Task<Tn> NewTenantAsync(string[]? modules = null)
    {
        var t = await fx.CreateTenantAsync(modules ?? [.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var deputy = await fx.CreateDeputyAsync(t);
        return new Tn(t.TenantId, owner.AccessToken, admin.AccessToken, spec.AccessToken, deputy.Tokens.AccessToken);
    }

    private static Task<HttpResponseMessage> Call(HttpClient c, HttpMethod m, string path, string? token, object? body = null)
    {
        var r = new HttpRequestMessage(m, "/api/beauty/inventory" + path);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(r);
    }

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<Guid> Cat(HttpClient c, string token, string name, Guid? parent = null) =>
        (await Ok(await Call(c, HttpMethod.Post, "/categories", token, new { name, parentId = parent }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    [SkippableFact]
    public async Task Tree_crud_depth_cycle_duplicate_archive_delete_over_http()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();

        var hair = await Cat(c, t.Admin, "Волосся");
        var dye = await Cat(c, t.Admin, "Фарби", hair);
        var brand = await Cat(c, t.Owner, "Бренд", dye);
        await Fails(await Call(c, HttpMethod.Post, "/categories", t.Admin, new { name = "Тон", parentId = brand }), HttpStatusCode.UnprocessableEntity, "category_depth");
        await Fails(await Call(c, HttpMethod.Post, "/categories", t.Admin, new { name = "ВОЛОССЯ" }), HttpStatusCode.Conflict, "category_name_taken");
        await Fails(await Call(c, HttpMethod.Post, "/categories", t.Admin, new { name = " " }), HttpStatusCode.UnprocessableEntity, "invalid_name");
        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/categories", t.Admin, new { name = "Фарби" })).StatusCode); // інший батько

        // rename + sortOrder
        var renamed = await Ok(await Call(c, HttpMethod.Put, $"/categories/{dye}", t.Admin, new { name = "Барвники", sortOrder = 4 }));
        Assert.Equal("Барвники", renamed.GetProperty("name").GetString());
        Assert.Equal(4, renamed.GetProperty("sortOrder").GetInt32());
        Assert.Equal(1, renamed.GetProperty("childCount").GetInt32());

        // цикл і глибина при move
        await Fails(await Call(c, HttpMethod.Put, $"/categories/{hair}/move", t.Admin, new { parentId = brand }), HttpStatusCode.UnprocessableEntity, "category_cycle");
        var other = await Cat(c, t.Admin, "Інше");
        await Fails(await Call(c, HttpMethod.Put, $"/categories/{hair}/move", t.Admin, new { parentId = other }), HttpStatusCode.UnprocessableEntity, "category_depth");
        var moved = await Ok(await Call(c, HttpMethod.Put, $"/categories/{brand}/move", t.Admin, new { parentId = (Guid?)null }));
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("parentId").ValueKind);

        // список: плоский, архівні ховаються за замовчуванням
        await Ok(await Call(c, HttpMethod.Post, $"/categories/{other}/archive", t.Admin));
        var visible = (await Ok(await Call(c, HttpMethod.Get, "/categories", t.Spec))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(other, visible);
        Assert.Contains(hair, visible);
        var all = (await Ok(await Call(c, HttpMethod.Get, "/categories?includeArchived=true", t.Spec))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(all, x => x.GetProperty("id").GetGuid() == other && x.GetProperty("isArchived").GetBoolean());
        Assert.False((await Ok(await Call(c, HttpMethod.Post, $"/categories/{other}/unarchive", t.Admin))).GetProperty("isArchived").GetBoolean());

        // видалення: непорожня -> 409, порожня -> 204, повторно -> 404
        await Fails(await Call(c, HttpMethod.Delete, $"/categories/{hair}", t.Admin), HttpStatusCode.Conflict, "category_not_empty");
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/categories/{other}", t.Admin)).StatusCode);
        await Fails(await Call(c, HttpMethod.Delete, $"/categories/{other}", t.Admin), HttpStatusCode.NotFound, "category_not_found");
    }

    [SkippableFact]
    public async Task Product_category_id_path_archived_and_delete_blocked_by_products()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var hair = await Cat(c, t.Admin, "Волосся");
        var dye = await Cat(c, t.Admin, "Фарби", hair);

        var p = await Ok(await Call(c, HttpMethod.Post, "/products", t.Admin,
            new { name = "Dye", unit = "pcs", purpose = "sale", salePrice = 10, category = "Hair", categoryId = dye }), HttpStatusCode.Created);
        var pid = p.GetProperty("id").GetGuid();
        Assert.Equal(dye, p.GetProperty("categoryId").GetGuid());
        Assert.Equal("Волосся › Фарби", p.GetProperty("categoryPath").GetString());
        Assert.Equal("Hair", p.GetProperty("category").GetString());

        var list = (await Ok(await Call(c, HttpMethod.Get, "/products", t.Spec))).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Волосся › Фарби", list.GetProperty("categoryPath").GetString());
        var node = (await Ok(await Call(c, HttpMethod.Get, "/categories", t.Spec))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == dye);
        Assert.Equal(1, node.GetProperty("productCount").GetInt32());

        // видалення блокується товаром (навіть неактивним), потім вільне
        await Ok(await Call(c, HttpMethod.Put, $"/products/{pid}", t.Admin, new { name = "Dye", unit = "pcs", purpose = "sale", salePrice = 10, categoryId = dye, isActive = false }));
        await Fails(await Call(c, HttpMethod.Delete, $"/categories/{dye}", t.Admin), HttpStatusCode.Conflict, "category_not_empty");

        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Ghost", unit = "pcs", purpose = "sale", salePrice = 10, categoryId = Guid.NewGuid() }),
            HttpStatusCode.UnprocessableEntity, "category_not_found");

        // архівна: призначити не можна, але існуючий товар редагується без зміни categoryId; шлях повертається
        await Ok(await Call(c, HttpMethod.Post, $"/categories/{dye}/archive", t.Admin));
        await Fails(await Call(c, HttpMethod.Post, "/products", t.Admin, new { name = "Dye2", unit = "pcs", purpose = "sale", salePrice = 10, categoryId = dye }),
            HttpStatusCode.UnprocessableEntity, "category_archived");
        var edited = await Ok(await Call(c, HttpMethod.Put, $"/products/{pid}", t.Admin, new { name = "Dye renamed", unit = "pcs", purpose = "sale", salePrice = 10, categoryId = dye }));
        Assert.Equal("Волосся › Фарби", edited.GetProperty("categoryPath").GetString());
        var cleared = await Ok(await Call(c, HttpMethod.Put, $"/products/{pid}", t.Admin, new { name = "Dye renamed", unit = "pcs", purpose = "sale", salePrice = 10 }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("categoryId").ValueKind);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/categories/{dye}", t.Admin)).StatusCode);
    }

    [SkippableFact]
    public async Task Cross_tenant_categories_are_invisible_and_immutable_and_parent_or_product_link_is_rejected()
    {
        NeedDb();
        var c = NewClient();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var foreign = await Cat(c, b.Owner, "Чужа");
        var own = await Cat(c, a.Owner, "Своя");

        var seen = (await Ok(await Call(c, HttpMethod.Get, "/categories?includeArchived=true", a.Owner))).GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([own], seen);
        await Fails(await Call(c, HttpMethod.Put, $"/categories/{foreign}", a.Owner, new { name = "X" }), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Put, $"/categories/{foreign}/move", a.Owner, new { parentId = (Guid?)null }), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Post, $"/categories/{foreign}/archive", a.Owner), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Delete, $"/categories/{foreign}", a.Owner), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Post, "/categories", a.Owner, new { name = "Child", parentId = foreign }), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Put, $"/categories/{own}/move", a.Owner, new { parentId = foreign }), HttpStatusCode.NotFound, "category_not_found");
        await Fails(await Call(c, HttpMethod.Post, "/products", a.Owner, new { name = "P", unit = "pcs", purpose = "sale", salePrice = 1, categoryId = foreign }),
            HttpStatusCode.UnprocessableEntity, "category_not_found");
        // чужий tenant лишився недоторканим
        Assert.Single((await Ok(await Call(c, HttpMethod.Get, "/categories", b.Owner))).GetProperty("items").EnumerateArray());
        // те саме ім'я в іншому tenant не конфліктує
        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/categories", a.Owner, new { name = "Чужа" })).StatusCode);
    }

    [SkippableFact]
    public async Task Permissions_specialist_reads_only_deputy_manages_and_disabled_module_gives_403()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        var id = await Cat(c, t.Admin, "A");

        Assert.Equal(HttpStatusCode.OK, (await Call(c, HttpMethod.Get, "/categories", t.Spec)).StatusCode);
        foreach (var (m, path) in new[]
                 {
                     (HttpMethod.Post, "/categories"), (HttpMethod.Put, $"/categories/{id}"), (HttpMethod.Put, $"/categories/{id}/move"),
                     (HttpMethod.Post, $"/categories/{id}/archive"), (HttpMethod.Post, $"/categories/{id}/unarchive"), (HttpMethod.Delete, $"/categories/{id}"),
                 })
            await Fails(await Call(c, m, path, t.Spec, m == HttpMethod.Delete ? null : new { name = "X" }), HttpStatusCode.Forbidden, "forbidden_role");
        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/categories", t.Deputy, new { name = "Deputy" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(c, HttpMethod.Get, "/categories", null)).StatusCode);

        var off = await NewTenantAsync(modules: [.. BeautyModules.All]); // без beauty_inventory
        foreach (var (m, path) in new[] { (HttpMethod.Get, "/categories"), (HttpMethod.Post, "/categories"), (HttpMethod.Delete, $"/categories/{id}") })
            foreach (var token in new[] { off.Owner, off.Admin, off.Spec })
                await Fails(await Call(c, m, path, token, m == HttpMethod.Post ? new { name = "X" } : null), HttpStatusCode.Forbidden, "module_disabled");
    }

    [SkippableFact]
    public async Task Limit_of_500_categories_per_tenant()
    {
        NeedDb();
        var c = NewClient();
        var t = await NewTenantAsync();
        await using (var ctx = fx.Db.CreateContext(t.Id))
        {
            for (var i = 0; i < 499; i++) ctx.Add(new BeautyCrm.Infrastructure.Data.Entities.ProductCategory { Name = "C" + i });
            await ctx.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Created, (await Call(c, HttpMethod.Post, "/categories", t.Owner, new { name = "Last" })).StatusCode);
        await Fails(await Call(c, HttpMethod.Post, "/categories", t.Owner, new { name = "Over" }), HttpStatusCode.UnprocessableEntity, "category_limit");
    }
}
