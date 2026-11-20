using System.Text;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>
/// TASK-880 (security-review складу): сервісні тести виправлень на <see cref="FakeInventoryStore"/>. L1 - підроблений курсор з коректним
/// хешем, але сміттєвими полями -> 422 invalid_cursor (не 500); M1 - «має рух» питається лише для сторінки; L3 - гонка unit_locked.
/// Час лише через <see cref="FakeClock"/>.
/// </summary>
public sealed class InventoryReviewFixServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static (FakeInventoryStore Store, InventoryService Svc, LocationRow Loc) Env()
    {
        var store = new FakeInventoryStore();
        var svc = new InventoryService(store, new FakeClock(Noon), new InventoryOptions());
        return (store, svc, store.AddLocation("Main"));
    }

    private static Actor Admin() => Actor.ForTest(Roles.Admin);

    private static void AssertInvalidCursor<T>(Result<T> r)
    {
        Assert.False(r.IsOk);
        Assert.Equal("invalid_cursor", r.Error!.Code);
        Assert.Equal(ErrorKind.Validation, r.Error.Kind);
    }

    private static string Hash(string cursor)
    {
        var b64 = cursor.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|')[^1];
    }

    private static string Forge(string hash, params string[] parts) => InventoryService.EncodeCursor(hash, parts);

    private const string Good = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Forged_name_cursor_with_valid_hash_but_garbage_id_is_422_for_products_and_stock()
    {
        var (store, svc, loc) = Env();
        store.AddProduct("A"); store.AddProduct("B"); store.AddProduct("C");

        var p1 = await svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, null, 1), default);
        Assert.NotNull(p1.Value!.NextCursor);
        var h = Hash(p1.Value.NextCursor!);
        foreach (var bad in new[] { Forge(h, "a", "not-a-guid"), Forge(h, "a", ""), Forge(h, "a", "{" + Good + "}"), Forge(h, "a") })
            AssertInvalidCursor(await svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, bad, 1), default));
        // той самий хеш, чисті поля -> проходить
        Assert.True((await svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, Forge(h, "a", Good), 1), default)).IsOk);

        var s1 = await svc.GetStockAsync(Admin(), new StockListQuery(loc.Id, null, null, null, null, null, null, 1), default);
        Assert.NotNull(s1.Value!.NextCursor);
        var sh = Hash(s1.Value.NextCursor!);
        AssertInvalidCursor(await svc.GetStockAsync(Admin(), new StockListQuery(loc.Id, null, null, null, null, null, Forge(sh, "a", "zzz"), 1), default));
        AssertInvalidCursor(await svc.GetSummaryAsync(Admin(), new SummaryListQuery(null, null, null, null, Forge(InventoryService.ParamHash("summary", null, null, null, null), "a", "zzz"), 1), default));
    }

    [Theory]
    [InlineData("abc", Good)]                       // не число
    [InlineData("-5", Good)]                        // від'ємні ticks
    [InlineData("99999999999999999999", Good)]      // переповнення long
    [InlineData("3155378976000000000", Good)]       // DateTimeOffset.MaxValue.UtcTicks + 1
    [InlineData("1", "not-a-guid")]
    public async Task Forged_time_cursor_is_422_for_movements_operations_stocktakes(string ticks, string id)
    {
        var (_, svc, _) = Env();

        AssertInvalidCursor(await svc.ListMovementsAsync(Admin(),
            new MovementListQuery(null, null, null, null, null, null, Forge(InventoryService.ParamHash("movements", null, null, null, null, null, null), ticks, id), 5), default));
        AssertInvalidCursor(await svc.ListOperationsAsync(Admin(),
            new OperationListQuery(null, null, null, null, Forge(InventoryService.ParamHash("operations", null, null, null, null), ticks, id), 5), default));
        AssertInvalidCursor(await svc.ListStocktakesAsync(Admin(),
            new StocktakeListQuery(null, null, Forge(InventoryService.ParamHash("stocktakes", null, null), ticks, id), 5), default));
    }

    [Theory]
    [InlineData("2", "", "1", Good)]                       // прапорець не 0/1
    [InlineData("0", "", "1", Good)]                       // flag 0 вимагає дату
    [InlineData("0", "2026-13-45", "1", Good)]             // нерозбірна дата
    [InlineData("0", "1999-01-01", "1", Good)]             // рік поза 2000..2100
    [InlineData("1", "", "x", Good)]
    [InlineData("1", "", "1", "zzz")]
    public async Task Forged_batch_cursor_is_422(string flag, string date, string ticks, string id)
    {
        var (_, svc, _) = Env();
        var hash = InventoryService.ParamHash("batches", null, null, "active", 30);
        AssertInvalidCursor(await svc.ListBatchesAsync(Admin(), new BatchListQuery(null, null, null, null, Forge(hash, flag, date, ticks, id), 5), default));
    }

    [Fact]
    public async Task Product_list_asks_for_movements_only_for_the_returned_page()
    {
        var (store, svc, loc) = Env();
        var withMove = store.AddProduct("A");
        for (var i = 0; i < 6; i++) store.AddProduct("P" + i);
        Assert.True((await svc.ReceiveAsync(Admin(), "k1", new ReceiptRequest(loc.Id, null, null, null, null, [new(withMove.Id, 1, null, null, null)]), default)).IsOk);

        var page = await svc.ListProductsAsync(Admin(), new ProductListQuery(null, null, null, null, null, null, 3), default);
        Assert.Equal([3], store.MovementProbeSizes);
        Assert.True(page.Value!.Items.Single(p => p.Id == withMove.Id).HasMovements);
        Assert.All(page.Value.Items.Where(p => p.Id != withMove.Id), p => Assert.False(p.HasMovements));
    }

    [Fact]
    public async Task Unit_change_racing_with_first_receipt_is_unit_locked_with_the_same_code()
    {
        var (store, svc, loc) = Env();
        var p = store.AddProduct("Race", "pcs");
        var req = new ProductRequest("Race", null, "ml", null, "both", null, null, 10m, null, false, null, null);
        // сервіс бачить товар без рухів; між перевіркою й записом сховище вже має рух (перше надходження)
        store.BeforeProductUpdate = () =>
        {
            store.BeforeProductUpdate = null;
            var h = new OperationHeader(Guid.NewGuid(), "receipt", loc.Id, null, null, null, null, null, null, Guid.NewGuid(), Noon, "race-key", "h");
            store.PostReceiptAsync(h, [new ReceiptLine(p.Id, 1, null, null, null)], default).GetAwaiter().GetResult();
        };

        var r = await svc.UpdateProductAsync(Admin(), p.Id, req, default);

        Assert.False(r.IsOk);
        Assert.Equal("unit_locked", r.Error!.Code);
        Assert.Equal(ErrorKind.Validation, r.Error.Kind);
        Assert.Equal("pcs", store.Products[p.Id].Unit);
    }
}
