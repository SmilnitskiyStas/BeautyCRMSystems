using System.Text.Json;

namespace BeautyCrm.Application.Features.BeautyBooking;

public enum SlotCheck { Ok, InPast, OutsideWorkingHours, Busy }

public static class SlotCalculator
{
    public const int StepMinutes = 15;
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    public static IReadOnlyList<(TimeOnly From, TimeOnly To)> IntervalsFor(string? workingHoursJson, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(workingHoursJson)) return [];
        using var doc = JsonDocument.Parse(workingHoursJson);
        if (!doc.RootElement.TryGetProperty(DayKeys[(int)day], out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray()
            .Select(i => (TimeOnly.Parse(i.GetProperty("from").GetString()!), TimeOnly.Parse(i.GetProperty("to").GetString()!)))
            .ToList();
    }

    public static IReadOnlyList<FreeSlot> Compute(
        SpecialistSchedule schedule, DateOnly date, int durationMinutes, IReadOnlyList<TimeRange> busy, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        var result = new List<FreeSlot>();
        foreach (var (from, to) in IntervalsFor(schedule.WorkingHoursJson, date.DayOfWeek))
        {
            for (var t = from; t.AddMinutes(durationMinutes) <= to && t.AddMinutes(durationMinutes) > t; t = t.AddMinutes(StepMinutes))
            {
                var start = ToOffset(date, t, tz);
                var end = start.AddMinutes(durationMinutes);
                if (start < now || Overlaps(busy, start, end)) continue;
                result.Add(new FreeSlot(schedule.SpecialistId, start, end, t.ToString("HH:mm")));
            }
        }
        return result;
    }

    public static SlotCheck Check(
        SpecialistSchedule schedule, DateTimeOffset start, int durationMinutes, IReadOnlyList<TimeRange> busy, DateTimeOffset now)
    {
        if (start < now) return SlotCheck.InPast;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.Timezone);
        var local = TimeZoneInfo.ConvertTime(start, tz);
        var from = TimeOnly.FromDateTime(local.DateTime);
        var to = from.AddMinutes(durationMinutes);
        var fits = to > from && IntervalsFor(schedule.WorkingHoursJson, local.DayOfWeek).Any(i => from >= i.From && to <= i.To);
        if (!fits) return SlotCheck.OutsideWorkingHours;
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
