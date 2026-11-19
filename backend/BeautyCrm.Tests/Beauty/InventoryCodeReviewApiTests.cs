using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Tests.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static BeautyCrm.Tests.Auth.AuthApiFixture;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-880 (код-рев'ю складу) на реальній PostgreSQL через HTTP: M1 (явна партія не вичерпується FEFO-рядком), M2 (час запису - після
/// блокувань), L3 (стабільний порядок рухів), L8 (surplusExpiresOn зберігається), L9 (цілочисельність при проведенні), L13 (сортування
/// партій за FEFO, далі location_id). Час - FrozenTime / TickingTime, не реальний годинник.
/// </summary>
public sealed class InventoryCodeReviewApiTests(AuthApiFixture fx) : IClassFixture<AuthApiFixture>, IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private static readonly long NoonUnix = DateTimeOffset.Parse("2026-11-18T12:00:00Z").ToUnixTimeSeconds();

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private void NeedDb() => Skip.If(fx.SkipReason is not null, fx.SkipReason);

    private HttpClient NewClient(TimeProvider clock)
    {
        var f = fx.CreateFactory(10_000).WithWebHostBuilder(b =>
        {
            b.UseSetting("Inventory:WritePerUserPerMinute", "10000");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton(clock);
            });
        });
        _disposables.Add(f);
        return f.CreateClient();
    }

    /// <summary>Годинник, що збільшується на секунду при кожному читанні (детермінований, без реального часу).</summary>
    private sealed class TickingTime(long unix) : TimeProvider
    {
        private long _n;
        public long Calls => Interlocked.Read(ref _n);
        public DateTimeOffset Last { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            var v = DateTimeOffset.FromUnixTimeSeconds(unix + Interlocked.Increment(ref _n));
            Last = v;
            return v;
        }
    }

    private sealed record Tn(TenantSeed Seed, BeautySeed Beauty, Guid Loc2, string Owner)
    {
        public Guid Id => Seed.TenantId;
        public Guid L1 => Beauty.LocationId;
    }

    private async Task<Tn> NewTenantAsync()
    {
        var t = await fx.CreateTenantAsync([.. BeautyModules.Known]);
        var owner = await fx.LoginAsync(t.Slug, t.OwnerEmail);
        var seed = await fx.SeedBeautyAsync(t.TenantId);
        Guid loc2;
        await using (var ctx = fx.Db.CreateContext(t.TenantId))
        {
            var l2 = new Location { Name = "Second", Timezone = "Europe/Kyiv" };
            ctx.Locations.Add(l2);
            await ctx.Locations.Where(l => l.Id == seed.LocationId).ExecuteUpdateAsync(s => s.SetProperty(l => l.Timezone, "Europe/Kyiv"));
            await ctx.SaveChangesAsync();
            loc2 = l2.Id;
        }
        return new Tn(t, seed, loc2, owner.AccessToken);
    }

    private static Task<HttpResponseMessage> Call(HttpClient c, HttpMethod m, string path, string token, object? body = null, string? key = null)
    {
        var r = new HttpRequestMessage(m, "/api/beauty/inventory" + path);
        if (body is not null) r.Content = JsonContent.Create(body, options: Json);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) r.Headers.Add("Idempotency-Key", key);
        return c.SendAsync(r);
    }

    private static Task<HttpResponseMessage> Get(HttpClient c, string path, string token) => Call(c, HttpMethod.Get, path, token);
    private static Task<HttpResponseMessage> Op(HttpClient c, string path, string token, object body) =>
        Call(c, HttpMethod.Post, path, token, body, Guid.NewGuid().ToString());

    private static async Task<JsonElement> Ok(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"expected {expected}, got {(int)r.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> Fails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == status, $"expected {status}, got {(int)r.StatusCode}: {body}");
        var root = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal(code, root.GetProperty("code").GetString());
        return root;
    }

    private async Task<decimal> LevelAsync(Guid tenant, Guid loc, Guid product)
    {
        await using var ctx = fx.Db.CreateContext(tenant);
        return await ctx.StockLevels.AsNoTracking().Where(l => l.LocationId == loc && l.ProductId == product).Select(l => l.Quantity).FirstOrDefaultAsync();
    }

    private static async Task<Guid> NewProduct(HttpClient c, string token, string name, string unit = "pcs")
    {
        var r = await Call(c, HttpMethod.Post, "/products", token, new { name, unit, purpose = "both", trackExpiry = false });
        return (await Ok(r, HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> Receive(HttpClient c, string token, Guid loc, object[] lines) =>
        await Ok(await Op(c, "/receipts", token, new { locationId = loc, lines }), HttpStatusCode.Created);

    [SkippableFact]
    public async Task M1_writeoff_fefo_line_does_not_starve_explicit_batch_line()
    {
        NeedDb();
        var c = NewClient(new FrozenTime(NoonUnix));
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "M1");
        await Receive(c, t.Owner, t.L1, [new { productId = p, quantity = 5, expiresOn = "2027-06-01" }]);   // B1
        await Receive(c, t.Owner, t.L1, [new { productId = p, quantity = 5, expiresOn = "2026-12-01" }]);   // B2 (FEFO-перша)
        var list = (await Ok(await Get(c, $"/batches?locationId={t.L1}&productId={p}", t.Owner))).GetProperty("items");
        var b2 = list.EnumerateArray().Single(x => x.GetProperty("expiresOn").GetString() == "2026-12-01").GetProperty("id").GetGuid();

        var r = await Ok(await Op(c, "/write-offs", t.Owner, new
        {
            locationId = t.L1, reason = "lost",
            lines = new object[] { new { productId = p, quantity = 5 }, new { productId = p, quantity = 5, batchId = b2 } },
        }), HttpStatusCode.Created);
        Assert.Equal(2, r.GetProperty("movements").GetArrayLength());
        Assert.Equal(0m, await LevelAsync(t.Id, t.L1, p));
    }

    [SkippableFact]
    public async Task M2_L3_movements_use_clock_after_locks_and_keep_request_line_order()
    {
        NeedDb();
        var clock = new TickingTime(NoonUnix);
        var c = NewClient(clock);
        var t = await NewTenantAsync();
        var products = new List<Guid>();
        for (var i = 0; i < 6; i++) products.Add(await NewProduct(c, t.Owner, $"Ord{i}"));
        // порядок у запиті навмисно не збігається з порядком id товарів
        var order = products.OrderByDescending(x => x).ToList();

        var callsBefore = clock.Calls;
        var r = await Receive(c, t.Owner, t.L1, [.. order.Select(p => (object)new { productId = p, quantity = 1 })]);
        Assert.True(clock.Calls - callsBefore >= 2); // сервіс і сховище читають годинник окремо
        var moves = r.GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(order, moves.Select(m => m.GetProperty("product").GetProperty("id").GetGuid()).ToList());
        // M2: час запису - останнє читання годинника (у сховищі, після блокувань), а не час, взятий сервісом до блокувань
        var occurred = moves.Select(m => m.GetProperty("occurredAt").GetDateTimeOffset()).Distinct().Single();
        Assert.Equal(clock.Last, occurred);
    }

    [SkippableFact]
    public async Task L8_counted_only_line_keeps_previous_surplus_expiry()
    {
        NeedDb();
        var c = NewClient(new FrozenTime(NoonUnix));
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "L8");
        var st = await Ok(await Call(c, HttpMethod.Post, "/stocktakes", t.Owner, new { locationId = t.L1, productIds = new[] { p } }), HttpStatusCode.Created);
        var id = st.GetProperty("id").GetGuid();
        var v = st.GetProperty("version").GetInt32();
        var put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Owner,
            new { expectedVersion = v, lines = new[] { new { productId = p, countedQuantity = 3, surplusExpiresOn = "2027-01-01" } } }));
        v = put.GetProperty("version").GetInt32();
        put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Owner,
            new { expectedVersion = v, lines = new object[] { new { productId = p, countedQuantity = 4 } } }));
        var line = put.GetProperty("lines")[0];
        Assert.Equal(4m, line.GetProperty("countedQuantity").GetDecimal());
        Assert.Equal("2027-01-01", line.GetProperty("surplusExpiresOn").GetString());
    }

    [SkippableFact]
    public async Task L9_post_rejects_fractional_count_when_unit_changed_to_pcs_after_put()
    {
        NeedDb();
        var c = NewClient(new FrozenTime(NoonUnix));
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "L9", "kg");
        var st = await Ok(await Call(c, HttpMethod.Post, "/stocktakes", t.Owner, new { locationId = t.L1, productIds = new[] { p } }), HttpStatusCode.Created);
        var id = st.GetProperty("id").GetGuid();
        var v = st.GetProperty("version").GetInt32();
        var put = await Ok(await Call(c, HttpMethod.Put, $"/stocktakes/{id}/lines", t.Owner,
            new { expectedVersion = v, lines = new[] { new { productId = p, countedQuantity = 1.5m } } }));
        v = put.GetProperty("version").GetInt32();
        await Ok(await Call(c, HttpMethod.Put, $"/products/{p}", t.Owner, new { name = "L9", unit = "pcs", purpose = "both" })); // рухів немає - зміна дозволена
        var bad = await Fails(await Call(c, HttpMethod.Post, $"/stocktakes/{id}/post", t.Owner, new { expectedVersion = v }), HttpStatusCode.UnprocessableEntity, "invalid_quantity");
        Assert.Equal(p, bad.GetProperty("productId").GetGuid());
        Assert.Equal(0m, await LevelAsync(t.Id, t.L1, p));
    }

    [SkippableFact]
    public async Task L13_batches_sort_by_fefo_then_location_and_cursor_pages_match_full_list()
    {
        NeedDb();
        var c = NewClient(new FrozenTime(NoonUnix)); // однаковий received_at у всіх партій
        var t = await NewTenantAsync();
        var p = await NewProduct(c, t.Owner, "L13");
        foreach (var loc in new[] { t.L1, t.Loc2, t.L1, t.Loc2 })
            await Receive(c, t.Owner, loc, [new { productId = p, quantity = 1, expiresOn = "2027-01-01" }]);

        var full = (await Ok(await Get(c, $"/batches?productId={p}&take=100", t.Owner))).GetProperty("items").EnumerateArray()
            .Select(x => (Loc: x.GetProperty("locationId").GetGuid(), Id: x.GetProperty("id").GetGuid())).ToList();
        Assert.Equal(4, full.Count);
        var expected = full.OrderBy(x => x.Loc.ToString("D"), StringComparer.Ordinal).ThenBy(x => x.Id.ToString("D"), StringComparer.Ordinal).ToList();
        Assert.Equal(expected, full);

        var paged = new List<(Guid, Guid)>();
        string? cursor = null;
        do
        {
            var page = await Ok(await Get(c, $"/batches?productId={p}&take=1" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), t.Owner));
            paged.AddRange(page.GetProperty("items").EnumerateArray().Select(x => (x.GetProperty("locationId").GetGuid(), x.GetProperty("id").GetGuid())));
            cursor = page.TryGetProperty("nextCursor", out var nc) && nc.ValueKind == JsonValueKind.String ? nc.GetString() : null;
        } while (cursor is not null);
        Assert.Equal(full, paged);
    }
}
