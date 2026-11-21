using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyProductImages;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-912 (ADR-027, §33.22.9 п.3-4): CRM-маршрути фото товарів і публічна роздача на реальному PostgreSQL (RLS) через повний HTTP-конвеєр,
/// з тимчасовою текою як томом. Обробка зображень - окремо в <see cref="ProductImageProcessorTests"/>.
/// </summary>
public sealed class ProductImagesApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _dirs = [];
    private const string Inv = "/api/beauty/inventory";

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private sealed record Host(WebApplicationFactory<Program> Factory, HttpClient Client, string Dir)
    {
        public string[] Files(string sub) => Directory.Exists(Path.Combine(Dir, sub))
            ? Directory.GetFiles(Path.Combine(Dir, sub), "*", SearchOption.AllDirectories) : [];
    }

    private Host NewHost(Dictionary<string, string>? cfg = null, Action<IServiceCollection>? services = null, bool enabled = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "beauty-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (enabled)
            {
                b.UseSetting("Media:RootPath", dir);
                b.UseSetting("Media:PublicBaseUrl", "https://beauty.test");
            }
            b.UseSetting("Inventory:WritePerUserPerMinute", "600");
            b.UseSetting("Media:UploadsPerUserPerMinute", "120");
            foreach (var (k, v) in cfg ?? []) b.UseSetting(k, v);
            if (services is not null) b.ConfigureTestServices(services);
        });
        _disposables.Add(f);
        return new Host(f, f.CreateClient(), dir);
    }

    private sealed record Tn(TenantSeed Seed, string Owner, string Admin, string Spec, string SpecOwn, Guid P1, Guid P2)
    {
        public Guid Id => Seed.TenantId;
    }

    private async Task<Tn> NewTenantAsync(HttpClient c)
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var own = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        own["inventory.manage"] = "own";
        var specOwn = await fx.CreateUserAsync(t, Roles.Specialist, seed.SpecialistB, own);
        return new Tn(t, owner.AccessToken, admin.AccessToken, spec.AccessToken, specOwn.Tokens.AccessToken,
            await NewProductAsync(c, owner.AccessToken, "P1"), await NewProductAsync(c, owner.AccessToken, "P2"));
    }

    private static async Task<Guid> NewProductAsync(HttpClient c, string token, string name)
    {
        var r = await Send(c, HttpMethod.Post, Inv + "/products", token, JsonContent(new { name, unit = "pcs", purpose = "sale", salePrice = 10 }));
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    // ---------- HTTP ----------

    private static HttpContent JsonContent(object body) => System.Net.Http.Json.JsonContent.Create(body, options: Json);

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, string? token, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = content };
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(r);
    }

    private static MultipartFormDataContent Form(byte[] bytes, string part = "file", string name = "photo.jpg") =>
        new() { { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, part, name } };

    private static Task<HttpResponseMessage> Upload(HttpClient c, string token, Guid product, byte[] bytes, string part = "file") =>
        Send(c, HttpMethod.Post, $"{Inv}/products/{product}/images", token, Form(bytes, part));

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Detail(HttpClient c, string token, Guid product) =>
        await Ok(await Send(c, HttpMethod.Get, $"{Inv}/products/{product}", token));

    private static string KeyOf(string url) => url[(url.LastIndexOf('/') + 1)..].Split('-')[0];
    private static string PathOf(string url) => new Uri(url).AbsolutePath;

    // ---------- вимкнена функція ----------

    [SkippableFact]
    public async Task Without_RootPath_the_feature_is_off_routes_404_and_dtos_null_and_empty()
    {
        NeedDb();
        var h = NewHost(enabled: false);
        var t = await NewTenantAsync(h.Client);
        var r = await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(300, 300));
        await Fails(r, HttpStatusCode.NotFound, "product_images_disabled");
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{Guid.NewGuid()}", t.Owner), HttpStatusCode.NotFound, "product_images_disabled");
        await Fails(await Send(h.Client, HttpMethod.Patch, $"{Inv}/products/{t.P1}/images/order", t.Owner, JsonContent(new { imageIds = new[] { Guid.NewGuid() } })),
            HttpStatusCode.NotFound, "product_images_disabled");

        var d = await Detail(h.Client, t.Owner, t.P1);
        Assert.Empty(d.GetProperty("images").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("imageUrl").ValueKind);
        var list = await Ok(await Send(h.Client, HttpMethod.Get, Inv + "/products", t.Owner));
        Assert.All(list.GetProperty("items").EnumerateArray(), i => Assert.Equal(JsonValueKind.Null, i.GetProperty("imageUrl").ValueKind));
        var file = await h.Client.GetAsync("/api/public/media/p/" + new string('a', 32) + "-400.webp");
        Assert.Equal(HttpStatusCode.NotFound, file.StatusCode);
    }

    // ---------- права ----------

    [SkippableFact]
    public async Task Permissions_manage_all_required_specialists_get_403_owner_and_admin_allowed()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var bytes = Images.Jpeg(300, 300);
        foreach (var token in new[] { t.Spec, t.SpecOwn })
        {
            var r = await Upload(h.Client, token, t.P1, bytes);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            var b = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("forbidden_role", b.GetProperty("code").GetString());
            Assert.Equal("inventory.manage", b.GetProperty("permission").GetString());
            Assert.Equal("all", b.GetProperty("required").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{Guid.NewGuid()}", token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(h.Client, HttpMethod.Patch, $"{Inv}/products/{t.P1}/images/order", token, JsonContent(new { imageIds = Array.Empty<Guid>() }))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(h.Client, HttpMethod.Post, $"{Inv}/products/{t.P1}/images", null, Form(bytes))).StatusCode);
        Assert.Empty(h.Files("p"));
        await Ok(await Upload(h.Client, t.Owner, t.P1, bytes), HttpStatusCode.Created);
        await Ok(await Upload(h.Client, t.Admin, t.P1, bytes), HttpStatusCode.Created);
    }

    // ---------- upload, роздача, видалення ----------

    [SkippableFact]
    public async Task Upload_then_public_serving_headers_detail_list_and_delete_remove_files_after_commit()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var r = await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(3000, 2000));
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        var img = await Ok(r, HttpStatusCode.Created);
        var url = img.GetProperty("url").GetString()!;
        var thumb = img.GetProperty("thumbUrl").GetString()!;
        var key = KeyOf(url);
        Assert.Equal($"https://beauty.test/api/public/media/p/{key}-1600.webp", url);
        Assert.Equal($"https://beauty.test/api/public/media/p/{key}-400.webp", thumb);
        Assert.Equal(1600, img.GetProperty("width").GetInt32());
        Assert.Equal(1067, img.GetProperty("height").GetInt32());
        Assert.Equal(0, img.GetProperty("sortOrder").GetInt32());
        Assert.True(img.GetProperty("sizeBytes").GetInt32() > 0);

        // файли: два варіанти й маркер власника; tmp не лишилось
        var p = Path.Combine(h.Dir, "p", key[..2]);
        Assert.True(File.Exists(Path.Combine(p, $"{key}-400.webp")) && File.Exists(Path.Combine(p, $"{key}-1600.webp")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "t", t.Id.ToString("D"), key)));
        Assert.Empty(h.Files("p").Where(f => f.EndsWith(".tmp")));

        // деталь і список: галерея й прев'ю
        var d = await Detail(h.Client, t.Owner, t.P1);
        Assert.Equal(thumb, d.GetProperty("imageUrl").GetString());
        Assert.Equal(url, d.GetProperty("images")[0].GetProperty("url").GetString());
        var list = await Ok(await Send(h.Client, HttpMethod.Get, Inv + "/products", t.Owner));
        var items = list.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!);
        Assert.Equal(thumb, items["P1"].GetProperty("imageUrl").GetString());
        Assert.Equal(JsonValueKind.Null, items["P2"].GetProperty("imageUrl").ValueKind);

        // публічна роздача: без автентифікації, без заголовків застосунку
        var get = await h.Client.GetAsync(PathOf(thumb));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("image/webp", get.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", get.Headers.CacheControl?.ToString());
        Assert.Equal($"\"{key}-400\"", get.Headers.ETag?.Tag);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", get.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("cross-origin", get.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("inline", get.Content.Headers.ContentDisposition?.DispositionType);
        var body = await get.Content.ReadAsByteArrayAsync();
        using (var webp = Image.Load<Rgba32>(body)) Assert.Equal(400, webp.Width);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, PathOf(url)))).StatusCode);
        var cond = new HttpRequestMessage(HttpMethod.Get, PathOf(thumb)); cond.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{key}-400\""));
        Assert.Equal(HttpStatusCode.NotModified, (await h.Client.SendAsync(cond)).StatusCode);
        var range = new HttpRequestMessage(HttpMethod.Get, PathOf(thumb)); range.Headers.Range = new RangeHeaderValue(0, 9);
        var part = await h.Client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal(10, (await part.Content.ReadAsByteArrayAsync()).Length);

        // видалення: 204, файли зникли, повтор і чужий id - 404
        var id = img.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{id}", t.Owner)).StatusCode);
        Assert.Empty(h.Files("p"));
        Assert.Empty(h.Files("t"));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(PathOf(thumb))).StatusCode);
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{id}", t.Owner), HttpStatusCode.NotFound, "product_image_not_found");
        Assert.Empty((await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").EnumerateArray());
    }

    [SkippableFact]
    public async Task Public_serving_validation_returns_identical_404_no_store()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var img = await Ok(await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(400, 400)), HttpStatusCode.Created);
        var key = KeyOf(img.GetProperty("url").GetString()!);
        var bad = new[]
        {
            $"{key.ToUpperInvariant()}-400.webp", $"{key}-800.webp", $"{key}-400.jpg", $"{key}-400.webp.tmp", $"{key[..31]}-400.webp", $"{key}0-400.webp",
            $"t/{t.Id}/{key}", $"{key[..2]}/{key}-400.webp", "../" + key + "-400.webp", "..%2f" + key + "-400.webp", "%2e%2e/%2e%2e/x", $"{new string('b', 32)}-400.webp", "", " ",
        };
        foreach (var name in bad)
        {
            var ctx = await h.Factory.Server.SendAsync(c =>
            {
                c.Request.Method = "GET";
                c.Request.Path = "/api/public/media/p/" + name; // без нормалізації URI
            });
            Assert.True(ctx.Response.StatusCode == 404, $"{name}: {ctx.Response.StatusCode}");
            Assert.Equal("no-store", ctx.Response.Headers.CacheControl.ToString());
            using (var rd = new StreamReader(ctx.Response.Body)) Assert.Equal("", await rd.ReadToEndAsync());
        }
        // з відсутнім файлом так само; шлях до маркера не обслуговується
        var r = await h.Client.GetAsync($"/api/public/media/p/{new string('c', 32)}-1600.webp");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.NotEqual(HttpStatusCode.OK, (await h.Client.GetAsync($"/api/public/media/t/{t.Id}/{key}")).StatusCode); // t/ не обслуговується
    }

    [SkippableFact]
    public async Task Public_serving_refuses_symlinked_files()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var img = await Ok(await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(400, 400)), HttpStatusCode.Created);
        var key = KeyOf(img.GetProperty("url").GetString()!);
        var link = Path.Combine(h.Dir, "p", key[..2], new string('d', 32) + "-400.webp");
        try { File.CreateSymbolicLink(link, Path.Combine(h.Dir, "p", key[..2], $"{key}-400.webp")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Skip.If(true, "symlinks are not permitted in this environment"); }
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync($"/api/public/media/p/{new string('d', 32)}-400.webp")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync($"/api/public/media/p/{key}-400.webp")).StatusCode);
    }

    [SkippableFact]
    public async Task Public_media_route_does_not_touch_the_database_and_has_its_own_rate_limit()
    {
        NeedDb();
        var counter = new ConnectionCounter();
        var h = NewHost(new() { ["Media:PublicPermitPerMinute"] = "4" }, s =>
        {
            s.RemoveAll<DbContextOptions<BeautyDbContext>>();
            s.AddDbContext<BeautyDbContext>((sp, o) => o.UseBeautyNpgsql(fx.Db.AppConnectionString)
                .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>(), counter));
        });
        var t = await NewTenantAsync(h.Client); // контроль: звичайні маршрути рахуються
        var img = await Ok(await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(400, 400)), HttpStatusCode.Created);
        Assert.True(counter.Opened > 0);
        var path = PathOf(img.GetProperty("thumbUrl").GetString()!);
        var before = counter.Opened;
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await h.Client.GetAsync(path)).StatusCode);
        Assert.Equal(before, counter.Opened);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    private sealed class ConnectionCounter : DbConnectionInterceptor
    {
        public int Opened;
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        { Interlocked.Increment(ref Opened); return result; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken ct = default)
        { Interlocked.Increment(ref Opened); return ValueTask.FromResult(result); }
    }

    // ---------- порядок ----------

    [SkippableFact]
    public async Task Reorder_is_an_exact_permutation_and_idor_is_404()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var img = await Ok(await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(300 + i, 300)), HttpStatusCode.Created);
            Assert.Equal(i, img.GetProperty("sortOrder").GetInt32());
            ids.Add(img.GetProperty("id").GetGuid());
        }
        var other = await Ok(await Upload(h.Client, t.Owner, t.P2, Images.Jpeg(300, 300)), HttpStatusCode.Created);
        var otherId = other.GetProperty("id").GetGuid();
        Task<HttpResponseMessage> Order(Guid product, IEnumerable<Guid> order, string? token = null) =>
            Send(h.Client, HttpMethod.Patch, $"{Inv}/products/{product}/images/order", token ?? t.Owner, JsonContent(new { imageIds = order }));

        var perm = new[] { ids[2], ids[0], ids[1] };
        var ok = await Ok(await Order(t.P1, perm));
        Assert.Equal(perm, ok.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal([0, 1, 2], ok.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("sortOrder").GetInt32()).ToArray());
        Assert.Equal(perm, (await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());

        await Fails(await Order(t.P1, [ids[0], ids[0], ids[1]]), (HttpStatusCode)422, "invalid_image_order"); // дубль
        await Fails(await Order(t.P1, [ids[0], ids[1]]), (HttpStatusCode)422, "invalid_image_order"); // неповний
        await Fails(await Order(t.P1, [ids[0], ids[1], ids[2], ids[2]]), (HttpStatusCode)422, "invalid_image_order");
        await Fails(await Order(t.P1, [ids[0], ids[1], otherId]), (HttpStatusCode)422, "invalid_image_order"); // фото іншого товару
        await Fails(await Order(t.P1, [ids[0], ids[1], Guid.NewGuid()]), (HttpStatusCode)422, "invalid_image_order");
        await Fails(await Order(t.P1, []), (HttpStatusCode)422, "invalid_image_order");
        await Fails(await Order(Guid.NewGuid(), ids), HttpStatusCode.NotFound, "product_not_found");
        // порядок не змінився після відмов
        Assert.Equal(perm, (await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());
        // фото іншого товару того ж tenant при видаленні -> 404 product_image_not_found
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{otherId}", t.Owner), HttpStatusCode.NotFound, "product_image_not_found");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(PathOf(other.GetProperty("url").GetString()!))).StatusCode);
    }

    [SkippableFact]
    public async Task Foreign_tenant_cannot_see_upload_delete_or_reorder_images_of_another_tenant()
    {
        NeedDb();
        var h = NewHost();
        var a = await NewTenantAsync(h.Client);
        var b = await NewTenantAsync(h.Client);
        var img = await Ok(await Upload(h.Client, a.Owner, a.P1, Images.Jpeg(300, 300)), HttpStatusCode.Created);
        var id = img.GetProperty("id").GetGuid();
        await Fails(await Upload(h.Client, b.Owner, a.P1, Images.Jpeg(300, 300)), HttpStatusCode.NotFound, "product_not_found");
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{a.P1}/images/{id}", b.Owner), HttpStatusCode.NotFound, "product_not_found");
        await Fails(await Send(h.Client, HttpMethod.Patch, $"{Inv}/products/{a.P1}/images/order", b.Owner, JsonContent(new { imageIds = new[] { id } })), HttpStatusCode.NotFound, "product_not_found");
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{b.P1}/images/{id}", b.Owner), HttpStatusCode.NotFound, "product_image_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await Send(h.Client, HttpMethod.Get, $"{Inv}/products/{a.P1}", b.Owner)).StatusCode);
        Assert.Single((await Detail(h.Client, a.Owner, a.P1)).GetProperty("images").EnumerateArray());
        Assert.Single(h.Files("p"), f => f.EndsWith("-400.webp"));
    }

    // ---------- відмови upload ----------

    [SkippableFact]
    public async Task Invalid_uploads_are_rejected_without_files()
    {
        NeedDb();
        var h = NewHost(new() { ["Media:MaxUploadBytes"] = "1048576" });
        var t = await NewTenantAsync(h.Client);
        async Task Rejects(byte[] bytes, HttpStatusCode status, string code, string part = "file")
        {
            await Fails(await Upload(h.Client, t.Owner, t.P1, bytes, part), status, code);
            Assert.Empty(h.Files("p"));
            Assert.Empty(h.Files("t"));
        }
        await Rejects(Images.Gif(), (HttpStatusCode)415, "unsupported_image_type");
        await Rejects(Images.Svg(), (HttpStatusCode)415, "unsupported_image_type");
        await Rejects(Images.Heic(), (HttpStatusCode)415, "unsupported_image_type");
        await Rejects("<html><script>alert(1)</script></html>"u8.ToArray(), (HttpStatusCode)415, "unsupported_image_type");
        await Rejects(Images.Jpeg(199, 400), (HttpStatusCode)422, "image_dimensions_invalid");
        await Rejects(Images.Png(10_001, 300), (HttpStatusCode)422, "image_dimensions_invalid");
        await Rejects(Images.Bomb(), (HttpStatusCode)422, "image_dimensions_invalid");
        await Rejects(Images.Corrupt(), (HttpStatusCode)422, "image_decode_failed");
        await Rejects(Images.Jpeg(300, 300), (HttpStatusCode)422, "file_required", part: "other");
        await Rejects(Images.Noise(1_200_000), (HttpStatusCode)413, "file_too_large");
        // немає multipart взагалі
        await Fails(await Send(h.Client, HttpMethod.Post, $"{Inv}/products/{t.P1}/images", t.Owner, JsonContent(new { file = "x" })), (HttpStatusCode)422, "file_required");
        // порядок перевірок: товар перевіряється раніше за файл
        await Fails(await Upload(h.Client, t.Owner, Guid.NewGuid(), Images.Gif()), HttpStatusCode.NotFound, "product_not_found");
        Assert.Empty((await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").EnumerateArray());
    }

    [SkippableFact]
    public async Task Polyglot_trailing_html_is_dropped_because_only_the_encoder_output_is_stored()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync(h.Client);
        var poly = Images.Jpeg(300, 300).Concat("<script>alert(document.cookie)</script><svg onload=alert(1)>"u8.ToArray()).ToArray();
        var img = await Ok(await Upload(h.Client, t.Owner, t.P1, poly), HttpStatusCode.Created);
        var key = KeyOf(img.GetProperty("url").GetString()!);
        foreach (var f in h.Files("p"))
        {
            var bytes = await File.ReadAllBytesAsync(f);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.DoesNotContain("<script", System.Text.Encoding.Latin1.GetString(bytes));
            Assert.DoesNotContain("onload", System.Text.Encoding.Latin1.GetString(bytes));
        }
        Assert.Equal(2, h.Files("p").Length);
        _ = key;
    }

    [SkippableFact]
    public async Task Limits_per_product_and_per_tenant_and_parallel_last_slot_has_exactly_one_winner()
    {
        NeedDb();
        var h = NewHost(new() { ["Media:MaxImagesPerProduct"] = "2", ["Media:MaxImagesPerTenant"] = "3" });
        var t = await NewTenantAsync(h.Client);
        var bytes = Images.Jpeg(300, 300);
        await Ok(await Upload(h.Client, t.Owner, t.P1, bytes), HttpStatusCode.Created);
        // паралельні upload на останнє місце товару: рівно один успіх, файли тих, хто програв, прибрано
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Upload(h.Client, t.Owner, t.P1, bytes)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var r in results.Where(r => r.StatusCode != HttpStatusCode.Created))
            await Fails(r, (HttpStatusCode)422, "too_many_images");
        Assert.Equal(2, (await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").GetArrayLength());
        Assert.Equal(4, h.Files("p").Length);
        Assert.Equal(2, h.Files("t").Length);
        var full = await Upload(h.Client, t.Owner, t.P1, bytes);
        await Fails(full, (HttpStatusCode)422, "too_many_images");
        // квота tenant-а: 3-те фото другого товару проходить, 4-те - ні
        await Ok(await Upload(h.Client, t.Owner, t.P2, bytes), HttpStatusCode.Created);
        var quota = await Upload(h.Client, t.Owner, t.P2, bytes);
        await Fails(quota, (HttpStatusCode)422, "image_quota_exceeded");
        Assert.Equal(6, h.Files("p").Length);
    }

    // ---------- збої: файл -> транзакція, видалення після commit ----------

    private sealed class ThrowingStore(IProductImageStore inner) : IProductImageStore
    {
        public Task<bool> ProductExistsAsync(Guid p, CancellationToken ct) => inner.ProductExistsAsync(p, ct);
        public Task<(int Product, int Tenant)> CountsAsync(Guid p, CancellationToken ct) => inner.CountsAsync(p, ct);
        public Task<ImageWriteResult> AddAsync(Guid p, NewProductImage i, int a, int b, CancellationToken ct) => throw new InvalidOperationException("injected db failure");
        public Task<ImageWriteResult> DeleteAsync(Guid p, Guid i, CancellationToken ct) => inner.DeleteAsync(p, i, ct);
        public Task<ImageWriteResult> ReorderAsync(Guid p, IReadOnlyList<Guid> ids, CancellationToken ct) => inner.ReorderAsync(p, ids, ct);
    }

    [SkippableFact]
    public async Task Db_failure_after_files_were_written_leaves_neither_row_nor_files()
    {
        NeedDb();
        var h = NewHost(services: s => s.AddScoped<IProductImageStore>(sp => new ThrowingStore(sp.GetRequiredService<EfBeautyStore>())));
        var t = await NewTenantAsync(h.Client);
        try
        {
            var r = await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(300, 300));
            Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        }
        catch (InvalidOperationException) { } // TestServer пробрасує необроблений виняток
        Assert.Empty(h.Files("p"));
        Assert.Empty(h.Files("t"));
    }

    private sealed class FailingDeleteStorage(IProductImageStorage inner) : IProductImageStorage
    {
        public Task<bool> TryWriteAsync(Guid t, string key, IReadOnlyList<ImageVariantData> v, CancellationToken ct) => inner.TryWriteAsync(t, key, v, ct);
        public bool Delete(Guid t, string key) => throw new IOException("injected file delete failure");
    }

    [SkippableFact]
    public async Task File_delete_failure_after_commit_still_returns_204_and_leaves_files_for_the_sweeper()
    {
        NeedDb();
        var h = NewHost(services: s => s.AddSingleton<IProductImageStorage>(sp => new FailingDeleteStorage(sp.GetRequiredService<BeautyCrm.Infrastructure.Media.FileProductImageStorage>())));
        var t = await NewTenantAsync(h.Client);
        var img = await Ok(await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(300, 300)), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(h.Client, HttpMethod.Delete, $"{Inv}/products/{t.P1}/images/{img.GetProperty("id").GetGuid()}", t.Owner)).StatusCode);
        Assert.Empty((await Detail(h.Client, t.Owner, t.P1)).GetProperty("images").EnumerateArray());
        Assert.Equal(2, h.Files("p").Length);
        Assert.Single(h.Files("t"));
    }

    private sealed class BusyProcessor : IImageProcessor
    {
        public Task<Result<ProcessedImage>> ProcessAsync(Stream input, CancellationToken ct) =>
            Task.FromResult<Result<ProcessedImage>>(Error.Unavailable("media_busy", "busy"));
    }

    [SkippableFact]
    public async Task Busy_processor_gives_503_media_busy_with_retry_after_and_no_files()
    {
        NeedDb();
        var h = NewHost(services: s => s.AddSingleton<IImageProcessor>(new BusyProcessor()));
        var t = await NewTenantAsync(h.Client);
        var r = await Upload(h.Client, t.Owner, t.P1, Images.Jpeg(300, 300));
        await Fails(r, HttpStatusCode.ServiceUnavailable, "media_busy");
        Assert.Equal("5", r.Headers.RetryAfter?.Delta?.TotalSeconds.ToString());
        Assert.Empty(h.Files("p"));
    }

    [SkippableFact]
    public async Task Upload_is_rate_limited_per_user()
    {
        NeedDb();
        var h = NewHost(new() { ["Media:UploadsPerUserPerMinute"] = "2" });
        var t = await NewTenantAsync(h.Client);
        var bytes = Images.Jpeg(300, 300);
        await Ok(await Upload(h.Client, t.Owner, t.P1, bytes), HttpStatusCode.Created);
        await Ok(await Upload(h.Client, t.Owner, t.P1, bytes), HttpStatusCode.Created);
        await Fails(await Upload(h.Client, t.Owner, t.P1, bytes), (HttpStatusCode)429, "rate_limited");
        await Ok(await Upload(h.Client, t.Admin, t.P2, bytes), HttpStatusCode.Created); // ліміт за користувачем
    }

    [SkippableTheory]
    [InlineData("Media:MaxUploadBytes", "100")]
    [InlineData("Media:MaxImagesPerProduct", "21")]
    [InlineData("Media:SweepMode", "maybe")]
    [InlineData("Media:PublicBaseUrl", "http://beauty.test")] // поза Development лише https
    [InlineData("Media:PublicBaseUrl", "https://beauty.test/")]
    [InlineData("Media:PublicBaseUrl", "https://beauty.test/x")]
    [InlineData("Media:RootPath", "relative/path")]
    [InlineData("Media:RootPath", "/definitely/not/existing/volume")]
    public void Invalid_media_configuration_fails_startup(string key, string value)
    {
        NeedDb();
        var dir = Path.Combine(Path.GetTempPath(), "beauty-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Media:RootPath", dir);
            b.UseSetting("Media:PublicBaseUrl", "https://beauty.test");
            b.UseSetting(key, value);
        });
        _disposables.Add(f);
        Assert.ThrowsAny<Exception>(() => f.CreateClient());
    }
}
