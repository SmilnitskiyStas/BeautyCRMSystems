using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// Логотип закладу (контракт .ai/artifacts/location-logo): PUT/DELETE /api/beauty/locations/{id}/logo на реальному PostgreSQL (RLS) через повний
/// HTTP-конвеєр з тимчасовою текою як томом; logoUrl у CRM і в публічному каталозі (resolve / search). Конвеєр обробки - ProductImageProcessorTests.
/// </summary>
public sealed class LocationLogoApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private readonly List<string> _dirs = [];
    private const string Loc = "/api/beauty/locations";

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

    private Host NewHost(bool enabled = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "beauty-logo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            if (enabled)
            {
                b.UseSetting("Media:RootPath", dir);
                b.UseSetting("Media:PublicBaseUrl", "https://beauty.test");
            }
            b.UseSetting("Media:UploadsPerUserPerMinute", "120");
        });
        _disposables.Add(f);
        return new Host(f, f.CreateClient(), dir);
    }

    private sealed record Tn(TenantSeed Seed, string Owner, string Spec, Guid Location);

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        return new Tn(t, owner.AccessToken, spec.AccessToken, seed.LocationId);
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string url, string? token, HttpContent? content = null)
    {
        var r = new HttpRequestMessage(m, url) { Content = content };
        if (token is not null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(r);
    }

    private static MultipartFormDataContent Form(byte[] bytes, string part = "file") =>
        new() { { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, part, "logo.jpg" } };

    private static Task<HttpResponseMessage> Put(HttpClient c, string? token, Guid id, byte[] bytes, string part = "file") =>
        Send(c, HttpMethod.Put, $"{Loc}/{id}/logo", token, Form(bytes, part));

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

    private static string KeyOf(string url) => url[(url.LastIndexOf('/') + 1)..].Split('-')[0];

    private static async Task<JsonElement> ListedAsync(HttpClient c, string token, Guid id)
    {
        var list = await Ok(await Send(c, HttpMethod.Get, Loc, token));
        return list.EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == id);
    }

    [SkippableFact]
    public async Task Without_RootPath_routes_are_404_media_disabled_and_logoUrl_is_null()
    {
        NeedDb();
        var h = NewHost(enabled: false);
        var t = await NewTenantAsync();
        await Fails(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(300, 300)), HttpStatusCode.NotFound, "media_disabled");
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Loc}/{t.Location}/logo", t.Owner), HttpStatusCode.NotFound, "media_disabled");
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(h.Client, t.Owner, t.Location)).GetProperty("logoUrl").ValueKind);
        Assert.Empty(h.Files("p"));
    }

    [SkippableFact]
    public async Task Permissions_edit_required_specialist_403_anonymous_401_unknown_location_404()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync();
        var bytes = Images.Jpeg(300, 300);
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(h.Client, t.Spec, t.Location, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(h.Client, HttpMethod.Delete, $"{Loc}/{t.Location}/logo", t.Spec)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Put(h.Client, null, t.Location, bytes)).StatusCode);
        Assert.Empty(h.Files("p"));
        await Fails(await Put(h.Client, t.Owner, Guid.NewGuid(), bytes), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Send(h.Client, HttpMethod.Delete, $"{Loc}/{Guid.NewGuid()}/logo", t.Owner), HttpStatusCode.NotFound, "location_not_found");
        Assert.Empty(h.Files("p"));
    }

    [SkippableFact]
    public async Task Upload_replace_and_delete_manage_files_and_logoUrl_idempotently()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync();

        var first = await Ok(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(1200, 800)));
        var url1 = first.GetProperty("logoUrl").GetString()!;
        var key1 = KeyOf(url1);
        Assert.Equal($"https://beauty.test/api/public/media/p/{key1}-400.webp", url1);
        Assert.Equal(2, h.Files("p").Length); // 400 + 1600
        Assert.Equal(url1, (await ListedAsync(h.Client, t.Owner, t.Location)).GetProperty("logoUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, (await h.Client.GetAsync(new Uri(url1).AbsolutePath)).StatusCode);

        var second = await Ok(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(900, 900)));
        var url2 = second.GetProperty("logoUrl").GetString()!;
        Assert.NotEqual(url1, url2);
        var files = h.Files("p");
        Assert.Equal(2, files.Length); // попередній логотип видалено після commit
        Assert.All(files, f => Assert.Contains(KeyOf(url2), f));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync(new Uri(url1).AbsolutePath)).StatusCode);

        var deleted = await Ok(await Send(h.Client, HttpMethod.Delete, $"{Loc}/{t.Location}/logo", t.Owner));
        Assert.Equal(JsonValueKind.Null, deleted.GetProperty("logoUrl").ValueKind);
        Assert.Empty(h.Files("p"));
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(h.Client, t.Owner, t.Location)).GetProperty("logoUrl").ValueKind);
        var again = await Ok(await Send(h.Client, HttpMethod.Delete, $"{Loc}/{t.Location}/logo", t.Owner));
        Assert.Equal(JsonValueKind.Null, again.GetProperty("logoUrl").ValueKind);
    }

    [SkippableFact]
    public async Task Invalid_uploads_are_rejected_without_leaving_files()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync();
        await Fails(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(300, 300), part: "other"), HttpStatusCode.UnprocessableEntity, "file_required");
        await Fails(await Put(h.Client, t.Owner, t.Location, "not an image"u8.ToArray()), HttpStatusCode.UnsupportedMediaType, "unsupported_image_type");
        await Fails(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(50, 50)), HttpStatusCode.UnprocessableEntity, "image_dimensions_invalid");
        Assert.Empty(h.Files("p"));
        Assert.Equal(JsonValueKind.Null, (await ListedAsync(h.Client, t.Owner, t.Location)).GetProperty("logoUrl").ValueKind);
    }

    [SkippableFact]
    public async Task Public_catalog_resolve_and_search_return_logoUrl_and_null_without_logo()
    {
        NeedDb();
        var h = NewHost();
        var t = await NewTenantAsync();
        var city = "Logo" + Guid.NewGuid().ToString("N")[..12];
        var listed = await ListedAsync(h.Client, t.Owner, t.Location);
        var code = listed.GetProperty("publicCode").GetString()!;
        await Ok(await Send(h.Client, HttpMethod.Put, $"{Loc}/{t.Location}", t.Owner,
            System.Net.Http.Json.JsonContent.Create(new { name = listed.GetProperty("name").GetString(), timezone = "Europe/Kyiv", city, isDiscoverable = true }, options: Json)));

        var before = await Ok(await h.Client.GetAsync("/api/public/resolve/" + code));
        Assert.Equal(JsonValueKind.Null, before.GetProperty("logoUrl").ValueKind);

        var url = (await Ok(await Put(h.Client, t.Owner, t.Location, Images.Jpeg(600, 600)))).GetProperty("logoUrl").GetString()!;
        var resolved = await Ok(await h.Client.GetAsync("/api/public/resolve/" + code));
        Assert.Equal(url, resolved.GetProperty("logoUrl").GetString());

        var search = await Ok(await h.Client.PostAsync("/api/public/locations/search", System.Net.Http.Json.JsonContent.Create(new { city }, options: Json)));
        var item = search.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(url, item.GetProperty("logoUrl").GetString());
    }
}
