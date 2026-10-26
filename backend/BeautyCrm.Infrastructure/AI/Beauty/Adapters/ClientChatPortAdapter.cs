using BeautyCrm.Application.Features.BeautyChatBooking;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

/// <summary>
/// Порт scope ClientChat поверх публічних сервісів (ті самі правила слотів, цін, акцій і маскування, що й у публічному записі).
/// Залежності беруться ліниво з scope: збірки без публічного запису (тести DI, воркер) не ламаються на старті.
/// Кожен метод звіряє tenant зі scope (<see cref="TenantGuard"/>): розбіжність = <see cref="TenantMismatchException"/>.
/// Токен підтвердження пропозиції тут відкидається - далі за адаптер він не виходить.
/// </summary>
public sealed class ClientChatPortAdapter(IServiceProvider services, TenantContext tenant) : IClientChatPort
{
    // Коди, які дозволено показувати моделі; усе інше -> нейтральний код.
    private static readonly HashSet<string> CatalogCodes = ["location_not_found", "service_not_found", "specialist_not_found", "invalid_date"];
    private static readonly HashSet<string> PrepareCodes =
        ["location_not_found", "service_not_found", "specialist_not_found", "invalid_start", "slot_not_offered", "price_not_set"];

    private PublicBookingService Pub => services.GetRequiredService<PublicBookingService>();
    private ChatBookingService Chat => services.GetRequiredService<ChatBookingService>();

    public async Task<IReadOnlyList<ChatLocationDto>> ListLocationsAsync(Guid tenantId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        return (await Pub.ListLocationsAsync(ct))
            .Select(l => new ChatLocationDto(l.Id, l.Name, l.Address, l.Timezone, l.WorkingHours, l.ClosedWeekdays ?? [])).ToList();
    }

    public async Task<ChatPortResult<IReadOnlyList<ChatServiceDto>>> ListServicesAsync(Guid tenantId, Guid locationId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var r = await Pub.ListServicesAsync(locationId, null, ct);
        if (!r.IsOk) return ChatPortResult<IReadOnlyList<ChatServiceDto>>.Fail(Map(r.Error!.Code, CatalogCodes, "catalog_unavailable"));
        IReadOnlyList<ChatServiceDto> list = r.Value!
            .Select(s => new ChatServiceDto(s.Id, s.Name, s.Category, s.DurationMinutes, s.PriceOriginal, s.PriceFinal, s.PromotionName)).ToList();
        return ChatPortResult<IReadOnlyList<ChatServiceDto>>.Ok(list);
    }

    public async Task<ChatPortResult<IReadOnlyList<ChatSpecialistDto>>> ListSpecialistsAsync(
        Guid tenantId, Guid locationId, Guid? serviceId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var r = await Pub.ListSpecialistsAsync(locationId, serviceId, ct);
        if (!r.IsOk) return ChatPortResult<IReadOnlyList<ChatSpecialistDto>>.Fail(Map(r.Error!.Code, CatalogCodes, "catalog_unavailable"));
        IReadOnlyList<ChatSpecialistDto> list = r.Value!.Select(s => new ChatSpecialistDto(s.Id, s.Name, s.Title)).ToList(); // без PhotoUrl
        return ChatPortResult<IReadOnlyList<ChatSpecialistDto>>.Ok(list);
    }

    public async Task<ChatPortResult<ChatSlotsDto>> FindSlotsAsync(
        Guid tenantId, Guid locationId, Guid serviceId, Guid? specialistId, DateOnly date, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var location = (await Pub.ListLocationsAsync(ct)).FirstOrDefault(l => l.Id == locationId);
        if (location is null) return ChatPortResult<ChatSlotsDto>.Fail("location_not_found");

        var r = await Pub.GetSlotsAsync(locationId, specialistId, serviceId, date, ct);
        if (!r.IsOk) return ChatPortResult<ChatSlotsDto>.Fail(Map(r.Error!.Code, CatalogCodes, "slots_unavailable"));

        var tz = TimeZoneInfo.FindSystemTimeZoneById(location.Timezone);
        var slots = r.Value!.OrderBy(s => s.StartsAt)
            .Select(s => new ChatSlotDto(s.SpecialistId, TimeZoneInfo.ConvertTime(s.StartsAt, tz))).ToList();
        return ChatPortResult<ChatSlotsDto>.Ok(new ChatSlotsDto(location.Timezone, slots));
    }

    public async Task<ChatPortResult<ChatPreparedDto>> PrepareBookingAsync(
        Guid tenantId, Guid conversationId, Guid locationId, Guid serviceId, Guid specialistId, DateTimeOffset startsAt, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);
        var r = await Chat.PrepareAsync(new ChatPrepareRequest(conversationId, locationId, serviceId, specialistId, startsAt), ct);
        if (!r.IsOk) return ChatPortResult<ChatPreparedDto>.Fail(Map(r.Error!.Code, PrepareCodes, "prepare_failed"));

        var p = r.Value!;
        var s = p.Summary;
        // p.ConfirmationToken свідомо не читається.
        return ChatPortResult<ChatPreparedDto>.Ok(new ChatPreparedDto(p.ProposalId,
            new ChatBookingSummaryDto(s.ServiceName, s.SpecialistName, s.LocationName, s.Address, s.LocalDate, s.LocalTime, s.Timezone,
                s.DurationMinutes, s.PriceOriginal, s.PriceFinal, s.PromotionName), p.ExpiresAt));
    }

    private static string Map(string code, HashSet<string> allowed, string fallback) => allowed.Contains(code) ? code : fallback;
}
