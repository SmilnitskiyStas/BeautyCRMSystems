using System.Globalization;
using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// Шаблони T-flow-* (§28.12, TASK-838): тексти кроків потоку запису кнопками формує лише КОД з даних БД, без LLM. Назви з БД проходять
/// ту саму санітизацію, що й підписи кнопок (<see cref="ChatKeyboardLayout.SanitizeLabel"/>): жодних керівних символів і зайвих пробілів.
/// </summary>
public static class FlowTemplates
{
    public const string NoServices = "Зараз немає послуг для онлайн-запису. Можу передати ваше питання адміністратору.";
    public const string NoDays = "Найближчим часом вільних днів немає.";
    public const string Expired = "Запис перервано через тривалу паузу. Почнемо знову?";
    public const string Cancelled = "Добре, запис не створюю. Записатися — /book.";
    public const string Unavailable = "Цей варіант уже недоступний.";
    public const string OtherTime = "Добре, цей час не бронюю. Ось наступний вільний час:";
    public const string StartAgain = "📅 Почати знову";

    private static readonly string[] Weekdays = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    private static string N(string? s) => ChatKeyboardLayout.SanitizeLabel(s, 80);

    private static string Place(string name, string? address) =>
        string.IsNullOrWhiteSpace(address) ? $"«{N(name)}»" : $"«{N(name)}», {N(address)}";

    /// <summary>Рядки «Обрано: …» на початку повідомлення наступного кроку (автопропуск, §28.3).</summary>
    public static string ChosenLines(IEnumerable<FlowChosen> chosen) =>
        string.Join("\n", chosen.Select(c => c.Kind == "specialist"
            ? $"Обрано: спеціаліст {N(c.Name)}"
            : $"Обрано: заклад {Place(c.Name, c.Address)}"));

    public static string NoSlots(string? specialist) =>
        $"У {(string.IsNullOrWhiteSpace(specialist) ? "спеціаліста" : N(specialist))} немає вільного часу найближчими днями.";

    /// <summary>Текст кроку: примітка, рядки «Обрано» і заголовок за кроком (без перелічення слотів — їх показують кнопки).</summary>
    public static string StepText(FlowView v)
    {
        var parts = new List<string>();
        if (v.Notice == FlowNotices.Unavailable) parts.Add(Unavailable);
        else if (v.Notice == FlowNotices.OtherTime) parts.Add(OtherTime);
        if (v.Chosen.Count > 0) parts.Add(ChosenLines(v.Chosen));
        var n = v.Names;
        string head = v.Step switch
        {
            FlowSteps.Service => "Оберіть послугу:",
            FlowSteps.Specialist => $"Оберіть спеціаліста для «{N(n.Service)}»:",
            FlowSteps.LastLocation => $"Записати вас у {Place(n.Location ?? "", n.Address)}, як минулого разу?",
            FlowSteps.Location => LocationHead(v),
            FlowSteps.Day => v.NoDays ? NoDays : "Оберіть день:",
            _ => v.NoSlots ? NoSlots(n.Specialist) : $"Вільний час, {N(n.Specialist)}, {N(n.Location)}:",
        };
        parts.Add(head);
        return string.Join("\n", parts);
    }

    private static string LocationHead(FlowView v)
    {
        var head = $"Оберіть заклад, де {N(v.Names.Specialist)} виконує «{N(v.Names.Service)}»:";
        var lines = v.Items.Where(i => i.Verb == FlowVerbs.Location)
            .Select(i => i.Label + (i.Address is { Length: > 0 } a ? " — " + N(a) : ""));
        return head + "\n" + string.Join("\n", lines.Select(l => "• " + l));
    }

    public static string NavLabel(string nav, bool moreIsRestart) => nav switch
    {
        FlowNav.More => moreIsRestart ? "↩ На початок" : "➕ Ще",
        FlowNav.Back => "⬅ Назад",
        FlowNav.Cancel => "✖ Скасувати",
        FlowNav.OtherLocation => "Інший заклад",
        FlowNav.OtherDay => "📆 Інший день",
        FlowNav.OtherSpecialist => "👤 Інший спеціаліст",
        _ => "",
    };

    /// <summary>«14:30» (сьогодні) або «пт 14:30» у зоні закладу.</summary>
    public static string SlotLabel(DateTimeOffset at, string? timezone, DateTimeOffset now)
    {
        var tz = Zone(timezone);
        var local = TimeZoneInfo.ConvertTime(at, tz);
        var today = TimeZoneInfo.ConvertTime(now, tz).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? time : $"{Weekdays[(int)local.DayOfWeek]} {time}";
    }

    /// <summary>«пт 08.11» для дати.</summary>
    public static string DayLabel(DateOnly date) =>
        $"{Weekdays[(int)date.DayOfWeek]} {date.ToString("dd.MM", CultureInfo.InvariantCulture)}";

    private static TimeZoneInfo Zone(string? id)
    {
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
