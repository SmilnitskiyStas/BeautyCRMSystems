using System.Text.Json.Serialization;

namespace BeautyCrm.Application.Features.BeautyAnalytics;

// ---- відповіді датного режиму (§19). Legacy-записи (NetworkAnalytics/LocationAnalytics/PromotionAnalytics) не змінюються. ----

/// <summary>
/// Мережа за локальні дні [FromDate..ToDate] (§19.1). From/To — еталонні миттєвості (to виключно). Previous — лише при compare=true.
/// </summary>
public sealed record NetworkReport(
    string FromDate, string ToDate, string Timezone, DateTimeOffset From, DateTimeOffset To,
    int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue, decimal AverageCheck, decimal CancellationRate,
    int NewClients,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NetworkPrevious? Previous = null);

public sealed record NetworkPrevious(
    string FromDate, string ToDate, int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue,
    decimal AverageCheck, decimal CancellationRate, int NewClients);

/// <summary>Load = busy / available (0..1, 4 знаки) або null, якщо доступних хвилин немає (§19.4).</summary>
public sealed record LocationReport(
    Guid LocationId, string Name, int Appointments, int Completed, int Cancelled, decimal Revenue,
    int NoShow, string Timezone, bool IsActive, int BusyMinutes, int AvailableMinutes, decimal? Load,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LocationPrevious? Previous = null);

public sealed record LocationPrevious(
    int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue, int BusyMinutes, int AvailableMinutes, decimal? Load);

public sealed record PromotionReport(
    Guid PromotionId, string Name, int Uses, decimal DiscountTotal, decimal Revenue,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PromotionPrevious? Previous = null);

public sealed record PromotionPrevious(int Uses, decimal DiscountTotal, decimal Revenue);

public sealed record SeriesPoint(string From, string To, int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue);

/// <summary>Granularity: day | week | month (§19.2).</summary>
public sealed record SeriesReport(string FromDate, string ToDate, string Timezone, string Granularity, IReadOnlyList<SeriesPoint> Points);

public sealed record BreakdownSpecialist(
    Guid SpecialistId, string Name, bool IsActive, int Appointments, int Completed, int Cancelled, int NoShow, decimal Revenue,
    int BusyMinutes, int AvailableMinutes, decimal? Load);

public sealed record BreakdownService(Guid ServiceId, string Name, int Completed, decimal Revenue);

public sealed record BreakdownReport(
    Guid LocationId, string Name, string Timezone, string FromDate, string ToDate, int BusyMinutes, int AvailableMinutes, decimal? Load,
    IReadOnlyList<BreakdownSpecialist> Specialists, IReadOnlyList<BreakdownService> Services);

// ---- рядки порту даних (Infrastructure -> Application) ----

public sealed record AnalyticsLocation(
    Guid Id, string Name, string Timezone, bool IsActive, IReadOnlyList<string> ClosedWeekdays, string? WorkingHoursJson);

/// <summary>Зв'язок майстер-заклад з графіком (активність зв'язку й профілю потрібна для майбутніх днів, §19.4).</summary>
public sealed record AnalyticsLink(
    Guid SpecialistId, string SpecialistName, bool SpecialistActive, Guid LocationId, bool LinkActive, string? WorkingHoursJson);

/// <summary>Діапазон повних локальних дат: закриття закладу (OwnerId = заклад) або затверджена відсутність (OwnerId = майстер).</summary>
public sealed record AnalyticsSpan(Guid OwnerId, DateOnly From, DateOnly To);

/// <summary>Агрегат записів: заклад × локальна дата (зона закладу) × статус. Revenue — Σ price_final лише для completed.</summary>
public sealed record DailyRow(Guid LocationId, DateOnly Date, string Status, int Count, decimal Revenue);

/// <summary>Σ тривалості записів pending/confirmed/completed майстра в закладі.</summary>
public sealed record BusyRow(Guid LocationId, Guid SpecialistId, int Minutes);

public sealed record SpecialistStatRow(Guid SpecialistId, string Name, bool IsActive, string Status, int Count, decimal Revenue);
public sealed record ServiceStatRow(Guid ServiceId, string Name, int Completed, decimal Revenue);
public sealed record PromotionStatRow(Guid PromotionId, string Name, int Uses, decimal Discount, decimal Revenue);
