using BeautyCrm.Application.Features.BeautyReminders;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>Вибір нагадування: <see cref="ReminderMethods"/> none | telegram | call і нормалізовані точки (унікальні, за зростанням).</summary>
public sealed record ReminderChoice(string Method, IReadOnlyList<short> Offsets)
{
    public static readonly ReminderChoice None = new(ReminderMethods.None, []);
    public static readonly ReminderChoice Call = new(ReminderMethods.Call, [1440]);
}

/// <summary>Пункт меню вибору (порядок = нумерація T-reminder-offer; підписи використає TASK-753 для кнопок).</summary>
public sealed record ReminderChoiceOption(int Number, string Label, ReminderChoice Choice);

/// <summary>Меню вибору нагадування (§22.7, ADR-012 §2.5): 1 - за 1 год, 2 - за 2 год, 3 - за 24 год, 4 - не нагадувати; «зателефонувати» - окремий пункт.</summary>
public static class ReminderChoiceMenu
{
    public const string PromptKind = "reminder_choice";

    public static IReadOnlyList<ReminderChoiceOption> Options(bool callAvailable)
    {
        var list = new List<ReminderChoiceOption>
        {
            new(1, "за 1 годину", new ReminderChoice(ReminderMethods.Telegram, [60])),
            new(2, "за 2 години", new ReminderChoice(ReminderMethods.Telegram, [120])),
            new(3, "за 24 години", new ReminderChoice(ReminderMethods.Telegram, [1440])),
            new(4, "не нагадувати", ReminderChoice.None),
        };
        if (callAvailable) list.Add(new ReminderChoiceOption(5, "зателефонувати", ReminderChoice.Call));
        return list;
    }
}

public enum ReminderParseKind { NotAChoice, Choice, Call, Invalid }

public sealed record ReminderParseResult(ReminderParseKind Kind, ReminderChoice? Choice = null)
{
    public static readonly ReminderParseResult NotAChoice = new(ReminderParseKind.NotAChoice);
    public static readonly ReminderParseResult Invalid = new(ReminderParseKind.Invalid);
    public static readonly ReminderParseResult Call = new(ReminderParseKind.Call, ReminderChoice.Call);
}

/// <summary>
/// Детермінований розбір відповіді на меню нагадувань (§22.7 крок 4a). Без LLM. Нормалізація - <see cref="ChatConfirmationText.Normalize"/>
/// (роздільники «,», «+», «і», «та» стають пробілами/службовими словами). Правило: слово, якого немає в словнику, робить текст
/// НЕ відповіддю (<see cref="ReminderParseKind.NotAChoice"/>, питання знімається, хід іде далі); лише цифри/одиниці з помилкою - <see cref="ReminderParseKind.Invalid"/>.
/// </summary>
public static class ReminderChoiceParser
{
    private static readonly HashSet<string> NonePhrases = new(StringComparer.Ordinal)
        { "4", "не треба", "не нагадувати", "ні", "не потрібно", "нічого", "нагадувань не треба", "не нагадуйте" };
    private static readonly HashSet<string> CallPhrases = new(StringComparer.Ordinal)
        { "зателефонувати", "дзвінок", "подзвоніть", "зателефонуйте" };
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal) { "і", "та", "й", "за", "через" };
    private static readonly HashSet<string> Units = new(StringComparer.Ordinal) { "год", "годин", "години", "годину" };
    private const int MaxTokens = 12;

    public static ReminderParseResult Parse(string? text)
    {
        var s = ChatConfirmationText.Normalize(text);
        if (s.Length == 0) return ReminderParseResult.NotAChoice;
        if (NonePhrases.Contains(s)) return new ReminderParseResult(ReminderParseKind.Choice, ReminderChoice.None);
        if (CallPhrases.Contains(s)) return ReminderParseResult.Call;

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > MaxTokens) return ReminderParseResult.NotAChoice;

        var offsets = new SortedSet<short>();
        var invalid = false;
        var any = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            if (Connectors.Contains(tok)) continue;
            if (IsDigits(tok))
            {
                any = true;
                var hasUnit = i + 1 < tokens.Length && Units.Contains(tokens[i + 1]);
                if (hasUnit) i++;
                var value = tok.Length > 3 ? -1 : int.Parse(tok);
                short? minutes = hasUnit
                    ? value switch { 1 => 60, 2 => 120, 24 => 1440, _ => null }   // «3 год» не існує
                    : value switch { 1 => 60, 2 => 120, 3 => 1440, 24 => 1440, _ => null }; // «4» - окремо (none)
                if (minutes is { } m) offsets.Add(m); else invalid = true;
                continue;
            }
            switch (tok)
            {
                case "годину": any = true; offsets.Add(60); break;
                case "добу" or "день": any = true; offsets.Add(1440); break;
                case "зателефонувати" or "дзвінок" or "подзвоніть" or "зателефонуйте": any = true; invalid = true; break; // дзвінок разом з іншим
                default: return ReminderParseResult.NotAChoice; // невідоме слово: це не відповідь на меню
            }
        }
        if (!any) return ReminderParseResult.NotAChoice;
        // «4» разом з іншими, «5», «3 год» - питання лишається.
        if (invalid || offsets.Count == 0) return ReminderParseResult.Invalid;
        return new ReminderParseResult(ReminderParseKind.Choice, new ReminderChoice(ReminderMethods.Telegram, offsets.ToArray()));
    }

    private static bool IsDigits(string t) => t.Length > 0 && t.All(c => c is >= '0' and <= '9');
}

/// <summary>Команди нагадувань (§22.7): точний збіг після нормалізації. «Стоп» - у будь-який момент; «нагадування» - показ меню.</summary>
public static class ReminderCommands
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "стоп", "stop" };
    private static readonly HashSet<string> ShowWords = new(StringComparer.Ordinal) { "нагадування", "нагадати", "змінити нагадування" };

    /// <param name="promptActive">Є чинне питання: тоді «не нагадувати» - це пункт меню (none для запису), а не «Стоп».</param>
    public static bool IsStop(string? text, bool promptActive)
    {
        if (text is not null && text.TrimStart().StartsWith('/')) return false; // slash-команди (/stop) - TASK-753
        var s = ChatConfirmationText.Normalize(text);
        return StopWords.Contains(s) || (!promptActive && s == "не нагадувати");
    }

    public static bool IsShowMenu(string? text) => ShowWords.Contains(ChatConfirmationText.Normalize(text));
}
