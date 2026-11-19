using System.Globalization;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyInventory;

/// <summary>
/// Сповіщення персоналу (§32.19): видимість за inventory.view &gt;= own і scope закладу (поза scope - 404 alert_not_found); прочитане
/// спільне для tenant (open -&gt; read, повтор - no-op), resolved ставить лише система (воркер). Без ПД. Час - лише через <see cref="TimeProvider"/>.
/// </summary>
public sealed class InventoryAlertsService(IStaffAlertStore store, TimeProvider clock, ILogger<InventoryAlertsService>? logger = null)
{
    public const int DefaultTake = 50, MaxTake = 200, UnreadCap = 1000;

    private static readonly Error AlertNotFound = Error.NotFound("alert_not_found", "Alert not found.");
    private static readonly Error LocationNotFound = Error.NotFound("location_not_found", "Location not found.");

    private static Error? NeedView(Actor a) =>
        a.Access.AtLeast(PermissionKeys.InventoryView, PermissionLevels.Own) ? null : Error.ForbiddenPermission(PermissionKeys.InventoryView, PermissionLevels.Own);

    private Task<InventoryScope> ScopeAsync(Actor actor, CancellationToken ct) =>
        InventoryScope.ResolveAsync(actor, PermissionKeys.InventoryView, store.SpecialistLocationIdsAsync, ct);

    public async Task<Result<Page<StaffAlertDto>>> ListAsync(Actor actor, StaffAlertListQuery q, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var status = (q.Status ?? "active").ToLowerInvariant();
        IReadOnlyList<string> statuses = status switch
        {
            "active" => ["open", "read"],
            "open" => ["open"],
            "read" => ["read"],
            "resolved" => ["resolved"],
            "all" => ["open", "read", "resolved"],
            _ => [],
        };
        if (statuses.Count == 0) return Error.Validation("invalid_alert_status", "status is invalid.");
        var kind = q.Kind?.ToLowerInvariant();
        if (kind is not null && !StaffAlertKinds.All.Contains(kind)) return Error.Validation("invalid_alert_kind", "kind is invalid.");

        var scope = await ScopeAsync(actor, ct);
        if (q.LocationId is { } lid && !scope.Contains(lid)) return LocationNotFound;
        var hash = InventoryService.ParamHash("alerts", status, kind, q.LocationId);
        var after = InventoryService.DecodeCursor(q.Cursor, hash, "tg");
        if (!after.IsOk) return after.Error!;
        var n = Math.Clamp(q.Take ?? DefaultTake, 1, MaxTake);

        List<StaffAlertDto> rows = [];
        if (scope.All || scope.Ids.Count > 0)
            rows = [.. await store.ListStaffAlertsAsync(new StaffAlertFilter(scope.LocationFilter, statuses, kind, q.LocationId), after.Value, n + 1, ct)];
        var hasMore = rows.Count > n;
        if (hasMore) rows.RemoveAt(n);
        var next = hasMore
            ? InventoryService.EncodeCursor(hash, rows[^1].CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture), rows[^1].Id.ToString())
            : null;
        return new Page<StaffAlertDto>(rows, hasMore, next);
    }

    public async Task<Result<AlertCountDto>> UnreadCountAsync(Actor actor, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ScopeAsync(actor, ct);
        if (!scope.All && scope.Ids.Count == 0) return new AlertCountDto(0);
        return new AlertCountDto(await store.CountOpenAlertsAsync(scope.LocationFilter, UnreadCap, ct));
    }

    public async Task<Result<StaffAlertDto>> MarkReadAsync(Actor actor, Guid id, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ScopeAsync(actor, ct);
        if (await store.GetStaffAlertAsync(id, ct) is not { } a || !scope.Contains(a.Location.Id)) return AlertNotFound;
        var updated = await store.MarkAlertReadAsync(id, actor.UserId, clock.GetUtcNow(), ct);
        if (updated is null) return AlertNotFound;
        if (a.Status == "open") logger?.LogInformation("inventory_alert_read id={Id} user={User}", id, actor.UserId);
        return updated;
    }

    public async Task<Result<AlertsUpdatedDto>> MarkAllReadAsync(Actor actor, Guid? locationId, CancellationToken ct)
    {
        if (NeedView(actor) is { } f) return f;
        var scope = await ScopeAsync(actor, ct);
        if (locationId is { } lid && !scope.Contains(lid)) return LocationNotFound;
        if (!scope.All && scope.Ids.Count == 0) return new AlertsUpdatedDto(0);
        var updated = await store.MarkAllAlertsReadAsync(scope.LocationFilter, locationId, actor.UserId, clock.GetUtcNow(), ct);
        logger?.LogInformation("inventory_alert_read id=all updated={Updated} user={User}", updated, actor.UserId);
        return new AlertsUpdatedDto(updated);
    }
}
