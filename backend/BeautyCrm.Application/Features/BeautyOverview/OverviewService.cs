using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyOverview;

/// <summary>Заклад для адмінки (GET /locations). Address може бути null.</summary>
public sealed record LocationDto(Guid Id, string Name, string? Address, string Timezone, bool IsActive);

/// <summary>
/// KPI дня. AppointmentsCount — усі записи дня, крім скасованих; Revenue — сума price_final завершених (як в аналітиці);
/// NewClients — клієнти, створені в цю локальну добу (за часовою зоною еталонного закладу); FreeSlots — кількість вільних
/// слотів, що не перетинаються, найкоротшої активної послуги майстра в робочих інтервалах (минулі й зайняті виключено).
/// </summary>
public sealed record OverviewKpi(int AppointmentsCount, decimal Revenue, int NewClients, int FreeSlots);

/// <param name="Timezone">Часова зона еталонного закладу (вибраного або першого за назвою) — по ній визначено NewClients і за замовчуванням date.</param>
public sealed record OverviewDto(DateOnly Date, string Timezone, IReadOnlyList<AppointmentDto> Appointments, OverviewKpi Kpi);

/// <summary>Порт даних огляду й довідника закладів. Реалізація — Infrastructure (EF Core під RLS tenant).</summary>
public interface IOverviewStore
{
    Task<IReadOnlyList<LocationDto>> ListLocationsAsync(CancellationToken ct);
    /// <summary>Тривалості активних послуг (id -> хвилини).</summary>
    Task<IReadOnlyDictionary<Guid, int>> GetActiveServiceDurationsAsync(CancellationToken ct);
    /// <summary>Клієнти, створені в [fromUtc, toUtc) (не видалені).</summary>
    Task<int> CountNewClientsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

public sealed class OverviewService(IOverviewStore overview, IBookingStore booking, CancellationSettingsService cancellation, TimeProvider clock)
{
    public async Task<IReadOnlyList<LocationDto>> ListLocationsAsync(CancellationToken ct) =>
        await overview.ListLocationsAsync(ct);

    /// <summary>Записи дня (локальна доба кожного закладу) + KPI. Лише для керівників (виручка).</summary>
    public async Task<Result<OverviewDto>> GetAsync(DateOnly? date, Guid? locationId, CancellationToken ct)
    {
        var all = (await overview.ListLocationsAsync(ct)).Where(l => l.IsActive).OrderBy(l => l.Name).ThenBy(l => l.Id).ToList();
        if (locationId is { } wanted && all.All(l => l.Id != wanted))
            return Error.NotFound("location_not_found", "Location not found.");
        var locations = locationId is { } id ? all.Where(l => l.Id == id).ToList() : all;
        var now = clock.GetUtcNow();
        if (locations.Count == 0)
        {
            var today = date ?? DateOnly.FromDateTime(now.UtcDateTime);
            return new OverviewDto(today, "UTC", [], new OverviewKpi(0, 0m, 0, 0));
        }

        var reference = TimeZoneInfo.FindSystemTimeZoneById(locations[0].Timezone);
        var day = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, reference).DateTime);
        var terms = await cancellation.GetTermsAsync(ct);
        var durations = await overview.GetActiveServiceDurationsAsync(ct);

        var appointments = new List<AppointmentDto>();
        var freeSlots = 0;
        foreach (var location in locations)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(location.Timezone);
            var from = SlotCalculator.ToOffset(day, TimeOnly.MinValue, tz);
            var to = SlotCalculator.ToOffset(day.AddDays(1), TimeOnly.MinValue, tz);
            appointments.AddRange((await booking.ListAppointmentsAsync(from, to, location.Id, null, ct))
                .Select(a => a with { Cancellation = terms }));

            var schedules = await booking.GetSchedulesAsync(location.Id, null, ct);
            if (schedules.Count == 0) continue;
            var busy = await booking.GetBusyAsync(schedules.Select(s => s.SpecialistId).ToList(), from, to, null, ct);
            foreach (var schedule in schedules)
            {
                var minutes = MinDuration(schedule, durations);
                if (minutes is null) continue;
                var slots = SlotCalculator.Compute(schedule with { Timezone = location.Timezone }, day, minutes.Value,
                    busy.Where(b => b.SpecialistId == schedule.SpecialistId).Select(b => b.Range).ToList(), now);
                freeSlots += NonOverlapping(slots);
            }
        }

        appointments = appointments.OrderBy(a => a.StartsAt).ThenBy(a => a.Id).ToList();
        var active = appointments.Where(a => a.Status != "cancelled").ToList();
        var refFrom = SlotCalculator.ToOffset(day, TimeOnly.MinValue, reference);
        var refTo = SlotCalculator.ToOffset(day.AddDays(1), TimeOnly.MinValue, reference);
        var kpi = new OverviewKpi(
            active.Count,
            appointments.Where(a => a.Status == "completed").Sum(a => a.PriceFinal),
            await overview.CountNewClientsAsync(refFrom, refTo, ct),
            freeSlots);
        return new OverviewDto(day, locations[0].Timezone, appointments, kpi);
    }

    /// <summary>Найкоротша активна послуга, яку пропонує майстер (ServiceIds = null — усі активні).</summary>
    private static int? MinDuration(SpecialistSchedule schedule, IReadOnlyDictionary<Guid, int> durations)
    {
        var candidates = schedule.ServiceIds is null
            ? durations.Values
            : schedule.ServiceIds.Where(durations.ContainsKey).Select(s => durations[s]);
        var min = candidates.Where(d => d > 0).DefaultIfEmpty(0).Min();
        return min > 0 ? min : null;
    }

    /// <summary>Жадібно: слоти впритул один до одного, що не перетинаються (старти на сітці 15 хв).</summary>
    private static int NonOverlapping(IReadOnlyList<FreeSlot> slots)
    {
        var count = 0;
        var lastEnd = DateTimeOffset.MinValue;
        foreach (var s in slots.OrderBy(s => s.StartsAt))
        {
            if (s.StartsAt < lastEnd) continue;
            count++;
            lastEnd = s.EndsAt;
        }
        return count;
    }
}
