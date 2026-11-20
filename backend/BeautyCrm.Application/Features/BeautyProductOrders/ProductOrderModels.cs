namespace BeautyCrm.Application.Features.BeautyProductOrders;

// Замовлення товарів клієнтами (ADR-026, контракт §33, TASK-902). Публічні DTO без ПД (ім'я, телефон), без внутрішніх id замовлення/платежу,
// без кількостей на складі й закупівельних цін. Спільні типи порту використовує й майбутній CRM-шар (TASK-903).

/// <summary>Конфігурація <c>ProductOrders:*</c> (§33.3). Значення читає й перевіряє Api-шар (невалідне = помилка старту).</summary>
public sealed class ProductOrdersOptions
{
    public const int MaxLines = 20;
    public const int MaxQuantityPerLine = 10;
    public const int MaxCatalogItems = 500;

    /// <summary>Лише литерал <c>true</c> (без регістру) вмикає; за замовчуванням вимкнено.</summary>
    public bool Enabled { get; set; }
    public int ReservationMinutes { get; set; } = 15;
    public int PaymentStaleSeconds { get; set; } = 120;
    public int MaxPaymentAttempts { get; set; } = 5;
    public int MaxPendingPerPhone { get; set; } = 2;
    public int MaxCreatesPerPhonePerHour { get; set; } = 5;
    /// <summary>0 = вимкнено.</summary>
    public int MaxCreatesPerLocationPerHour { get; set; } = 60;
    public decimal MaxOrderTotal { get; set; } = 50_000m;

    public static bool IsOn(string? value) => value is not null && value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Prod-запобіжник (§33.16): функція ввімкнена + Production (або невизначене середовище) + mock-оплата = помилка старту.</summary>
    public static void ValidateForHost(bool enabled, bool productionOrUnknown, bool paymentIsMock)
    {
        if (enabled && productionOrUnknown && paymentIsMock)
            throw new InvalidOperationException(
                "ProductOrders:Enabled=true in Production requires a real IPaymentService (MockPaymentService is registered). " +
                "Product orders stay disabled until a payment provider is chosen (ADR-026 §2.2).");
    }
}

// ---------- публічні DTO ----------

public sealed record PublicOrderLocationDto(Guid Id, string Name, string? Address);

public sealed record PublicProductDto(Guid Id, string Name, string Unit, decimal Price, string? CategoryPath, bool InStock);

public sealed record PublicProductCatalogDto(
    PublicOrderLocationDto Location, string Currency, int ReservationMinutes, int MaxLines, int MaxQuantityPerLine,
    IReadOnlyList<PublicProductDto> Items, bool Truncated);

public sealed record PublicProductOrderLineDto(Guid ProductId, string Name, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record PublicProductOrderDto(
    int Number, string Status, PublicOrderLocationDto Location, IReadOnlyList<PublicProductOrderLineDto> Lines, decimal Total, string Currency,
    DateTimeOffset CreatedAt, DateTimeOffset? ReservationExpiresAt, DateTimeOffset? PaidAt, DateTimeOffset? FulfilledAt,
    DateTimeOffset? ClosedAt, string? CloseReason, decimal? RefundAmount, bool PaymentInProgress, int PaymentAttemptsLeft, bool CanPay, bool CanCancel);

public sealed record PublicProductOrderCreatedDto(string PublicToken, PublicProductOrderDto Order);

public sealed record PublicProductOrderEnvelope(PublicProductOrderDto Order);

public sealed record PublicProductOrderCreateResult(PublicProductOrderCreatedDto Created, bool Replayed);

/// <param name="Replayed">true - повтор (уже оплачено / уже скасовано клієнтом): HTTP 200 + Idempotent-Replayed.</param>
public sealed record PublicProductOrderResult(PublicProductOrderDto Order, bool Replayed);

// ---------- вхідні DTO ----------

public sealed record CreateProductOrderCustomer(string? Name, string? Phone);

/// <param name="Quantity">decimal, щоб нецілі значення давали invalid_quantity, а не помилку розбору.</param>
public sealed record CreateProductOrderLine(Guid? ProductId, decimal? Quantity);

public sealed record CreateProductOrderRequest(
    Guid? LocationId, CreateProductOrderCustomer? Customer, IReadOnlyList<CreateProductOrderLine>? Lines,
    decimal? ExpectedTotal = null, string? CaptchaToken = null, string? Website = null);

// ---------- порт даних ----------

public sealed record OrderLocationRow(Guid Id, string Name, string? Address, string Timezone);

public sealed record CatalogRow(Guid Id, string Name, string Unit, decimal Price, string? CategoryPath, bool InStock);

public sealed record CatalogData(OrderLocationRow Location, IReadOnlyList<CatalogRow> Items, bool Truncated);

public sealed record ProductOrderLineView(Guid ProductId, string Name, string Unit, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Стан замовлення для сервісу/CRM. Ім'я й телефон гостя - лише внутрішньо (прив'язка клієнта після оплати); у публічний DTO не потрапляють.</summary>
public sealed record ProductOrderView(
    Guid Id, Guid LocationId, string LocationName, string? LocationAddress, int Number, string Status, string GuestName, string GuestPhone,
    decimal Total, DateTimeOffset CreatedAt, DateTimeOffset ReservationExpiresAt, int PaymentAttempts, DateTimeOffset? PaymentStartedAt,
    DateTimeOffset? PaidAt, DateTimeOffset? FulfilledAt, DateTimeOffset? ClosedAt, string? CloseReason, decimal? RefundAmount, Guid? PaymentId,
    string IdempotencyRequestHash, IReadOnlyList<ProductOrderLineView> Lines);

public sealed record NewOrderLine(Guid ProductId, int Quantity);

public sealed record NewProductOrder(
    Guid OrderId, string TokenHash, string KeyHash, string RequestHash, Guid LocationId, string Source, string GuestName, string GuestPhone,
    IReadOnlyList<NewOrderLine> Lines, decimal? ExpectedTotal);

public sealed record CreateLimits(
    int ReservationMinutes, decimal MaxOrderTotal, int MaxPendingPerPhone, int MaxCreatesPerPhonePerHour, int MaxCreatesPerLocationPerHour);

public enum CreateStatus { Created, Replay, LocationNotFound, ProductUnavailable, TotalTooLarge, PriceChanged, LimitReached, InsufficientStock }

public sealed record CreateOrderOutcome(
    CreateStatus Status, ProductOrderView? Order = null, IReadOnlyList<Guid>? ProductIds = null, decimal? Total = null, string? LimitScope = null);

public enum ClaimKind { Claimed, Reconcile, NoClaim }

/// <param name="PaymentId">Claimed - новий платіж; Reconcile - останній платіж замовлення (або новий, якщо жодного pending/paid немає).</param>
/// <param name="PaymentStatus">Reconcile: стан останнього платежу (pending | paid); новий платіж = pending.</param>
public sealed record ClaimOutcome(ClaimKind Kind, ProductOrderView? Order, Guid PaymentId = default, string? PaymentStatus = null);

public enum FinalizeKind { Paid, AlreadyPaid, NotPending, StockUnavailable }

public sealed record FinalizeOutcome(FinalizeKind Kind, ProductOrderView? Order);

/// <summary>
/// Порт даних замовлень товарів. Усе виконується під RLS tenant-а. Порядок блокувань (без циклів, §33.16): advisory tenant -> рівні (створення);
/// замовлення -> рівні -> товари FOR SHARE (фіналізація оплати, повернення). Час передається ззовні (TimeProvider сервісу).
/// </summary>
public interface IProductOrderStore
{
    Task<OrderLocationRow?> GetActiveLocationAsync(Guid locationId, CancellationToken ct);
    Task<CatalogData?> ListCatalogAsync(Guid locationId, DateTimeOffset now, CancellationToken ct);
    Task<ProductOrderView?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);
    Task<ProductOrderView?> FindByIdempotencyKeyAsync(string keyHash, CancellationToken ct);
    Task<ProductOrderView?> GetOrderAsync(Guid orderId, CancellationToken ct);
    Task<CreateOrderOutcome> CreateAsync(NewProductOrder order, CreateLimits limits, DateTimeOffset now, CancellationToken ct);

    /// <summary>Крок 1 оплати (§33.16): умовний UPDATE-claim + платіж pending; завислу оплату (старшу за stale) перехоплює для reconcile.</summary>
    Task<ClaimOutcome> ClaimPaymentAsync(Guid orderId, DateTimeOffset now, TimeSpan stale, int maxAttempts, CancellationToken ct);
    /// <summary>Відмова картки: платіж failed, payment_started_at = NULL (спроба витрачена).</summary>
    Task MarkPaymentFailedAsync(Guid orderId, Guid paymentId, CancellationToken ct);
    /// <summary>Платіж paid -> фіналізація: FEFO-списання (без прострочених), операція sale, рухи sale, клієнт, статус paid - одна транзакція.</summary>
    Task<FinalizeOutcome> FinalizePaidAsync(Guid orderId, Guid paymentId, string? providerPaymentId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Після успішного повернення провайдером: замовлення refunded (stock_unavailable, 100%), платіж refunded.</summary>
    Task CompensateAsync(Guid orderId, Guid paymentId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Умовне скасування неоплаченого (§33.16): true - скасовано цим викликом. <paramref name="by"/>: client | staff.</summary>
    Task<bool> CancelUnpaidAsync(Guid orderId, string by, Guid? userId, string? note, DateTimeOffset now, CancellationToken ct);
}
