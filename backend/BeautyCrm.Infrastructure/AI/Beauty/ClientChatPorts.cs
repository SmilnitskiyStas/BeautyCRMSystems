using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

// Порти scope ClientChat (TASK-727, §21.7). Лише публічні поля каталогу: без клієнтів, нотаток, алергій, телефонів і ПД персоналу.
// Реалізація - Adapters/ClientChatPortAdapter (поверх PublicBookingService / ChatBookingService); у тестах - fake.

public sealed record ChatLocationDto(
    Guid Id, string Name, string? Address, string Timezone, JsonElement? WorkingHours, IReadOnlyList<string> ClosedWeekdays);

public sealed record ChatServiceDto(
    Guid Id, string Name, string? Category, int DurationMinutes, decimal PriceOriginal, decimal PriceFinal, string? PromotionName);

public sealed record ChatSpecialistDto(Guid Id, string Name, string? Title);

public sealed record ChatSlotDto(Guid SpecialistId, DateTimeOffset StartsAt);

public sealed record ChatSlotsDto(string Timezone, IReadOnlyList<ChatSlotDto> Slots);

/// <summary>Дані для канонічного підсумку. Без id і токена підтвердження.</summary>
public sealed record ChatBookingSummaryDto(
    string ServiceName, string SpecialistName, string LocationName, string? Address, DateOnly LocalDate, TimeOnly LocalTime,
    string Timezone, int DurationMinutes, decimal PriceOriginal, decimal PriceFinal, string? PromotionName,
    string? CouponOutcome = null); // TASK-782 (§26.9.3): applied | not_better | not_applicable; купона не було - null

/// <summary>
/// Результат етапу 1. Токен підтвердження НЕ входить у DTO: адаптер його відкидає, шар ШІ його не бачить
/// (пропозицію етапу 2 конвеєр знаходить за розмовою).
/// </summary>
public sealed record ChatPreparedDto(Guid ProposalId, ChatBookingSummaryDto Summary, DateTimeOffset ExpiresAt);

/// <summary>Успіх або код помилки (без тексту й внутрішніх деталей). Код - один із відомих, адаптер не віддає невідомих.</summary>
public sealed record ChatPortResult<T>(T? Value, string? ErrorCode)
{
    public bool IsOk => ErrorCode is null;
    public static ChatPortResult<T> Ok(T value) => new(value, null);
    public static ChatPortResult<T> Fail(string code) => new(default, code);
}

public interface IClientChatPort
{
    Task<IReadOnlyList<ChatLocationDto>> ListLocationsAsync(Guid tenantId, CancellationToken ct);
    Task<ChatPortResult<IReadOnlyList<ChatServiceDto>>> ListServicesAsync(Guid tenantId, Guid locationId, CancellationToken ct);
    Task<ChatPortResult<IReadOnlyList<ChatSpecialistDto>>> ListSpecialistsAsync(Guid tenantId, Guid locationId, Guid? serviceId, CancellationToken ct);
    /// <summary>Ті самі слоти й правила (сітка, горизонт, маскування), що й у публічному записі; час - з offset зони закладу.</summary>
    Task<ChatPortResult<ChatSlotsDto>> FindSlotsAsync(
        Guid tenantId, Guid locationId, Guid serviceId, Guid? specialistId, DateOnly date, CancellationToken ct);
    /// <summary>Етап 1 запису (<c>ChatBookingService.PrepareAsync</c>). Запису не створює. conversationId - з контексту ходу.</summary>
    Task<ChatPortResult<ChatPreparedDto>> PrepareBookingAsync(
        Guid tenantId, Guid conversationId, Guid locationId, Guid serviceId, Guid specialistId, DateTimeOffset startsAt, CancellationToken ct);
}
