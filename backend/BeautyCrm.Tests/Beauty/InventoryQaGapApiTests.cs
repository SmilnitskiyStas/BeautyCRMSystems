using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-892 (QA, етап 2a складу) - наскрізні HTTP-сценарії на реальній БД через справжній PATCH /appointments/{id} completed:
/// подвійне завершення, модуль вимкнено/ввімкнено, нестача + погашення надходженням, ADR-014 cost_snapshot, IDOR норм і сповіщень
/// (locationId / productId чужого tenant, scope спеціаліста).
/// </summary>
public sealed class InventoryQaGapApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    public void Dispose() { foreach (var d in _disposables) d.Dispose(); }
    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient NewClient()
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b => b.UseSetting("Inventory:WritePerUserPerMinute", "600"));
        _disposables.Add(f);
        return f.CreateClient();
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, Guid Loc2, string Owner, string Admin, string Spec)
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
        return new Tn(t, seed, loc2, owner.AccessToken, admin.AccessToken, spec.AccessToken);
    }

    private static Task<HttpResponseMessage> Send(HttpClient c, HttpMethod m, string path, string token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, "/api/beauty" + path);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        if (key is not null) r.Headers.Add("Idempotency-Key", key);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c.SendAsync(r);
    }

    private static Task<HttpResponseMessage> Inv(HttpClient c, HttpMethod m, string path, string token, object? body = null) =>
        Send(c, m, "/inventory" + path, token, body, m == HttpMethod.Post ? Guid.NewGuid().ToString() : null);

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<T> AdminScalar<T>(string sql)
    {
        await using var admin = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private async Task Admin(string sql)
    {
        await using var admin = new NpgsqlConnection(fx.Db.AdminConnectionString);
        await admin.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, admin);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Guid> ProductDirect(Tn t, string name = "Dye", ProductUnit unit = ProductUnit.Ml)
    {
        await using var ctx = fx.Db.CreateContext(t.Id);
        var p = new Product { Name = name + Guid.NewGuid().ToString("N")[..6], Unit = unit, Purpose = ProductPurpose.SalonUse, IsActive = true, UpdatedByUserId = t.Seed.OwnerId };
        ctx.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private async Task NormsDirect(Tn t, Guid product, decimal qty)
    {
        await using var ctx = fx.Db.CreateContext(t.Id);
        var set = new ServiceMaterialSet { ServiceId = t.Beauty.ServiceId, LocationId = null, UpdatedByUserId = t.Seed.OwnerId };
        ctx.Add(set);
        await ctx.SaveChangesAsync();
        ctx.Add(new ServiceMaterial { SetId = set.Id, ProductId = product, Quantity = qty });
        await ctx.SaveChangesAsync();
    }

    private Task<int> OpsFor(Guid tenant, Guid appt) =>
        AdminScalar<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{tenant}' AND appointment_id = '{appt}'");

    private static JsonElement StockRow(JsonElement stock, Guid product) =>
        stock.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("product").GetProperty("id").GetGuid() == product);

    // ---------- подвійне завершення через HTTP ----------

    [SkippableFact]
    public async Task Double_completion_over_http_consumes_once_and_second_patch_is_409()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var p = await ProductDirect(t);
        await NormsDirect(t, p, 2m);
        await Ok(await Inv(c, HttpMethod.Post, "/receipts", t.Admin, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 10m } } }), HttpStatusCode.Created);

        var first = await Send(c, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.DoesNotContain("shortage", body, StringComparison.OrdinalIgnoreCase); // відповідь PATCH не змінюється (§32.18)
        var second = await Send(c, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(1, await OpsFor(t.Id, t.Beauty.AppointmentA));
        var stock = await Ok(await Inv(c, HttpMethod.Get, $"/stock?locationId={t.L1}", t.Admin));
        Assert.Equal(8m, StockRow(stock, p).GetProperty("quantity").GetDecimal());
        var ops = await Ok(await Inv(c, HttpMethod.Get, "/operations?kind=service_consumption", t.Admin));
        var op = Assert.Single(ops.GetProperty("items").EnumerateArray());
        Assert.Equal(t.Beauty.AppointmentA, op.GetProperty("appointmentId").GetGuid());
        var mv = await Ok(await Inv(c, HttpMethod.Get, "/movements?type=service_consumption", t.Admin));
        var m = Assert.Single(mv.GetProperty("items").EnumerateArray());
        Assert.Equal(-2m, m.GetProperty("quantity").GetDecimal());
        Assert.False(m.GetProperty("isShortage").GetBoolean());
    }

    // ---------- модуль вимкнено / ввімкнено ----------

    [SkippableFact]
    public async Task Completion_with_module_off_writes_nothing_and_enabling_later_does_not_backfill()
    {
        NeedDb();
        var t = await NewTenantAsync(withInventory: false);
        var c = NewClient();
        var p = await ProductDirect(t);
        await NormsDirect(t, p, 3m);

        var off = await Send(c, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(0, await OpsFor(t.Id, t.Beauty.AppointmentA));
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_levels WHERE tenant_id = '{t.Id}'"));
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_staff_alerts WHERE tenant_id = '{t.Id}'"));

        var all = string.Join(",", BeautyModules.Known);
        await Admin($"UPDATE tenants SET modules = '{{{all}}}' WHERE id = '{t.Id}'");
        var on = await Send(c, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentB}", t.Owner, new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(1, await OpsFor(t.Id, t.Beauty.AppointmentB)); // новий візит списує (нестача: запасу немає)
        Assert.Equal(0, await OpsFor(t.Id, t.Beauty.AppointmentA)); // минулий не списується заднім числом
    }

    // ---------- нестача + погашення через HTTP, ADR-014 ----------

    [SkippableFact]
    public async Task Shortage_then_receipt_repays_over_http_and_cost_snapshot_is_untouched()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var p = await ProductDirect(t);
        await NormsDirect(t, p, 30m);
        await Admin($"INSERT INTO beauty_service_costs (id, tenant_id, service_id, location_id, cost_amount, master_percent, updated_by_user_id) VALUES (gen_random_uuid(), '{t.Id}', '{t.Beauty.ServiceId}', NULL, 9.90, 30, '{t.Seed.OwnerId}')");

        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Patch, $"/appointments/{t.Beauty.AppointmentA}", t.Owner, new { status = "completed" })).StatusCode);
        Assert.Equal(9.90m, await AdminScalar<decimal>($"SELECT cost_snapshot FROM beauty_appointments WHERE id = '{t.Beauty.AppointmentA}'"));

        var row = StockRow(await Ok(await Inv(c, HttpMethod.Get, $"/stock?locationId={t.L1}", t.Admin)), p);
        Assert.Equal(0m, row.GetProperty("quantity").GetDecimal());
        Assert.Equal(30m, row.GetProperty("shortageQuantity").GetDecimal());
        Assert.Equal(-30m, row.GetProperty("balance").GetDecimal());

        var alertsRaw = await (await Inv(c, HttpMethod.Get, "/alerts?kind=shortage", t.Admin)).Content.ReadAsStringAsync();
        var alert = Assert.Single(JsonDocument.Parse(alertsRaw).RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(30m, alert.GetProperty("quantity").GetDecimal());
        foreach (var forbidden in new[] { t.Beauty.AppointmentA.ToString(), t.Beauty.ClientId.ToString(), "Client", "+380000000000", "Master A" })
            Assert.DoesNotContain(forbidden, alertsRaw);

        // надходження 1000 при дефіциті 30: сальдо -30 -> 970
        Assert.Equal(HttpStatusCode.Created, (await Inv(c, HttpMethod.Post, "/receipts", t.Admin, new { locationId = t.L1, lines = new[] { new { productId = p, quantity = 1000m, unitCost = 1m } } })).StatusCode);
        row = StockRow(await Ok(await Inv(c, HttpMethod.Get, $"/stock?locationId={t.L1}", t.Admin)), p);
        Assert.Equal(970m, row.GetProperty("quantity").GetDecimal());
        Assert.Equal(0m, row.GetProperty("shortageQuantity").GetDecimal());
        Assert.Equal(970m, row.GetProperty("balance").GetDecimal());
        var movs = (await Ok(await Inv(c, HttpMethod.Get, $"/movements?locationId={t.L1}&take=50", t.Admin))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(movs, m => m.GetProperty("type").GetString() == "receipt" && m.GetProperty("isShortage").GetBoolean() && m.GetProperty("quantity").GetDecimal() == 30m);
        Assert.Contains(movs, m => m.GetProperty("type").GetString() == "receipt" && !m.GetProperty("isShortage").GetBoolean() && m.GetProperty("quantity").GetDecimal() == 970m);
        Assert.Equal(9.90m, await AdminScalar<decimal>($"SELECT cost_snapshot FROM beauty_appointments WHERE id = '{t.Beauty.AppointmentA}'"));
    }

    // ---------- IDOR: норми ----------

    [SkippableFact]
    public async Task Foreign_tenant_ids_in_norms_and_alert_endpoints_are_404_and_change_nothing()
    {
        NeedDb();
        var a = await NewTenantAsync();
        var b = await NewTenantAsync();
        var c = NewClient();
        var pa = await ProductDirect(a);
        var pb = await ProductDirect(b);

        // b-admin: свій service, але чужий заклад / чужий товар
        var foreignLoc = await Inv(c, HttpMethod.Put, $"/services/{b.Beauty.ServiceId}/materials", b.Admin, new { locationId = a.L1, lines = new[] { new { productId = pb, quantity = 1m } } });
        Assert.Equal(HttpStatusCode.NotFound, foreignLoc.StatusCode);
        var foreignProd = await Inv(c, HttpMethod.Put, $"/services/{b.Beauty.ServiceId}/materials", b.Admin, new { locationId = (Guid?)null, lines = new[] { new { productId = pa, quantity = 1m } } });
        Assert.Equal(HttpStatusCode.NotFound, foreignProd.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Inv(c, HttpMethod.Delete, $"/services/{b.Beauty.ServiceId}/materials?locationId={a.L1}", b.Admin)).StatusCode);
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_service_material_sets WHERE tenant_id = '{b.Id}'"));
        // чужий serviceId: GET/PUT/DELETE
        Assert.Equal(HttpStatusCode.NotFound, (await Inv(c, HttpMethod.Get, $"/services/{a.Beauty.ServiceId}/materials", b.Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Inv(c, HttpMethod.Delete, $"/services/{a.Beauty.ServiceId}/materials", b.Admin)).StatusCode);

        // сповіщення: чужий locationId у фільтрі й read-all
        await Admin($"INSERT INTO beauty_staff_alerts (id, tenant_id, kind, location_id, product_id, quantity, threshold, dedupe_key, status) VALUES (gen_random_uuid(), '{a.Id}', 'low_stock', '{a.L1}', '{pa}', 1, 5, 'qa-k1', 'open')");
        var list = await Inv(c, HttpMethod.Get, $"/alerts?status=all&locationId={a.L1}", b.Admin);
        if (list.StatusCode == HttpStatusCode.OK) Assert.Empty((await Ok(list)).GetProperty("items").EnumerateArray());
        else Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        var ra = await Inv(c, HttpMethod.Post, $"/alerts/read-all?locationId={a.L1}", b.Admin);
        if (ra.StatusCode == HttpStatusCode.OK) Assert.Equal(0, (await Ok(ra)).GetProperty("updated").GetInt32());
        else Assert.Equal(HttpStatusCode.NotFound, ra.StatusCode);
        Assert.Equal("open", await AdminScalar<string>($"SELECT status FROM beauty_staff_alerts WHERE tenant_id = '{a.Id}' AND dedupe_key = 'qa-k1'"));
    }

    [SkippableFact]
    public async Task Specialist_scope_hides_other_location_alerts_and_cannot_mark_them_read()
    {
        NeedDb();
        var t = await NewTenantAsync();
        var c = NewClient();
        var p = await ProductDirect(t);
        await Admin($"INSERT INTO beauty_staff_alerts (id, tenant_id, kind, location_id, product_id, quantity, threshold, dedupe_key, status) VALUES ('{Guid.NewGuid()}', '{t.Id}', 'low_stock', '{t.Loc2}', '{p}', 1, 5, 'qa-l2', 'open')");
        var foreignId = await AdminScalar<Guid>($"SELECT id FROM beauty_staff_alerts WHERE tenant_id = '{t.Id}' AND dedupe_key = 'qa-l2'");

        Assert.Empty((await Ok(await Inv(c, HttpMethod.Get, "/alerts?status=all", t.Spec))).GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await Ok(await Inv(c, HttpMethod.Get, "/alerts/unread-count", t.Spec))).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await Inv(c, HttpMethod.Post, $"/alerts/{foreignId}/read", t.Spec)).StatusCode);
        var ra = await Inv(c, HttpMethod.Post, $"/alerts/read-all?locationId={t.Loc2}", t.Spec);
        if (ra.StatusCode == HttpStatusCode.OK) Assert.Equal(0, (await Ok(ra)).GetProperty("updated").GetInt32());
        else Assert.Equal(HttpStatusCode.NotFound, ra.StatusCode);
        Assert.Equal(0, (await Ok(await Inv(c, HttpMethod.Post, "/alerts/read-all", t.Spec))).GetProperty("updated").GetInt32());
        Assert.Equal("open", await AdminScalar<string>($"SELECT status FROM beauty_staff_alerts WHERE id = '{foreignId}'"));
        // власник бачить
        Assert.Single((await Ok(await Inv(c, HttpMethod.Get, "/alerts?status=all", t.Owner))).GetProperty("items").EnumerateArray());
    }

    // ---------- ізоляція tenant у автосписанні ----------

    [SkippableFact]
    public async Task Autoconsumption_in_one_tenant_never_touches_another_and_module_gate_is_per_tenant()
    {
        NeedDb();
        var on = await NewTenantAsync();
        var off = await NewTenantAsync(withInventory: false);
        var c = NewClient();
        var pOn = await ProductDirect(on);
        var pOff = await ProductDirect(off);
        await NormsDirect(on, pOn, 2m);
        await NormsDirect(off, pOff, 2m);

        // завершення в tenant з вимкненим модулем не повинно «підхопити» модуль сусіда
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Patch, $"/appointments/{off.Beauty.AppointmentA}", off.Owner, new { status = "completed" })).StatusCode);
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{off.Id}'"));
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Patch, $"/appointments/{on.Beauty.AppointmentA}", on.Owner, new { status = "completed" })).StatusCode);
        Assert.Equal(1, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_operations WHERE tenant_id = '{on.Id}'"));
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_movements WHERE tenant_id = '{off.Id}'"));
        Assert.Equal(0, await AdminScalar<int>($"SELECT count(*) FROM beauty_stock_levels WHERE tenant_id = '{off.Id}'"));
    }
}
