using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyInventory;
using BeautyCrm.Infrastructure.Data.Beauty;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-884/886 (ADR-025 §11, §32.16, §32.19) на реальній PostgreSQL (RLS, роль app) через повний HTTP-конвеєр: норми матеріалів (права, роль,
/// scope, валідація, purpose_locked/unit_locked, ізоляція tenant, модульний гейт, гонка першого створення), сповіщення (scope, статуси, лічильник,
/// пагінація, ізоляція) і writer (dedupe, без ПД). Годинник: час сповіщень задається тестом (без реального годинника в перевірках).
/// </summary>
public sealed class InventoryStage2aApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

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

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, Guid Loc2, string Owner, string Admin, string Spec, string Deputy)
    {
        public Guid Id => Seed.TenantId;
        public Guid L1 => Beauty.LocationId;
    }

    private async Task<Tn> NewTenantAsync(bool withInventory = true)
    {
        var t = await fx.CreateTenantAsync(withInventory ? [.. BeautyModules.Known] : [.. BeautyModules.Known.Where(m => m != BeautyModules.Inventory)]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        Guid loc2;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var l2 = new Location { Name = "Second", Timezone = "Europe/Kyiv" };
            ctx.Locations.Add(l2);
            ctx.SpecialistLocations.Add(new SpecialistLocation { SpecialistId = seed.SpecialistA, LocationId = seed.LocationId });
            await ctx.SaveChangesAsync();
            loc2 = l2.Id;
        }
        var admin = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Admin);
        var spec = await fx.InviteAndLoginAsync(t, owner.AccessToken, Roles.Specialist, seed.SpecialistA);
        var deputy = await fx.CreateDeputyAsync(t);
        return new Tn(t, seed, loc2, owner.AccessToken, admin.AccessToken, spec.AccessToken, deputy.Tokens.AccessToken);
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
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var el = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, el.GetProperty("code").GetString());
        return el;
    }

    private static async Task<Guid> NewProduct(HttpClient c, string token, string name, string unit = "ml", string purpose = "both")
    {
        var r = await Call(c, HttpMethod.Post, "/products", token, new { name, unit, purpose, salePrice = 10 });
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static object Line(Guid p, decimal q) => new { productId = p, quantity = q };
    private static object Body(Guid? loc, params object[] lines) => new { locationId = loc, lines };

    // ---------- норми ----------

    [SkippableFact]
    public async Task Norms_network_and_override_flow_with_no_store_and_updatedBy()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var water = await NewProduct(c, t.Admin, "Water", "l");
        var svc = t.Beauty.ServiceId;

        var put = await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Admin, Body(null, Line(dye, 30), Line(water, 0.5m)));
        Assert.Equal("no-store", put.Headers.CacheControl?.ToString());
        var set = await Ok(put);
        Assert.Equal(2, set.GetProperty("lines").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, set.GetProperty("locationId").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, set.GetProperty("updatedBy").ValueKind);

        // повторний PUT (повна заміна) - ідемпотентний, рядків лишається стільки, скільки в тілі
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Admin, Body(null, Line(dye, 10))));
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Admin, Body(null, Line(dye, 10))));
        // порожній override закладу дозволений
        var empty = await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Admin, Body(t.L1)));
        Assert.Equal(0, empty.GetProperty("lines").GetArrayLength());
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Admin, Body(t.Loc2, Line(water, 2))));

        var g = await Ok(await Call(c, HttpMethod.Get, $"/services/{svc}/materials", t.Owner));
        Assert.Equal(1, g.GetProperty("network").GetProperty("lines").GetArrayLength());
        Assert.Equal(10m, g.GetProperty("network").GetProperty("lines")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(2, g.GetProperty("locations").GetArrayLength());
        Assert.All(g.GetProperty("locations").EnumerateArray(), l => Assert.False(string.IsNullOrEmpty(l.GetProperty("locationName").GetString())));

        var list = await Ok(await Call(c, HttpMethod.Get, "/service-materials", t.Owner));
        Assert.Equal(svc, Assert.Single(list.GetProperty("items").EnumerateArray()).GetProperty("serviceId").GetGuid());

        // DELETE: override, мережевий, повтор -> 404
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/services/{svc}/materials?locationId={t.Loc2}", t.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/services/{svc}/materials", t.Admin)).StatusCode);
        await Fails(await Call(c, HttpMethod.Delete, $"/services/{svc}/materials", t.Admin), HttpStatusCode.NotFound, "materials_not_found");

        await using var ctx = fx.Db.CreateContext(t.Id);
        Assert.Equal(1, await ctx.ServiceMaterialSets.CountAsync()); // залишився лише порожній override L1
        Assert.Equal(0, await ctx.ServiceMaterials.CountAsync());
    }

    [SkippableFact]
    public async Task Norms_roles_permissions_and_specialist_scope()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var svc = t.Beauty.ServiceId;
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Owner, Body(null, Line(dye, 1))));
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Owner, Body(t.L1, Line(dye, 2))));
        await Ok(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Owner, Body(t.Loc2, Line(dye, 3))));

        // specialist (view=own, manage=none): зміна 403, а читання - мережевий набір і лише свій заклад
        await Fails(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Spec, Body(null, Line(dye, 1))), HttpStatusCode.Forbidden, "forbidden_role");
        await Fails(await Call(c, HttpMethod.Delete, $"/services/{svc}/materials", t.Spec), HttpStatusCode.Forbidden, "forbidden_role");
        var g = await Ok(await Call(c, HttpMethod.Get, $"/services/{svc}/materials", t.Spec));
        Assert.NotEqual(JsonValueKind.Null, g.GetProperty("network").ValueKind);
        Assert.Equal(t.L1, Assert.Single(g.GetProperty("locations").EnumerateArray()).GetProperty("locationId").GetGuid());
        Assert.Single((await Ok(await Call(c, HttpMethod.Get, "/service-materials", t.Spec))).GetProperty("items").EnumerateArray());

        // deputy: роль не owner/admin -> 403 незалежно від збереженого права
        await Fails(await Call(c, HttpMethod.Put, $"/services/{svc}/materials", t.Deputy, Body(null, Line(dye, 1))), HttpStatusCode.Forbidden, "forbidden_role");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Call(c, HttpMethod.Put, $"/services/{svc}/materials", null, Body(null, Line(dye, 1)))).StatusCode);
    }

    // L5: рольовий фільтр спрацьовує до прив'язки тіла - deputy отримує 403 forbidden_role навіть на некоректний JSON; owner/admin на кривий JSON - 422
    [SkippableFact]
    public async Task Deputy_gets_403_forbidden_role_on_malformed_json_before_body_binding()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var path = $"/api/beauty/inventory/services/{t.Beauty.ServiceId}/materials";
        async Task<HttpResponseMessage> Raw(HttpMethod m, string token, string? json)
        {
            var r = new HttpRequestMessage(m, path);
            if (json is not null) r.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await c.SendAsync(r);
        }
        await Fails(await Raw(HttpMethod.Put, t.Deputy, "{not json"), HttpStatusCode.Forbidden, "forbidden_role");
        await Fails(await Raw(HttpMethod.Put, t.Deputy, "{\"lines\":\"x\"}"), HttpStatusCode.Forbidden, "forbidden_role");
        await Fails(await Raw(HttpMethod.Delete, t.Deputy, null), HttpStatusCode.Forbidden, "forbidden_role");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Raw(HttpMethod.Put, t.Admin, "{not json")).StatusCode); // без фільтра ролі це був би 422 прив'язки
    }

    [SkippableFact]
    public async Task Norms_validation_and_not_found_over_http()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var retail = await NewProduct(c, t.Admin, "Retail", "pcs", "sale");
        var gloves = await NewProduct(c, t.Admin, "Gloves", "pcs");
        var svc = t.Beauty.ServiceId;
        string P(Guid s) => $"/services/{s}/materials";

        var e1 = await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(dye, 1), Line(retail, 1))), HttpStatusCode.UnprocessableEntity, "product_not_for_salon_use");
        Assert.Equal(1, e1.GetProperty("line").GetInt32());
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(gloves, 1.5m))), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(dye, 0))), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(dye, 1), Line(dye, 2))), HttpStatusCode.UnprocessableEntity, "duplicate_line");
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null)), HttpStatusCode.UnprocessableEntity, "invalid_lines");
        await Fails(await Call(c, HttpMethod.Put, P(Guid.NewGuid()), t.Admin, Body(null, Line(dye, 1))), HttpStatusCode.NotFound, "service_not_found");
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(Guid.NewGuid(), Line(dye, 1))), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(Guid.NewGuid(), 1))), HttpStatusCode.NotFound, "product_not_found");

        // товар вимкнено в закладі -> override цього закладу відхиляється, інший заклад і мережа - ні
        await Ok(await Call(c, HttpMethod.Put, $"/products/{dye}/locations/{t.L1}", t.Admin, new { isEnabled = false }));
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(t.L1, Line(dye, 1))), HttpStatusCode.UnprocessableEntity, "product_disabled_for_location");
        await Ok(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(t.Loc2, Line(dye, 1))));
        await Ok(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(dye, 1))));

        // неактивний товар
        await Ok(await Call(c, HttpMethod.Put, $"/products/{gloves}", t.Admin, new { name = "Gloves", unit = "pcs", purpose = "both", salePrice = 10, isActive = false }));
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, Line(gloves, 1))), HttpStatusCode.UnprocessableEntity, "product_not_for_salon_use");
        // межа 50 рядків
        await Fails(await Call(c, HttpMethod.Put, P(svc), t.Admin, Body(null, [.. Enumerable.Range(0, 51).Select(_ => Line(Guid.NewGuid(), 1))])), HttpStatusCode.UnprocessableEntity, "too_many_lines");
    }

    [SkippableFact]
    public async Task Product_in_norms_locks_unit_and_purpose()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var free = await NewProduct(c, t.Admin, "Free");
        await Ok(await Call(c, HttpMethod.Put, $"/services/{t.Beauty.ServiceId}/materials", t.Admin, Body(null, Line(dye, 1))));

        await Fails(await Call(c, HttpMethod.Put, $"/products/{dye}", t.Admin, new { name = "Dye", unit = "ml", purpose = "sale", salePrice = 10 }), HttpStatusCode.UnprocessableEntity, "purpose_locked");
        await Fails(await Call(c, HttpMethod.Put, $"/products/{dye}", t.Admin, new { name = "Dye", unit = "g", purpose = "both", salePrice = 10 }), HttpStatusCode.UnprocessableEntity, "unit_locked");
        // salon_use <-> both, назва, деактивація - дозволені
        await Ok(await Call(c, HttpMethod.Put, $"/products/{dye}", t.Admin, new { name = "Dye 2", unit = "ml", purpose = "salon_use", salePrice = 10 }));
        await Ok(await Call(c, HttpMethod.Put, $"/products/{dye}", t.Admin, new { name = "Dye 2", unit = "ml", purpose = "both", salePrice = 10, isActive = false }));
        // товар поза нормами - без обмежень
        await Ok(await Call(c, HttpMethod.Put, $"/products/{free}", t.Admin, new { name = "Free", unit = "g", purpose = "sale", salePrice = 10 }));
        // після видалення набору обмеження зникає
        Assert.Equal(HttpStatusCode.NoContent, (await Call(c, HttpMethod.Delete, $"/services/{t.Beauty.ServiceId}/materials", t.Admin)).StatusCode);
        await Ok(await Call(c, HttpMethod.Put, $"/products/{dye}", t.Admin, new { name = "Dye 2", unit = "g", purpose = "sale", salePrice = 10 }));
    }

    [SkippableFact]
    public async Task Norms_and_alerts_are_isolated_between_tenants_and_gated_by_module()
    {
        NeedDb();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var off = await NewTenantAsync(withInventory: false);
        var c = NewClient();
        var dye = await NewProduct(c, a.Admin, "Dye");
        await Ok(await Call(c, HttpMethod.Put, $"/services/{a.Beauty.ServiceId}/materials", a.Admin, Body(null, Line(dye, 1))));
        var alertId = await SeedAlertAsync(a, a.L1, dye, "low_stock", "k1", T0);

        await Fails(await Call(c, HttpMethod.Get, $"/services/{a.Beauty.ServiceId}/materials", b.Admin), HttpStatusCode.NotFound, "service_not_found");
        await Fails(await Call(c, HttpMethod.Put, $"/services/{a.Beauty.ServiceId}/materials", b.Admin, Body(null, Line(dye, 1))), HttpStatusCode.NotFound, "service_not_found");
        Assert.Empty((await Ok(await Call(c, HttpMethod.Get, "/service-materials", b.Admin))).GetProperty("items").EnumerateArray());
        Assert.Empty((await Ok(await Call(c, HttpMethod.Get, "/alerts?status=all", b.Admin))).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await Ok(await Call(c, HttpMethod.Get, "/alerts/unread-count", b.Admin))).GetProperty("count").GetInt32());
        await Fails(await Call(c, HttpMethod.Post, $"/alerts/{alertId}/read", b.Admin), HttpStatusCode.NotFound, "alert_not_found");
        Assert.Equal(0, (await Ok(await Call(c, HttpMethod.Post, "/alerts/read-all", b.Admin))).GetProperty("updated").GetInt32());
        Assert.Equal("open", await AlertStatusAsync(a.Id, alertId));

        foreach (var (m, p) in new[] { (HttpMethod.Get, "/service-materials"), (HttpMethod.Get, $"/services/{off.Beauty.ServiceId}/materials"),
                     (HttpMethod.Get, "/alerts"), (HttpMethod.Get, "/alerts/unread-count"), (HttpMethod.Post, "/alerts/read-all") })
            await Fails(await Call(c, m, p, off.Admin), HttpStatusCode.Forbidden, "module_disabled");
    }

    // ---------- сповіщення ----------

    private async Task<Guid> SeedAlertAsync(Tn t, Guid loc, Guid product, string kind, string key, DateTimeOffset createdAt, string status = "open")
    {
        await using var ctx = fx.Db.CreateContext(t.Id);
        var a = new StaffAlert
        {
            Kind = kind, LocationId = loc, ProductId = product, Quantity = 1m, DedupeKey = key, CreatedAt = createdAt, Status = status,
            ResolvedAt = status == "resolved" ? createdAt : null, ReadAt = status == "read" ? createdAt : null, Threshold = kind == "low_stock" ? 5m : null,
        };
        ctx.StaffAlerts.Add(a);
        await ctx.SaveChangesAsync();
        return a.Id;
    }

    private async Task<string> AlertStatusAsync(Guid tenant, Guid id)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        return await ctx.StaffAlerts.AsNoTracking().Where(a => a.Id == id).Select(a => a.Status).SingleAsync();
    }

    [SkippableFact]
    public async Task Alerts_list_scope_status_read_count_and_paging()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var a1 = await SeedAlertAsync(t, t.L1, dye, "low_stock", "k1", T0);
        var a2 = await SeedAlertAsync(t, t.L1, dye, "low_stock", "k2", T0.AddMinutes(1), "read");
        var a3 = await SeedAlertAsync(t, t.Loc2, dye, "low_stock", "k3", T0.AddMinutes(2));
        var a4 = await SeedAlertAsync(t, t.L1, dye, "low_stock", "k4", T0.AddMinutes(3), "resolved");

        // admin (all): active = open|read, новіші першими
        var all = await Ok(await Call(c, HttpMethod.Get, "/alerts", t.Admin));
        Assert.Equal([a3, a2, a1], all.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        var first = all.GetProperty("items")[0];
        Assert.Equal("Second", first.GetProperty("location").GetProperty("name").GetString());
        Assert.Equal("Dye", first.GetProperty("product").GetProperty("name").GetString());
        Assert.False(first.TryGetProperty("appointmentId", out _) || first.TryGetProperty("clientId", out _));
        Assert.Equal(4, (await Ok(await Call(c, HttpMethod.Get, "/alerts?status=all", t.Admin))).GetProperty("items").GetArrayLength());
        Assert.Single((await Ok(await Call(c, HttpMethod.Get, "/alerts?status=resolved", t.Admin))).GetProperty("items").EnumerateArray());
        await Fails(await Call(c, HttpMethod.Get, "/alerts?status=zzz", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_alert_status");
        await Fails(await Call(c, HttpMethod.Get, "/alerts?kind=zzz", t.Admin), HttpStatusCode.UnprocessableEntity, "invalid_alert_kind");

        // пагінація
        var p1 = await Ok(await Call(c, HttpMethod.Get, "/alerts?take=2", t.Admin));
        Assert.True(p1.GetProperty("hasMore").GetBoolean());
        var p2 = await Ok(await Call(c, HttpMethod.Get, $"/alerts?take=2&cursor={Uri.EscapeDataString(p1.GetProperty("nextCursor").GetString()!)}", t.Admin));
        Assert.False(p2.GetProperty("hasMore").GetBoolean());
        Assert.Equal(a1, Assert.Single(p2.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        // specialist (own = L1): лише L1, чужий заклад і чуже сповіщення -> 404
        var spec = await Ok(await Call(c, HttpMethod.Get, "/alerts?status=all", t.Spec));
        Assert.DoesNotContain(a3, spec.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
        Assert.Equal(3, spec.GetProperty("items").GetArrayLength());
        await Fails(await Call(c, HttpMethod.Get, $"/alerts?locationId={t.Loc2}", t.Spec), HttpStatusCode.NotFound, "location_not_found");
        await Fails(await Call(c, HttpMethod.Post, $"/alerts/{a3}/read", t.Spec), HttpStatusCode.NotFound, "alert_not_found");
        Assert.Equal(1, (await Ok(await Call(c, HttpMethod.Get, "/alerts/unread-count", t.Spec))).GetProperty("count").GetInt32()); // a1
        Assert.Equal(2, (await Ok(await Call(c, HttpMethod.Get, "/alerts/unread-count", t.Admin))).GetProperty("count").GetInt32()); // a1, a3

        // mark read: open -> read, спільно для всіх; повтор і resolved - no-op 200
        var read = await Ok(await Call(c, HttpMethod.Post, $"/alerts/{a1}/read", t.Spec));
        Assert.Equal("read", read.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("readBy").ValueKind);
        Assert.Equal(1, (await Ok(await Call(c, HttpMethod.Get, "/alerts/unread-count", t.Admin))).GetProperty("count").GetInt32());
        Assert.Equal("read", (await Ok(await Call(c, HttpMethod.Post, $"/alerts/{a1}/read", t.Admin))).GetProperty("status").GetString());
        Assert.Equal("resolved", (await Ok(await Call(c, HttpMethod.Post, $"/alerts/{a4}/read", t.Admin))).GetProperty("status").GetString());

        // read-all: specialist - лише свій scope (a3 лишається open); admin - решта
        Assert.Equal(0, (await Ok(await Call(c, HttpMethod.Post, "/alerts/read-all", t.Spec))).GetProperty("updated").GetInt32());
        await Fails(await Call(c, HttpMethod.Post, $"/alerts/read-all?locationId={t.Loc2}", t.Spec), HttpStatusCode.NotFound, "location_not_found");
        Assert.Equal("open", await AlertStatusAsync(t.Id, a3));
        Assert.Equal(1, (await Ok(await Call(c, HttpMethod.Post, $"/alerts/read-all?locationId={t.Loc2}", t.Admin))).GetProperty("updated").GetInt32());
        Assert.Equal("resolved", await AlertStatusAsync(t.Id, a4));
        Assert.Equal(0, (await Ok(await Call(c, HttpMethod.Get, "/alerts/unread-count", t.Admin))).GetProperty("count").GetInt32());
    }

    // ---------- writer і гонки (DB) ----------

    private sealed class NoSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    [SkippableFact]
    public async Task Writer_shortage_alert_is_idempotent_and_free_of_personal_data()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        Guid op;
        await using (var seed = fx.Db.CreateContext(t.Id))
        {
            var o = new StockOperation { Kind = StockOperationKind.ServiceConsumption, LocationId = t.L1, AppointmentId = t.Beauty.AppointmentA, CreatedAt = T0 };
            seed.StockOperations.Add(o);
            await seed.SaveChangesAsync();
            op = o.Id;
        }

        await using var ctx = fx.Db.CreateContext(t.Id);
        var writer = (IStaffAlertWriter)new EfBeautyStore(ctx, new NoSecrets());
        Assert.True(await writer.CreateShortageAlertAsync(t.L1, dye, op, 2.5m, T0, default));
        Assert.False(await writer.CreateShortageAlertAsync(t.L1, dye, op, 9m, T0, default));
        // у відкритій транзакції виклику повтор теж не ламає транзакцію
        await using (var tx = await ctx.Database.BeginTransactionAsync())
        {
            Assert.False(await writer.CreateShortageAlertAsync(t.L1, dye, op, 1m, T0, default));
            await tx.CommitAsync();
        }

        var rows = await ctx.StaffAlerts.AsNoTracking().ToListAsync();
        var a = Assert.Single(rows);
        Assert.Equal("shortage", a.Kind);
        Assert.Equal(2.5m, a.Quantity);
        Assert.Equal(op, a.OperationId);
        Assert.Equal("open", a.Status);
        Assert.Equal($"shortage:{op}:{dye}", a.DedupeKey);
        Assert.Null(a.BatchId);

        var json = await Ok(await Call(c, HttpMethod.Get, "/alerts", t.Admin));
        var dto = Assert.Single(json.GetProperty("items").EnumerateArray());
        Assert.Equal(op, dto.GetProperty("operationId").GetGuid());
        Assert.DoesNotContain(t.Beauty.AppointmentA.ToString(), dto.GetRawText());
        Assert.DoesNotContain(t.Beauty.ClientId.ToString(), dto.GetRawText());
    }

    [SkippableFact]
    public async Task Concurrent_first_puts_of_the_same_set_both_succeed_and_leave_one_set()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var dye = await NewProduct(c, t.Admin, "Dye");
        var water = await NewProduct(c, t.Admin, "Water", "l");
        var userId = t.Seed.OwnerId;

        async Task<MaterialPutResult> PutOne(Guid product, decimal q)
        {
            await using var ctx = fx.Db.CreateContext(t.Id);
            return await new EfBeautyStore(ctx, new NoSecrets()).PutMaterialSetAsync(t.Beauty.ServiceId, null, [new MaterialLineRow(product, q)], userId, T0, default);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => PutOne(i % 2 == 0 ? dye : water, i + 1)));
        Assert.All(results, r => Assert.Equal(MaterialPutStatus.Ok, r.Status));
        await using var check = fx.Db.CreateContext(t.Id);
        var set = await check.ServiceMaterialSets.AsNoTracking().SingleAsync();
        Assert.Single(await check.ServiceMaterials.AsNoTracking().Where(m => m.SetId == set.Id).ToListAsync()); // повна заміна, без суміші рядків
    }
}
