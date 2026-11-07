namespace BeautyCrm.Application.Features.BeautyChatFlow;

// Детермінований запис кнопками в чаті (TASK-838, ADR-020, контракти §28). Application-шар: стани, порти, опції кроків.
// Нічого тут не залежить від ШІ: кроки формуються з БД, а клавіатури/повідомлення будує Infrastructure.

/// <summary>Налаштування потоку, <c>ChatFlow:*</c> (§28.2). <see cref="Enabled"/> за замовч. false: поведінка ADR-011/013 без змін.</summary>
public sealed class ChatFlowOptions
{
    public bool Enabled { get; init; }
    public bool OfferLastLocation { get; init; } = true;
    public int IdleTimeoutMinutes { get; init; } = 30;
    public int PageSize { get; init; } = 5;
    public int DayPageSize { get; init; } = 6;
    public int MaxScanDays { get; init; } = 14;
    public bool ResendStepAfterLlm { get; init; } = true;
    public bool LlmPrepareWhenFlow { get; init; }
    public bool GeoEnabled { get; init; }
    public int TransientLocationTtlSeconds { get; init; } = 300;
    public int FinishedRetentionDays { get; init; } = 30;
    /// <summary>Розмір сторінки кроку <c>time</c> (<c>ChatButtons:SlotButtons</c>, 6).</summary>
    public int SlotButtons { get; init; } = 6;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(IdleTimeoutMinutes);
}

public static class FlowSteps
{
    public const string Service = "service";
    public const string Specialist = "specialist";
    public const string LastLocation = "last_location";
    public const string Location = "location";
    public const string Time = "time";
    public const string Day = "day";
    public const string Confirm = "confirm";
}

public static class FlowStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
}

/// <summary>Навігаційні дії <c>fn</c> (§28.5): закритий перелік.</summary>
public static class FlowNav
{
    public const string OtherLocation = "other_location";
    public const string More = "more";
    public const string Back = "back";
    public const string OtherDay = "other_day";
    public const string OtherSpecialist = "other_specialist";
    public const string Cancel = "cancel";

    public static readonly IReadOnlyList<string> All = [OtherLocation, More, Back, OtherDay, OtherSpecialist, Cancel];

    /// <summary>Чи дозволена навігація для кроку (§28.5 «Валідація при створенні»).</summary>
    public static bool AllowedFor(string step, string nav) => nav switch
    {
        OtherLocation => step == FlowSteps.LastLocation,
        OtherDay or OtherSpecialist => step == FlowSteps.Time,
        More => step is FlowSteps.Service or FlowSteps.Specialist or FlowSteps.Location or FlowSteps.Time or FlowSteps.Day,
        Back => step is not (FlowSteps.Service or FlowSteps.Confirm),
        Cancel => step != FlowSteps.Confirm,
        _ => false,
    };
}

public static class FlowCancelReasons
{
    public const string Client = "client";
    public const string Declined = "declined";
    public const string Handoff = "handoff";
    public const string NoServices = "no_services";
}

public static class FlowOrigins
{
    public const string Button = "button";
    public const string Command = "command";
    public const string Llm = "llm";
    public const string Text = "text";
}

/// <summary>Знімок рядка <c>beauty_chat_booking_flows</c> (без ПД і координат).</summary>
public sealed record FlowState(
    Guid Id, Guid ConversationId, string Status, string Step, int Version,
    Guid? ServiceId, Guid? SpecialistId, Guid? LocationId, Guid? SuggestedLastLocationId,
    DateOnly? Day, TimeOnly? PreferredTime, DateTimeOffset? CursorAt, string? ListCursor,
    IReadOnlyList<string> SkippedSteps, Guid? ProposalId, string Origin, DateTimeOffset ExpiresAt, string? CancelReason = null)
{
    public bool Skipped(string step) => SkippedSteps.Contains(step);
    public bool IsActive => Status == FlowStatuses.Active;
}

/// <summary>Контекст розмови для кроків: клієнт (історія), заклад каналу (фіксує заклад, §28.3 п. 3).</summary>
public sealed record FlowContext(Guid ConversationId, Guid? ClientId, Guid? ChannelLocationId);

// ---- порт каталогу (tenant-wide, §28.4) ----

public sealed record FlowServiceItem(Guid Id, string Name, string? Category);
public sealed record FlowSpecialistItem(Guid Id, string Name);
/// <param name="Latitude">Координати ЗАКЛАДУ (їх вводить адміністратор, §28.10), не клієнта. null - не задано.</param>
public sealed record FlowLocationItem(Guid Id, string Name, string? Address, string Timezone, double? Latitude = null, double? Longitude = null)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}

/// <summary>Назви вибраного для заголовків кроку й рядка «Обрано» (null - вибору ще немає або запис зник).</summary>
public sealed record FlowNames(string? Service, string? Specialist, string? Location, string? Address, string? Timezone);

/// <summary>
/// Опції кроків із БД під RLS tenant каналу (§28.4, без LLM). Придатність майстра та сама, що в публічному каталозі
/// (<c>EligibleSpecialistIdsAsync</c>), але узагальнена на всі активні заклади tenant; потрібна ціна послуги в закладі (override або мережева).
/// Списки повертаються повністю у стабільному порядку (ключі сортування закінчуються на <c>id</c>); курсорну пагінацію робить код потоку.
/// </summary>
public interface IChatFlowCatalog
{
    /// <summary>Активні послуги, які виконує ≥ 1 придатний майстер у ≥ 1 активному закладі з ціною. Фільтри працюють в обидва боки (рішення 7).</summary>
    Task<IReadOnlyList<FlowServiceItem>> ListBookableServicesAsync(Guid? specialistId, Guid? locationId, CancellationToken ct);

    /// <summary>Придатні майстри з призначенням на послугу хоча б в одному активному закладі з ціною.</summary>
    Task<IReadOnlyList<FlowSpecialistItem>> ListSpecialistsForServiceAsync(Guid serviceId, Guid? locationId, CancellationToken ct);

    /// <summary>Активні заклади, де майстер придатний, призначений на послугу, і для послуги є ціна.</summary>
    Task<IReadOnlyList<FlowLocationItem>> ListLocationsForAsync(Guid serviceId, Guid specialistId, CancellationToken ct);

    /// <summary>Заклад найпізнішого за <c>starts_at</c> нескасованого (pending|confirmed|completed) запису клієнта; null - записів немає.</summary>
    Task<Guid?> GetLastAppointmentLocationAsync(Guid clientId, CancellationToken ct);

    Task<FlowNames> GetNamesAsync(Guid? serviceId, Guid? specialistId, Guid? locationId, CancellationToken ct);
}

// ---- порт сховища потоку ----

public sealed record NewFlowRequest(FlowState State);

/// <summary>
/// Сховище стану потоку (RLS tenant). <see cref="TryAdvanceAsync"/> - CAS за версією: у транзакції ходу разом з CAS клавіатури (§28.6).
/// Усі методи йдуть через <c>BeautyDbContext</c> тієї ж області, тож діють у поточній транзакції контексту.
/// </summary>
public interface IChatFlowStore
{
    Task<FlowState?> GetActiveAsync(Guid conversationId, CancellationToken ct);
    Task<FlowState?> GetAsync(Guid flowId, Guid conversationId, CancellationToken ct);

    /// <summary>Попередній <c>active</c> потік розмови -> <c>superseded</c>, новий рядок (вже активний, або одразу завершений для no_services). Повертає id витіснених потоків.</summary>
    Task<IReadOnlyList<Guid>> CreateAsync(FlowState state, DateTimeOffset now, CancellationToken ct);

    /// <summary>CAS: <c>UPDATE … WHERE id = @id AND version = @before.Version AND status = 'active'</c>; <paramref name="after"/>.Version = before + 1. false - програно.</summary>
    Task<bool> TryAdvanceAsync(FlowState before, FlowState after, CancellationToken ct);

    /// <summary>Завершує активний потік (completed|cancelled|expired|superseded) лише якщо він ще active. true - перевів.</summary>
    Task<bool> FinishAsync(Guid flowId, string status, string? cancelReason, DateTimeOffset now, CancellationToken ct);

    /// <summary>Sweeper: <c>active</c> з <c>expires_at ≤ now</c> -> expired. Повертає id.</summary>
    Task<IReadOnlyList<Guid>> ExpireDueAsync(DateTimeOffset now, int batch, CancellationToken ct);

    /// <summary>Retention: видаляє завершені потоки, старші за <paramref name="before"/> (пакет). Повертає кількість.</summary>
    Task<int> DeleteFinishedBeforeAsync(DateTimeOffset before, int batch, CancellationToken ct);
}
