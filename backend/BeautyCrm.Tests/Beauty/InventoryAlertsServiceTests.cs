using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyInventory;

namespace BeautyCrm.Tests.Beauty;

/// <summary>TASK-886 (§32.19): сповіщення персоналу - сервіс на <see cref="FakeAlertStore"/>: scope закладу, статуси, спільне прочитане, пагінація, dedupe writer. Час - FakeClock.</summary>
public sealed class InventoryAlertsServiceTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private sealed class Env
    {
        public readonly FakeAlertStore Store = new();
        public readonly FakeClock Clock = new(Noon);
        public readonly InventoryAlertsService Svc;
        public readonly LocationRow L1, L2;
        public Env()
        {
            Svc = new InventoryAlertsService(Store, Clock);
            L1 = Store.AddLocation("Main");
            L2 = Store.AddLocation("Second");
        }
    }

    private static Actor Admin() => Actor.ForTest(Roles.Admin);
    private static Actor Specialist(Guid sid, params (string Key, string Level)[] o)
    {
        var p = PermissionPresets.For(Roles.Specialist).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var (k, l) in o) p[k] = l;
        return Actor.ForTest(Roles.Specialist, sid, permissions: p);
    }

    private static void AssertErr<T>(Result<T> r, string code, ErrorKind kind)
    {
        Assert.False(r.IsOk, "expected error " + code);
        Assert.Equal(code, r.Error!.Code);
        Assert.Equal(kind, r.Error.Kind);
    }

    [Fact]
    public async Task List_default_is_active_newest_first_and_excludes_resolved()
    {
        var e = new Env();
        var old = e.Store.Seed(e.L1.Id, createdAt: Noon.AddHours(-3));
        var read = e.Store.Seed(e.L1.Id, "expiring", "read", Noon.AddHours(-1));
        e.Store.Seed(e.L1.Id, "shortage", "resolved", Noon);

        var r = (await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, null, null, null, null), default)).Value!;
        Assert.Equal([read.Id, old.Id], r.Items.Select(a => a.Id));
        var all = (await e.Svc.ListAsync(Admin(), new StaffAlertListQuery("all", null, null, null, null), default)).Value!;
        Assert.Equal(3, all.Items.Count);
        var resolved = (await e.Svc.ListAsync(Admin(), new StaffAlertListQuery("resolved", null, null, null, null), default)).Value!;
        Assert.Equal("shortage", Assert.Single(resolved.Items).Kind);
    }

    [Fact]
    public async Task List_filters_by_kind_and_location_and_validates_enums()
    {
        var e = new Env();
        e.Store.Seed(e.L1.Id, "low_stock");
        e.Store.Seed(e.L2.Id, "expiring");
        Assert.Single((await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, "expiring", null, null, null), default)).Value!.Items);
        Assert.Single((await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, null, e.L1.Id, null, null), default)).Value!.Items);
        AssertErr(await e.Svc.ListAsync(Admin(), new StaffAlertListQuery("bogus", null, null, null, null), default), "invalid_alert_status", ErrorKind.Validation);
        AssertErr(await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, "bogus", null, null, null), default), "invalid_alert_kind", ErrorKind.Validation);
    }

    [Fact]
    public async Task Pagination_walks_all_pages_without_gaps_and_rejects_foreign_cursor()
    {
        var e = new Env();
        var ids = Enumerable.Range(0, 5).Select(i => e.Store.Seed(e.L1.Id, createdAt: Noon.AddMinutes(-i)).Id).ToList();
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var p = (await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, null, null, cursor, 2), default)).Value!;
            seen.AddRange(p.Items.Select(a => a.Id));
            cursor = p.NextCursor;
            Assert.Equal(cursor is not null, p.HasMore);
        } while (cursor is not null);
        Assert.Equal(ids, seen);

        var first = (await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, null, null, null, 2), default)).Value!;
        AssertErr(await e.Svc.ListAsync(Admin(), new StaffAlertListQuery("all", null, null, first.NextCursor, 2), default), "invalid_cursor", ErrorKind.Validation);
        AssertErr(await e.Svc.ListAsync(Admin(), new StaffAlertListQuery(null, null, null, "garbage!", 2), default), "invalid_cursor", ErrorKind.Validation);
    }

    [Fact]
    public async Task Own_scope_sees_only_own_locations_and_foreign_ones_are_404()
    {
        var e = new Env();
        var sid = Guid.NewGuid();
        e.Store.SpecLocations[sid] = [e.L1.Id];
        var mine = e.Store.Seed(e.L1.Id);
        var foreign = e.Store.Seed(e.L2.Id);
        var spec = Specialist(sid);

        var list = (await e.Svc.ListAsync(spec, new StaffAlertListQuery(null, null, null, null, null), default)).Value!;
        Assert.Equal([mine.Id], list.Items.Select(a => a.Id));
        AssertErr(await e.Svc.ListAsync(spec, new StaffAlertListQuery(null, null, e.L2.Id, null, null), default), "location_not_found", ErrorKind.NotFound);
        Assert.Equal(1, (await e.Svc.UnreadCountAsync(spec, default)).Value!.Count);
        AssertErr(await e.Svc.MarkReadAsync(spec, foreign.Id, default), "alert_not_found", ErrorKind.NotFound);
        Assert.Equal("open", e.Store.Alerts.First(a => a.Id == foreign.Id).Status);
        AssertErr(await e.Svc.MarkAllReadAsync(spec, e.L2.Id, default), "location_not_found", ErrorKind.NotFound);
        Assert.Equal(1, (await e.Svc.MarkAllReadAsync(spec, null, default)).Value!.Updated);
        Assert.Equal("open", e.Store.Alerts.First(a => a.Id == foreign.Id).Status);
    }

    [Fact]
    public async Task Specialist_without_locations_sees_nothing_and_view_none_is_forbidden()
    {
        var e = new Env();
        e.Store.Seed(e.L1.Id);
        var lonely = Specialist(Guid.NewGuid());
        Assert.Empty((await e.Svc.ListAsync(lonely, new StaffAlertListQuery(null, null, null, null, null), default)).Value!.Items);
        Assert.Equal(0, (await e.Svc.UnreadCountAsync(lonely, default)).Value!.Count);
        var none = Specialist(Guid.NewGuid(), ("inventory.view", "none"));
        AssertErr(await e.Svc.ListAsync(none, new StaffAlertListQuery(null, null, null, null, null), default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.UnreadCountAsync(none, default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.MarkReadAsync(none, Guid.NewGuid(), default), "forbidden_role", ErrorKind.Forbidden);
        AssertErr(await e.Svc.MarkAllReadAsync(none, null, default), "forbidden_role", ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Mark_read_moves_open_to_read_with_clock_time_and_is_noop_for_read_and_resolved()
    {
        var e = new Env();
        var open = e.Store.Seed(e.L1.Id);
        var resolved = e.Store.Seed(e.L1.Id, "shortage", "resolved");
        var user = Admin();

        var r = (await e.Svc.MarkReadAsync(user, open.Id, default)).Value!;
        Assert.Equal("read", r.Status);
        Assert.Equal(Noon, r.ReadAt);
        Assert.Equal(user.UserId, r.ReadBy!.Id);

        e.Clock.Now = Noon.AddHours(1);
        var again = (await e.Svc.MarkReadAsync(Actor.ForTest(Roles.Owner), open.Id, default)).Value!;
        Assert.Equal("read", again.Status);
        Assert.Equal(Noon, again.ReadAt); // не перезаписується

        var res = (await e.Svc.MarkReadAsync(user, resolved.Id, default)).Value!;
        Assert.Equal("resolved", res.Status);
        AssertErr(await e.Svc.MarkReadAsync(user, Guid.NewGuid(), default), "alert_not_found", ErrorKind.NotFound);
    }

    [Fact]
    public async Task Read_state_is_shared_for_all_users_of_the_tenant()
    {
        var e = new Env();
        var a = e.Store.Seed(e.L1.Id);
        await e.Svc.MarkReadAsync(Admin(), a.Id, default);
        Assert.Equal(0, (await e.Svc.UnreadCountAsync(Actor.ForTest(Roles.Owner), default)).Value!.Count);
    }

    [Fact]
    public async Task Unread_count_counts_only_open_and_is_capped_at_1000()
    {
        var e = new Env();
        e.Store.Seed(e.L1.Id);
        e.Store.Seed(e.L1.Id, status: "read");
        e.Store.Seed(e.L1.Id, status: "resolved");
        Assert.Equal(1, (await e.Svc.UnreadCountAsync(Admin(), default)).Value!.Count);
        for (var i = 0; i < 1100; i++) e.Store.Seed(e.L2.Id);
        Assert.Equal(InventoryAlertsService.UnreadCap, (await e.Svc.UnreadCountAsync(Admin(), default)).Value!.Count);
    }

    [Fact]
    public async Task Read_all_updates_only_open_optionally_in_one_location()
    {
        var e = new Env();
        e.Store.Seed(e.L1.Id);
        e.Store.Seed(e.L1.Id);
        e.Store.Seed(e.L2.Id);
        e.Store.Seed(e.L2.Id, status: "resolved");
        Assert.Equal(2, (await e.Svc.MarkAllReadAsync(Admin(), e.L1.Id, default)).Value!.Updated);
        Assert.Equal(1, (await e.Svc.MarkAllReadAsync(Admin(), null, default)).Value!.Updated);
        Assert.Equal(0, (await e.Svc.MarkAllReadAsync(Admin(), null, default)).Value!.Updated);
        Assert.Equal("resolved", e.Store.Alerts.Last().Status);
    }

    [Fact]
    public async Task Writer_dedupes_shortage_by_operation_and_product_and_carries_no_personal_data()
    {
        var e = new Env();
        var op = Guid.NewGuid();
        var prod = Guid.NewGuid();
        Assert.True(await e.Store.CreateShortageAlertAsync(e.L1.Id, prod, op, 2.5m, Noon, default));
        Assert.False(await e.Store.CreateShortageAlertAsync(e.L1.Id, prod, op, 9m, Noon, default));
        Assert.True(await e.Store.CreateShortageAlertAsync(e.L1.Id, Guid.NewGuid(), op, 1m, Noon, default));

        var a = e.Store.Alerts.First();
        Assert.Equal("shortage", a.Kind);
        Assert.Equal(2.5m, a.Quantity);
        Assert.Equal(op, a.OperationId);
        Assert.Equal($"shortage:{op}:{prod}", StaffAlertKeys.Shortage(op, prod));
        // DTO не має полів клієнта/запису
        var names = typeof(StaffAlertDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.DoesNotContain(names, n => n.Contains("client") || n.Contains("appointment") || n.Contains("phone") || n.Contains("master") || n.Contains("specialist"));
    }

    [Fact]
    public void Dedupe_keys_follow_the_contract()
    {
        var l = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var p = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Assert.Equal($"low_stock:{l}:{p}:2026-11-19", StaffAlertKeys.LowStock(l, p, new DateOnly(2026, 11, 19)));
        Assert.Equal($"expiring:{l}:7", StaffAlertKeys.Expiring(l, 7));
    }
}
