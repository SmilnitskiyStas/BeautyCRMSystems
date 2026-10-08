using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BeautyCrm.Application.Features.BeautyStaff;

/// <summary>
/// Валідація й нормалізація графіка (формат §9): {"mon":[{"from":"09:00","to":"18:00"}], ...}.
/// У БД потрапляє лише перевірений і перевиданий JSON, а не довільне тіло клієнта.
/// </summary>
public static class WorkingHoursValidator
{
    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    private const int MaxIntervalsPerDay = 8;

    /// <summary>null — формат хибний. Порожній об'єкт допустимий (немає робочих днів).</summary>
    public static string? TryNormalize(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } root) return null;
        var byDay = new Dictionary<string, List<(TimeOnly From, TimeOnly To)>>();
        foreach (var prop in root.EnumerateObject())
        {
            if (Array.IndexOf(Days, prop.Name) < 0 || byDay.ContainsKey(prop.Name)) return null;
            if (prop.Value.ValueKind != JsonValueKind.Array) return null;
            var list = new List<(TimeOnly From, TimeOnly To)>();
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (list.Count >= MaxIntervalsPerDay || item.ValueKind != JsonValueKind.Object) return null;
                string? from = null, to = null;
                foreach (var f in item.EnumerateObject())
                {
                    if (f.Value.ValueKind != JsonValueKind.String) return null;
                    switch (f.Name)
                    {
                        case "from" when from is null: from = f.Value.GetString(); break;
                        case "to" when to is null: to = f.Value.GetString(); break;
                        default: return null;
                    }
                }
                if (!TryTime(from, out var a) || !TryTime(to, out var b) || a >= b) return null;
                list.Add((a, b));
            }
            list.Sort((x, y) => x.From.CompareTo(y.From));
            for (var i = 1; i < list.Count; i++)
                if (list[i].From < list[i - 1].To) return null; // перекриття
            byDay[prop.Name] = list;
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var day in Days.Where(d => byDay.TryGetValue(d, out var l) && l.Count > 0))
            {
                w.WriteStartArray(day);
                foreach (var (from, to) in byDay[day])
                {
                    w.WriteStartObject();
                    w.WriteString("from", from.ToString("HH:mm", CultureInfo.InvariantCulture));
                    w.WriteString("to", to.ToString("HH:mm", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool TryTime(string? s, out TimeOnly t)
    {
        t = default;
        return s is not null && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out t);
    }
}
