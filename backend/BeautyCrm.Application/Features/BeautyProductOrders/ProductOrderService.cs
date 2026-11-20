using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Application.Features.BeautyProductOrders;

/// <summary>
/// Замовлення товарів клієнтами: публічна частина P1-P5 (§33) і спільні блоки для CRM (TASK-903): ефективний статус, відображення DTO,
/// життєвий цикл оплати (claim -> Charge -> фіналізація/компенсація/reconcile), скасування неоплаченого.
/// Логи без ПД, токенів, сум й Idempotency-Key (§33.18). Час - лише <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ProductOrderService(
    IProductOrderStore store, IPaymentService payments, ProductOrdersOptions options, PublicTenantResolver tenant,
    PublicTokenService tokens, PublicBookingOptions publicOptions, IClientIntegrityVerifier integrity, TimeProvider clock,
    ILogger<ProductOrderService> log)
{
    public const string Disabled = "product_orders_disabled";
    public const string Currency = "UAH";
    private const string PendingPayment = "pending_payment";

    /// <summary>Єдина відповідь на невідомий/чужий/зіпсований токен.</summary>
    public static readonly Error TokenNotFound = Error.NotFound("not_found", "Not found.");

    [GeneratedRegex("^[A-Za-z0-9._:-]{16,128}$")]
    private static partial Regex KeyPattern();

    private static readonly Error DisabledError = Error.NotFound(Disabled, "Product orders are not available.");
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("BeautyCrm.ProductOrders");

    /// <summary>Функція доступна: прапор сервера І модуль beauty_inventory tenant-а (beauty_booking перевірено визначенням tenant).</summary>
    public bool IsEnabled => options.Enabled && tenant.HasModule(BeautyModules.Inventory) && tenant.HasModule("beauty_booking");

    // ---------- спільні блоки (публічний + CRM) ----------

    /// <summary>§33.16: pending_payment без оплати в процесі й з минулим резервом = expired (навіть якщо воркер ще не закрив).</summary>
    public static string EffectiveStatus(ProductOrderView o, DateTimeOffset now) =>
        EffectiveStatus(o.Status, o.PaymentStartedAt, o.ReservationExpiresAt, now);

    public static string EffectiveStatus(string status, DateTimeOffset? paymentStartedAt, DateTimeOffset reservationExpiresAt, DateTimeOffset now) =>
        status == PendingPayment && paymentStartedAt is null && reservationExpiresAt <= now ? "expired" : status;

    public PublicProductOrderDto ToDto(ProductOrderView o, DateTimeOffset now)
    {
        var status = EffectiveStatus(o, now);
        var pending = status == PendingPayment;
        var inProgress = pending && o.PaymentStartedAt is not null;
        var attemptsLeft = Math.Max(0, options.MaxPaymentAttempts - o.PaymentAttempts);
        var closed = status is "cancelled" or "expired" or "refunded";
        return new PublicProductOrderDto(
            o.Number, status, new PublicOrderLocationDto(o.LocationId, o.LocationName, o.LocationAddress),
            [.. o.Lines.Select(l => new PublicProductOrderLineDto(l.ProductId, l.Name, l.Unit, l.Quantity, l.UnitPrice, l.LineTotal))],
            o.Total, Currency, o.CreatedAt,
            pending ? o.ReservationExpiresAt : null, o.PaidAt, o.FulfilledAt,
            closed ? (status == "expired" && o.Status == PendingPayment ? o.ReservationExpiresAt : o.ClosedAt) : null,
            closed ? (status == "expired" && o.Status == PendingPayment ? "expired" : o.CloseReason) : null,
            status == "refunded" ? o.RefundAmount : null,
            inProgress, pending ? attemptsLeft : 0,
            pending && !inProgress && attemptsLeft > 0, pending && !inProgress);
    }

    private Error WithOrder(ErrorKind kind, string code, string message, ProductOrderView o, DateTimeOffset now) =>
        new(kind, code, message, null, new Dictionary<string, object?> { ["order"] = ToDto(o, now) });

    private static void Result_(string result)
    {
        System.Diagnostics.Activity.Current?.SetTag("beauty.product_order.result", result);
        ResultCounter.Add(1, new KeyValuePair<string, object?>("result", result));
    }

    private static readonly System.Diagnostics.Metrics.Counter<long> ResultCounter =
        Meter.CreateCounter<long>("beauty.product_order.result", description: "Product order outcomes");

    // ---------- P1: каталог ----------

    public async Task<Result<PublicProductCatalogDto>> CatalogAsync(Guid? locationId, CancellationToken ct)
    {
        if (!IsEnabled) return DisabledError;
        if (locationId is not { } id || id == Guid.Empty) return Error.NotFound("location_not_found", "Location not found.");
        var data = await store.ListCatalogAsync(id, clock.GetUtcNow(), ct);
        if (data is null) return Error.NotFound("location_not_found", "Location not found.");
        return new PublicProductCatalogDto(
            new PublicOrderLocationDto(data.Location.Id, data.Location.Name, data.Location.Address), Currency, options.ReservationMinutes,
            ProductOrdersOptions.MaxLines, ProductOrdersOptions.MaxQuantityPerLine,
            [.. data.Items.Select(i => new PublicProductDto(i.Id, i.Name, i.Unit, i.Price, i.CategoryPath, i.InStock))], data.Truncated);
    }

    // ---------- P3: перегляд (працює завжди, навіть коли функцію вимкнено) ----------

    public async Task<Result<PublicProductOrderDto>> GetAsync(string? token, CancellationToken ct)
    {
        if (await FindAsync(token, ct) is not { } order) return TokenNotFound;
        return ToDto(order, clock.GetUtcNow());
    }

    private async Task<ProductOrderView?> FindAsync(string? token, CancellationToken ct) =>
        PublicTokenService.IsWellFormed(token) ? await store.FindByTokenHashAsync(PublicTokenService.Hash(token!), ct) : null;

    // ---------- P2: створення ----------

    public async Task<Result<PublicProductOrderCreateResult>> CreateAsync(CreateProductOrderRequest req, PublicRequestContext ctx, CancellationToken ct)
    {
        if (!IsEnabled) return DisabledError;
        if (tenant.TenantId is not { } tenantId) return TokenNotFound;

        if (string.IsNullOrWhiteSpace(ctx.IdempotencyKey))
            return Error.Validation("idempotency_key_required", "Idempotency-Key header is required.");
        if (!KeyPattern().IsMatch(ctx.IdempotencyKey))
            return Error.Validation("invalid_idempotency_key", "Idempotency-Key must be 16-128 chars of A-Z a-z 0-9 . _ : -.");
        // Honeypot (web): відповідь не розкриває причину; нічого не створюється.
        if (!string.IsNullOrEmpty(req.Website)) return Error.Validation("invalid_request", "Invalid request.");
        if (req.LocationId is not { } locationId || locationId == Guid.Empty)
            return Error.Validation("invalid_request", "locationId is required.");

        var name = req.Customer?.Name?.Trim();
        if (name is null || name.Length is < 2 or > 100 || name.EnumerateRunes().Any(r => Rune.IsControl(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format))
            return Error.Validation("invalid_name", "Customer name must be 2-100 characters.");
        var phone = PhoneNormalizer.Normalize(req.Customer?.Phone);
        if (phone is null) return Error.Validation("invalid_phone", "Phone must be in international format, e.g. +380501234567.");

        var form = ValidateLines(req.Lines);
        if (form.Error is not null) return form.Error;
        var lines = form.Lines!;
        if (req.ExpectedTotal is { } et && (et < 0 || decimal.Round(et, 2) != et))
            return Error.Validation("invalid_expected_total", "expectedTotal must be >= 0 with at most 2 decimals.");

        var keyHash = PublicTokenService.Hash("idem:" + ctx.IdempotencyKey);
        var requestHash = PublicTokenService.Hash(string.Join('|', locationId, name, phone,
            string.Join(';', lines.OrderBy(l => l.ProductId).Select(l => $"{l.ProductId:N}:{l.Quantity}")),
            req.ExpectedTotal?.ToString("0.00", CultureInfo.InvariantCulture) ?? ""));

        // Ідемпотентність: той самий ключ + те саме тіло -> поточний стан того самого замовлення; captcha/integrity не повторюються.
        if (await store.FindByIdempotencyKeyAsync(keyHash, ct) is { } hit) return Replay(tenantId, hit, requestHash);

        if (await CheckClientProofAsync(ctx, ct) is { } proofError) return proofError;

        var orderId = Guid.NewGuid();
        var token = tokens.CreateForProductOrder(tenantId, orderId);
        var now = clock.GetUtcNow();
        var outcome = await store.CreateAsync(
            new NewProductOrder(orderId, PublicTokenService.Hash(token), keyHash, requestHash, locationId,
                ctx.Client switch { ClientKind.Ios => "ios", ClientKind.Android => "android", _ => "web" }, name, phone, lines, req.ExpectedTotal),
            new CreateLimits(options.ReservationMinutes, options.MaxOrderTotal, options.MaxPendingPerPhone, options.MaxCreatesPerPhonePerHour,
                options.MaxCreatesPerLocationPerHour), now, ct);

        switch (outcome.Status)
        {
            case CreateStatus.Replay: return Replay(tenantId, outcome.Order!, requestHash);
            case CreateStatus.LocationNotFound: return Error.NotFound("location_not_found", "Location not found.");
            case CreateStatus.ProductUnavailable:
                return new Error(ErrorKind.Validation, "product_unavailable", "Some products are no longer available.", null,
                    new Dictionary<string, object?> { ["productIds"] = outcome.ProductIds });
            case CreateStatus.TotalTooLarge: return Error.Validation("order_total_too_large", "The order total is too large.");
            case CreateStatus.PriceChanged:
                return new Error(ErrorKind.Conflict, "price_changed", "Prices have changed.", null,
                    new Dictionary<string, object?> { ["total"] = outcome.Total });
            case CreateStatus.LimitReached:
                log.LogWarning("product_order_limit_reached scope={Scope} tenant={TenantId} location={LocationId}", outcome.LimitScope, tenantId, locationId);
                return Error.Validation("order_limit_reached", "Too many open orders. Try again later.");
            case CreateStatus.InsufficientStock:
                return new Error(ErrorKind.Validation, "insufficient_stock", "Not enough stock for some products.", null,
                    new Dictionary<string, object?> { ["productIds"] = outcome.ProductIds });
        }

        var order = outcome.Order!;
        log.LogInformation("product_order_created order={OrderId} location={LocationId} lines={Lines} source={Source} replayed={Replayed}",
            order.Id, order.LocationId, order.Lines.Count, ctx.Client.ToString().ToLowerInvariant(), false);
        Result_("created");
        return new PublicProductOrderCreateResult(new PublicProductOrderCreatedDto(token, ToDto(order, now)), false);
    }

    private Result<PublicProductOrderCreateResult> Replay(Guid tenantId, ProductOrderView hit, string requestHash)
    {
        if (hit.IdempotencyRequestHash != requestHash)
            return Error.Validation("idempotency_key_reused", "This Idempotency-Key was used with a different request.");
        log.LogInformation("product_order_created order={OrderId} location={LocationId} lines={Lines} source={Source} replayed={Replayed}",
            hit.Id, hit.LocationId, hit.Lines.Count, "-", true);
        return new PublicProductOrderCreateResult(
            new PublicProductOrderCreatedDto(tokens.CreateForProductOrder(tenantId, hit.Id), ToDto(hit, clock.GetUtcNow())), true);
    }

    private static (IReadOnlyList<NewOrderLine>? Lines, Error? Error) ValidateLines(IReadOnlyList<CreateProductOrderLine>? raw)
    {
        if (raw is null || raw.Count == 0) return (null, Error.Validation("invalid_lines", "At least one line is required."));
        if (raw.Count > ProductOrdersOptions.MaxLines) return (null, Error.Validation("too_many_lines", $"At most {ProductOrdersOptions.MaxLines} lines."));
        var seen = new HashSet<Guid>();
        var lines = new List<NewOrderLine>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var l = raw[i];
            if (l?.ProductId is not { } pid || pid == Guid.Empty) return (null, LineError("invalid_lines", "productId is required.", i));
            if (!seen.Add(pid)) return (null, LineError("duplicate_line", "A product may appear only once.", i));
            if (l.Quantity is not { } q || q != decimal.Truncate(q) || q < 1 || q > ProductOrdersOptions.MaxQuantityPerLine)
                return (null, LineError("invalid_quantity", $"quantity must be an integer 1..{ProductOrdersOptions.MaxQuantityPerLine}.", i));
            lines.Add(new NewOrderLine(pid, (int)q));
        }
        return (lines, null);
    }

    private static Error LineError(string code, string message, int line) =>
        new(ErrorKind.Validation, code, message, null, new Dictionary<string, object?> { ["line"] = line });

    /// <summary>Політика за міткою клієнта (як у публічному записі): none / monitor / enforce.</summary>
    private async Task<Error?> CheckClientProofAsync(PublicRequestContext ctx, CancellationToken ct)
    {
        var mode = publicOptions.ProtectionFor(ctx.Client);
        if (mode == ProtectionMode.None) return null;
        var web = ctx.Client == ClientKind.Web;
        var proof = new ClientProof(
            web ? ClientProofKind.Captcha : ctx.Client == ClientKind.Ios ? ClientProofKind.AppAttest : ClientProofKind.PlayIntegrity,
            web ? ctx.CaptchaToken : ctx.IntegrityToken);
        var ok = !string.IsNullOrWhiteSpace(proof.Token) && await integrity.VerifyAsync(proof, ctx.RemoteIp, ct);
        if (mode == ProtectionMode.Monitor)
        {
            System.Diagnostics.Activity.Current?.SetTag("beauty.integrity.verdict", $"{ctx.Client}:{(ok ? "ok" : "fail")}");
            return null;
        }
        return ok ? null : web
            ? Error.Validation("captcha_failed", "CAPTCHA verification failed.")
            : Error.Validation("integrity_failed", "Client verification failed.");
    }

    // ---------- P4: оплата ----------

    public async Task<Result<PublicProductOrderResult>> PayAsync(string? token, CancellationToken ct)
    {
        if (!IsEnabled) return DisabledError;
        if (await FindAsync(token, ct) is not { } found) return TokenNotFound;
        var now = clock.GetUtcNow();

        var claim = await store.ClaimPaymentAsync(found.Id, now, TimeSpan.FromSeconds(options.PaymentStaleSeconds), options.MaxPaymentAttempts, ct);
        if (claim.Kind == ClaimKind.NoClaim) return NoClaim(claim.Order ?? found, now);

        // Після claim: власний timeout замість токена запиту (обрив клієнта не лишає списання без фіналізації).
        using var cts = new CancellationTokenSource(ProductOrderPaymentFlow.PostClaimTimeout);
        var pct = cts.Token;
        var order = claim.Order!;
        var claimAt = order.PaymentStartedAt!.Value;
        if (claim.Kind == ClaimKind.Reconcile)
            log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", found.Id, order.PaymentAttempts, "reconciled");

        if (claim.Kind == ClaimKind.Reconcile && claim.PaymentStatus == "paid")
        {
            if (claim.RefundPending) return await CompensateAsync(order, claim.PaymentId, claimAt, true, pct);
            return await FinalizeAsync(order, claim.PaymentId, claimAt, null, pct);
        }
        return await ChargeAsync(order, claim.PaymentId, claimAt, pct);
    }

    private Result<PublicProductOrderResult> NoClaim(ProductOrderView o, DateTimeOffset now)
    {
        var status = EffectiveStatus(o, now);
        switch (status)
        {
            case "paid" or "fulfilled":
                log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", o.Id, o.PaymentAttempts, "replayed");
                Result_("replayed");
                return new PublicProductOrderResult(ToDto(o, now), true);
            case "expired": return WithOrder(ErrorKind.Conflict, "order_expired", "The reservation has expired.", o, now);
            case "cancelled" or "refunded": return WithOrder(ErrorKind.Conflict, "order_closed", "The order is closed.", o, now);
        }
        if (o.PaymentStartedAt is not null)
        {
            log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", o.Id, o.PaymentAttempts, "in_progress");
            return WithOrder(ErrorKind.Conflict, "payment_in_progress", "Payment is in progress.", o, now);
        }
        if (o.PaymentAttempts >= options.MaxPaymentAttempts)
            return WithOrder(ErrorKind.Validation, "payment_attempts_exceeded", "Payment attempts exhausted.", o, now);
        return WithOrder(ErrorKind.Conflict, "payment_in_progress", "Payment is in progress.", o, now);
    }

    /// <summary>
    /// Списання поза транзакцією БД (ChargeAsync ідемпотентний за PaymentId). Виняток/timeout/невизначена відповідь = результат НЕВІДОМИЙ:
    /// claim не скидаємо, лишаємо «в процесі» (reconcile дозавершить тим самим PaymentId). Скидаємо лише явну відмову (код) і лише CAS-ом.
    /// </summary>
    private async Task<Result<PublicProductOrderResult>> ChargeAsync(ProductOrderView o, Guid paymentId, DateTimeOffset claimAt, CancellationToken ct)
    {
        PaymentResult charge;
        try
        {
            charge = await payments.ChargeAsync(new PaymentCharge(paymentId, PaymentPurpose.ProductOrder, o.Id, o.Total, Currency), ct);
        }
        catch (Exception ex)
        {
            log.LogError("product_order_payment_unknown order={OrderId} error={Error}", o.Id, ex.GetType().Name);
            return await InProgressAsync(o, "unknown");
        }
        if (!charge.Success)
        {
            if (!ProductOrderPaymentFlow.IsExplicitDecline(charge))
            {
                log.LogError("product_order_payment_unknown order={OrderId} error={Error}", o.Id, "no_decline_code");
                return await InProgressAsync(o, "unknown");
            }
            if (!await store.MarkPaymentFailedAsync(o.Id, paymentId, claimAt, ct))
            {
                // claim уже не наш (перехоплено reconcile) або платіж не pending: стан належить іншому виконавцю
                log.LogWarning("product_order_payment_cas_lost order={OrderId}", o.Id);
                return NoClaim(await store.GetOrderAsync(o.Id, ct) ?? o, clock.GetUtcNow());
            }
            var fresh = await store.GetOrderAsync(o.Id, ct) ?? o;
            log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", o.Id, fresh.PaymentAttempts, "failed");
            Result_("payment_failed");
            return WithOrder(ErrorKind.PaymentFailed, "payment_failed", "Card payment failed.", fresh, clock.GetUtcNow());
        }
        return await FinalizeAsync(o, paymentId, claimAt, charge.ProviderPaymentId, ct);
    }

    private async Task<Result<PublicProductOrderResult>> InProgressAsync(ProductOrderView o, string result)
    {
        Result_("payment_" + result);
        var fresh = await store.GetOrderAsync(o.Id, CancellationToken.None) ?? o;
        return WithOrder(ErrorKind.Conflict, "payment_in_progress", "Payment is in progress.", fresh, clock.GetUtcNow());
    }

    private async Task<Result<PublicProductOrderResult>> FinalizeAsync(ProductOrderView o, Guid paymentId, DateTimeOffset claimAt, string? providerPaymentId, CancellationToken ct)
    {
        var fin = await store.FinalizePaidAsync(o.Id, paymentId, claimAt, providerPaymentId, clock.GetUtcNow(), ct);
        var now = clock.GetUtcNow();
        switch (fin.Kind)
        {
            case FinalizeKind.Paid:
                log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", o.Id, fin.Order!.PaymentAttempts, "paid");
                Result_("paid");
                return new PublicProductOrderResult(ToDto(fin.Order!, now), false);
            case FinalizeKind.AlreadyPaid:
                return new PublicProductOrderResult(ToDto(fin.Order!, now), true);
            case FinalizeKind.NotPending:
                return WithOrder(ErrorKind.Conflict, "order_closed", "The order is closed.", fin.Order!, now);
            case FinalizeKind.ClaimLost:
                log.LogWarning("product_order_payment_cas_lost order={OrderId}", o.Id);
                return NoClaim(fin.Order ?? o, now);
        }

        // Товар зник під час оплати: повернення 100%.
        log.LogInformation("product_order_payment order={OrderId} attempt={Attempt} result={Result}", o.Id, o.PaymentAttempts, "stock_unavailable");
        Result_("stock_unavailable");
        return await CompensateAsync(o, paymentId, claimAt, false, ct);
    }

    /// <summary>Намір (окрема транзакція) -> повернення -> refunded. Збій = лишається «в процесі» з маркером наміру; reconcile лише повторює повернення.</summary>
    private async Task<Result<PublicProductOrderResult>> CompensateAsync(ProductOrderView o, Guid paymentId, DateTimeOffset claimAt, bool intentMarked, CancellationToken ct)
    {
        await ProductOrderPaymentFlow.CompensateAsync(store, payments, log, clock, o.Id, paymentId, o.Total, claimAt, intentMarked);
        var after = await store.GetOrderAsync(o.Id, ct) ?? o;
        return WithOrder(ErrorKind.Conflict, "stock_unavailable", "The product ran out during payment.", after, clock.GetUtcNow());
    }

    // ---------- P5: скасування клієнтом ----------

    public async Task<Result<PublicProductOrderResult>> CancelAsync(string? token, CancellationToken ct)
    {
        if (!IsEnabled) return DisabledError;
        if (await FindAsync(token, ct) is not { } found) return TokenNotFound;
        var now = clock.GetUtcNow();
        var cancelled = await store.CancelUnpaidAsync(found.Id, "client", null, null, now, ct);
        var o = await store.GetOrderAsync(found.Id, ct) ?? found;
        if (cancelled)
        {
            log.LogInformation("product_order_cancelled order={OrderId} by={By} refund={Refund}", o.Id, "client", false);
            Result_("cancelled");
            return new PublicProductOrderResult(ToDto(o, now), false);
        }
        var status = EffectiveStatus(o, now);
        if (status == "cancelled" && o.CloseReason == "client") return new PublicProductOrderResult(ToDto(o, now), true);
        if (status == "expired") return WithOrder(ErrorKind.Conflict, "order_expired", "The reservation has expired.", o, now);
        if (status == PendingPayment) return WithOrder(ErrorKind.Conflict, "payment_in_progress", "Payment is in progress.", o, now);
        return WithOrder(ErrorKind.Conflict, "cannot_cancel", "Only the venue can cancel this order.", o, now);
    }
}

public static class ProductOrderServiceExtensions
{
    public static IServiceCollection AddBeautyProductOrdersApplication(this IServiceCollection services, ProductOrdersOptions options)
    {
        services.AddSingleton(options);
        services.AddScoped<ProductOrderService>();
        services.AddScoped<ProductOrderAdminService>(); // CRM (TASK-903): не залежить від PublicTenantResolver й прапора Enabled
        return services;
    }
}
