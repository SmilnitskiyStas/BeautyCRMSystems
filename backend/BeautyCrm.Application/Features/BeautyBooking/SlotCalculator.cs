using System.Text.Json;

namespace BeautyCrm.Application.Features.BeautyBooking;

/// <summary>
/// Unavailable = майстер у день відсутності або послуга йому не призначена (409 specialist_unavailable);
/// LocationClosed = день закритий для закладу (вихідний тиждня / дата закриття, 409 location_closed; §17).
/// OutsideLocationHours = запис не вміщується в години роботи закладу (409 outside_location_hours; §18.1).
/// </summary>
public enum SlotCheck { Ok, InPast, OutsideWorkingHours, Busy, Unavailable, LocationClosed, OutsideLocationHours }

public static class SlotCalculator
{
    public const int StepMinutes = 15;
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>Ключ дня тижня в графіку й закритих днях закладу (mon..sun).</summary>
    public static string DayKey(DayOfWeek day) => DayKeys[(int)day];

    public static IReadOnlyList<(TimeOnly From, TimeOnly To)> IntervalsFor(string? workingHoursJson, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(workingHoursJson)) return [];
        using var doc = JsonDocument.Parse(workingHoursJson);
        if (!doc.RootElement.TryGetProperty(DayKeys[(int)day], out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray()
            .Select(i => (TimeOnly.Parse(i.GetProperty("from").GetString()!), TimeOnly.Parse(i.GetProperty("to").GetString()!)))
            .ToList();
    }

    /// <summary>
    /// Інтервали на кожен день тижня за одне розбирання JSON (індекс = <see cref="DayOfWeek"/>). Некоректний або порожній
    /// JSON дає порожні дні. Спільний помічник для обчислення доступних хвилин (аналітика, §19.4).
    /// </summary>
    public static IReadOnlyList<(TimeOnly From, TimeOnly To)>[] WeeklyIntervals(string? workingHoursJson)
    {
        var week = new IReadOnlyList<(TimeOnly From, TimeOnly To)>[7];
        for (var d = 0; d < 7; d++)
        {
            try { week[d] = IntervalsFor(workingHoursJson, (DayOfWeek)d); }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            { week[d] = []; }
        }
        return week;
    }

    /// <summary>
    /// Сумарна довжина (хвилини настінного часу) попарних перетинів інтервалів <paramref name="a"/> і <paramref name="b"/>.
    /// <paramref name="b"/> = null — обмеження немає (повертається сума довжин <paramref name="a"/>).
    /// </summary>
    public static int OverlapMinutes(IReadOnlyList<(TimeOnly From, TimeOnly To)> a, IReadOnlyList<(TimeOnly From, TimeOnly To)>? b)
    {
        var total = 0;
        foreach (var x in a)
        {
            if (b is null)
            {
                if (x.To > x.From) total += (int)(x.To - x.From).TotalMinutes;
                continue;
            }
            foreach (var y in b)
            {
                var from = x.From > y.From ? x.From : y.From;
                var to = x.To < y.To ? x.To : y.To;
                if (to > from) total += (int)(to - from).TotalMinutes;
            }
        }
        return total;
    }

    /// <summary>Чи є в графіку бодай один робочий інтервал (майстер без графіка не бронюється).</summary>
    public static bool HasWorkingHours(string? workingHoursJson)
    {
        try
        {
            for (var d = 0; d < 7; d++)
                if (IntervalsFor(workingHoursJson, (DayOfWeek)d).Count > 0) return true;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Чи вміщується [from, from+duration] в один із інтервалів дня (години закладу, §18.1). Запис через північ не вміщується.
    /// </summary>
    public static bool FitsHours(string? hoursJson, DayOfWeek day, TimeOnly from, int durationMinutes)
    {
        var to = from.AddMinutes(durationMinutes);
        return to > from && IntervalsFor(hoursJson, day).Any(i => from >= i.From && to <= i.To);
    }

    public static IReadOnlyList<FreeSlot> Compute(
        SpecialistSchedule schedule, DateOnly date, int durationMinutes, IReadOnlyList<TimeRange> busy, DateTimeOffset now,
        Guid? serviceId = null)
    {
        if (schedule.IsClosedOn(date) || !schedule.Offers(serviceId) || schedule.IsAbsentOn(date)) return [];
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        var result = new List<FreeSlot>();
        foreach (var (from, to) in IntervalsFor(schedule.WorkingHoursJson, date.DayOfWeek))
        {
            for (var t = from; t.AddMinutes(durationMinutes) <= to && t.AddMinutes(durationMinutes) > t; t = t.AddMinutes(StepMinutes))
            {
                // §18.1: перетин із годинами закладу (null = без обмеження); сітка слотів лишається прив'язаною до графіка майстра
                if (schedule.HasLocationHours && !FitsHours(schedule.LocationHoursJson, date.DayOfWeek, t, durationMinutes)) continue;
                var start = ToOffset(date, t, tz);
                var end = start.AddMinutes(durationMinutes);
                if (start < now || Overlaps(busy, start, end)) continue;
                result.Add(new FreeSlot(schedule.SpecialistId, start, end, t.ToString("HH:mm")));
            }
        }
        return result;
    }

    public static SlotCheck Check(
        SpecialistSchedule schedule, DateTimeOffset start, int durationMinutes, IReadOnlyList<TimeRange> busy, DateTimeOffset now,
        Guid? serviceId = null)
    {
        if (start < now) return SlotCheck.InPast;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        var local = TimeZoneInfo.ConvertTime(start, tz);
        var localDate = DateOnly.FromDateTime(local.DateTime);
        if (schedule.IsClosedOn(localDate)) return SlotCheck.LocationClosed;
        if (!schedule.Offers(serviceId) || schedule.IsAbsentOn(localDate)) return SlotCheck.Unavailable;
        var from = TimeOnly.FromDateTime(local.DateTime);
        if (schedule.HasLocationHours && !FitsHours(schedule.LocationHoursJson, local.DayOfWeek, from, durationMinutes))
            return SlotCheck.OutsideLocationHours;
        if (!FitsHours(schedule.WorkingHoursJson, local.DayOfWeek, from, durationMinutes)) return SlotCheck.OutsideWorkingHours;
        return Overlaps(busy, start, start.AddMinutes(durationMinutes)) ? SlotCheck.Busy : SlotCheck.Ok;
    }

    /// <summary>Напіввідкриті інтервали [start, end): впритул — не перетин.</summary>
    public static bool Overlaps(IEnumerable<TimeRange> busy, DateTimeOffset start, DateTimeOffset end) =>
        busy.Any(b => start < b.End && b.Start < end);

    public static DateTimeOffset ToOffset(DateOnly date, TimeOnly time, TimeZoneInfo tz)
    {
        var dt = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(dt)) dt = dt.AddHours(1);
        return new DateTimeOffset(dt, tz.GetUtcOffset(dt));
    }

    public static DateOnly LocalDate(DateTimeOffset instant, string timezone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime);
}
