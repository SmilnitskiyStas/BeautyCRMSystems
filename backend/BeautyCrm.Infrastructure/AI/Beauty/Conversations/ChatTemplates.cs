using System.Globalization;
using System.Text;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyReminders;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// Шаблони відповідей коду (§21.8): без LLM і без токенів, лише українською (V1). T-summary формує <see cref="ChatSummaryText"/>.
/// Тексти правиться копірайтером без зміни контракту; дані запису в T-confirmed беруться з БД, а не з тексту моделі.
/// </summary>
public static class ChatTemplates
{
    public const string Greeting =
        "Вітаю! Я віртуальний асистент закладу. Можу розповісти про послуги та ціни, підібрати вільний час і записати вас на візит. " +
        "Напишіть, що вас цікавить.";

    /// <summary>T-unsupported / T-command (інша команда).</summary>
    public const string TextOnly = "Я поки розумію лише текстові повідомлення. Надішліть, будь ласка, текстом.";

    public const string Handoff = "Передаю ваше питання адміністратору, вам відповідять найближчим часом.";

    /// <summary>T-policy-check (§22.9): замість відповіді моделі, що суперечить фактам закладу або виходить за них (<see cref="PolicyClaimGuard"/>).</summary>
    public const string PolicyCheck = "Щодо цього я уточню в адміністратора — він відповість вам тут.";

    public const string AiUnavailable =
        "Зараз я не можу відповісти автоматично. Передаю ваше питання адміністратору, вам відповідять найближчим часом.";

    public const string RateLimited = "Ви надсилаєте повідомлення дуже часто. Зачекайте, будь ласка, хвилину й напишіть ще раз.";

    public const string Fallback = "Вибачте, не вдалося сформувати відповідь. Спробуйте, будь ласка, переформулювати запитання.";

    /// <summary>Модель двічі не дала тексту (порожньо/лише службові дані): нейтральне прохання уточнити замість «не вдалося» (TASK-735).</summary>
    public const string Clarify =
        "Уточніть, будь ласка, що саме вас цікавить: яка послуга, на який день і в який час вам зручно записатися?";

    public const string Declined = "Добре, запис не створено. Якщо захочете обрати інший час чи послугу, напишіть.";

    public const string Expired =
        "Пропозиція втратила чинність, запис не створено. Напишіть, будь ласка, коли вам зручно, і я підберу час знову.";

    public const string SlotTaken =
        "На жаль, цей час уже недоступний, запис не створено. Напишіть, будь ласка, коли вам зручно, і я запропоную інші варіанти.";

    public const string PriceChanged =
        "Ціна змінилася, тому запис не створено. Напишіть, будь ласка, і я підготую новий підсумок із актуальною ціною.";

    public const string Limit = "Досягнуто ліміт записів через чат. Передаю ваше питання адміністратору, він допоможе.";

    public const string AlreadyConfirmed = "Ваш запис уже підтверджено, повторно створювати його не потрібно.";

    public const string InProgress = "Ваш запис оформлюється. Зачекайте, будь ласка, хвилину.";

    /// <summary>Повторне «Так» на запис, який уже скасовано (L7).</summary>
    public const string AppointmentCancelled =
        "Цей запис уже скасовано. Якщо хочете записатися знову, напишіть, і я підберу час.";

    /// <summary>Нейтральна відповідь без LLM, коли ШІ тимчасово не може відповісти (ліміти нових розмов, повторні відкладення). Без handoff.</summary>
    public const string Busy =
        "Зараз у нас багато звернень, тому я не можу відповісти одразу. Напишіть, будь ласка, ще раз за кілька хвилин.";

    /// <summary>Підсумок запису остаточно не доставлено: пропозиція втратила чинність, «Так» нічого не створить (M1).</summary>
    public const string SummaryNotDelivered =
        "Не вдалося показати підсумок запису, тому запис не створено. Напишіть, будь ласка, коли вам зручно, і я підготую підсумок знову.";

    /// <summary>Початок T-confirmed: за ним репліка шукається в БД, щоб відрізнити «підтвердження ще не надіслано» від повторного «Так».</summary>
    public const string ConfirmedHeader = "Готово, ви записані!";

    private static readonly string[] Weekdays = ["нд", "пн", "вт", "ср", "чт", "пт", "сб"];

    // ---- нагадування й команди (§22.7, TASK-740); без markdown ----

    public const string ReminderInvalid = "Не зрозумів вибір. Відповідайте цифрами 1–4, наприклад «2 3».";

    public const string ReminderCallUnavailable =
        "Дзвінок-нагадування недоступний: ми не маємо вашого номера, а в чаті його не запитуємо. Можу нагадати тут, у Telegram: 1, 2 або 3.";

    public const string Stop = "Добре, більше не надсилатимемо нагадувань. Увімкнути знову — напишіть «нагадування».";

    public const string ReminderNoAppointments =
        "Зараз у вас немає майбутніх записів. Після запису я запропоную обрати нагадування. Щоб не отримувати нагадувань, напишіть «Стоп».";

    public const string ReminderAppointmentInactive = "Цей запис уже скасовано або завершено, тому нагадування для нього не налаштовуємо.";

    private const string ChangeHint = "Змінити — напишіть «нагадування».";

    /// <summary>
    /// T-reminder-offer: меню з упорядкованого списку варіантів (нумерація 1-4); пункт «зателефонувати» - лише якщо він у списку.
    /// <paramref name="buttons"/> - під повідомленням є кнопки (TASK-753): перший рядок згадує кнопки, цифри лишаються запасним способом.
    /// </summary>
    public static string ReminderOffer(IReadOnlyList<ReminderChoiceOption> options, bool buttons = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(buttons
            ? "Нагадати вам про візит? Оберіть кнопкою нижче або відповідайте цифрою:"
            : "Нагадати вам про візит? Відповідайте цифрою:");
        foreach (var o in options.Where(o => o.Choice.Method != ReminderMethods.Call))
            sb.AppendLine($"{o.Number} — {o.Label}");
        sb.Append("Можна кілька, наприклад «2 3».");
        if (options.Any(o => o.Choice.Method == ReminderMethods.Call))
            sb.Append("\nАбо напишіть «зателефонувати» — адміністратор зателефонує напередодні.");
        return sb.ToString();
    }

    /// <summary>T-reminder-set.</summary>
    public static string ReminderSet(ReminderChoice choice) => choice.Method switch
    {
        ReminderMethods.Call => $"Адміністратор зателефонує вам напередодні. {ChangeHint}",
        ReminderMethods.None => $"Добре, нагадувань не буде. {ChangeHint}",
        _ => $"Готово: нагадаємо {OffsetsText(choice.Offsets)} у Telegram. {ChangeHint}",
    };

    /// <summary>Рядок про нагадування в T-confirmed, коли вподобання вже є (план запису з БД).</summary>
    public static string ReminderLine(AppointmentRemindersDto? r)
    {
        var offsets = r?.OffsetsMinutes.Select(o => (short)o).ToArray() ?? [];
        return r?.Method switch
        {
            ReminderMethods.Call => $"Адміністратор зателефонує вам напередодні. {ChangeHint}",
            null or ReminderMethods.None => $"Нагадувань про візит не буде. {ChangeHint}",
            _ when offsets.Length == 0 => $"Нагадувань про візит не буде. {ChangeHint}",
            _ => $"Нагадаємо {OffsetsText(offsets)} у Telegram. {ChangeHint}",
        };
    }

    /// <summary>«за 24 год і за 2 год»: від найвіддаленішої точки.</summary>
    public static string OffsetsText(IReadOnlyList<short> offsets)
    {
        var parts = offsets.Distinct().OrderByDescending(o => o).Select(o => o switch
        {
            1440 => "за 24 год", 120 => "за 2 год", 60 => "за 1 год", _ => $"за {o} хв",
        }).ToList();
        return parts.Count <= 1 ? string.Concat(parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " і " + parts[^1];
    }

    /// <summary>«пт, 31.10 о 14:30» у зоні закладу (як у нагадуваннях воркера).</summary>
    public static string LocalWhen(DateTimeOffset at, string? timezone)
    {
        var local = ToLocal(at, timezone);
        return $"{Weekdays[(int)local.DayOfWeek]}, {local.ToString("dd.MM", CultureInfo.InvariantCulture)} о {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Меню для команди «нагадування»: заголовок про найближчий візит + T-reminder-offer.</summary>
    public static string ReminderOfferFor(string serviceName, DateTimeOffset startsAt, string? timezone, IReadOnlyList<ReminderChoiceOption> options,
        bool buttons = false)
    {
        var name = AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(serviceName)).Trim();
        return $"Найближчий візит: {name}, {LocalWhen(startsAt, timezone)}.\n{ReminderOffer(options, buttons)}";
    }

    // ---- кнопки й команди (TASK-753, §23.8); без markdown ----

    public const string OtherTime = "Добре, цей час не бронюю. Напишіть, коли вам зручно.";
    public const string MyEmpty = "Майбутніх записів у цьому чаті немає. Записатися — /book або напишіть, що бажаєте.";
    public const string MyListHeader = "Ваші найближчі записи:";
    public const string CancelUnavailable =
        "Цей запис уже не можна скасувати в чаті: його скасовано, завершено або він уже почався. Якщо потрібна допомога — /help.";
    public const string CancelLimit = "Сьогодні вже було кілька скасувань. Передаю ваше питання адміністратору, він відповість тут.";
    public const string RescheduleHandoff = "Перенесення поки робить адміністратор. Передаю ваше питання, він відповість тут.";
    public const string ActionStale = "Ця кнопка вже неактуальна. Напишіть, що бажаєте, або надішліть /my, щоб побачити свої записи.";
    public const string ActionHandoff = "Ваше питання вже передано адміністратору, він відповість тут. Поки що ця дія недоступна.";
    public const string AppointmentInactive = "Цей запис уже скасовано або завершено.";
    public const string BookStart = "Напишіть, яку послугу й на коли бажаєте, наприклад: «манікюр у п'ятницю після 15:00».";
    public const string SlotsLine = "Оберіть час кнопкою нижче або напишіть свій.";
    public const string SlotsIntro = "Ось вільний час.";
    public const string Help =
        "Я допоможу записатися, переглянути чи скасувати запис.\n/book — записатися\n/my — мої записи\n/cancel — скасувати запис\n" +
        "/stop — не надсилати нагадування\nАбо просто напишіть питання.";

    /// <summary>T-my-list: нумерований перелік найближчих записів (дані з БД, назви знешкоджено).</summary>
    public static string MyList(IReadOnlyList<(DateTimeOffset StartsAt, string Timezone, string Service, string Specialist, string Location)> items)
    {
        var sb = new StringBuilder(MyListHeader);
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            sb.Append($"\n{i + 1}. {LocalWhen(it.StartsAt, it.Timezone)} — {Clean(it.Service)}, {Clean(it.Specialist)}, {Clean(it.Location)}");
        }
        return sb.ToString();
    }

    /// <summary>T-cancel-confirm: екран підтвердження зі рядком політики (текст політики формує <c>ClientChatAppointments</c> за <c>CancellationPolicy</c>).</summary>
    public static string CancelConfirm(string service, string specialist, string location, DateTimeOffset startsAt, string timezone, string policyText) =>
        $"Скасувати запис?\n{Clean(service)}, майстер {Clean(specialist)}\n{LocalWhen(startsAt, timezone)}, {Clean(location)}\n{policyText}";

    public static string Cancelled(DateTimeOffset startsAt, string timezone) =>
        $"Запис на {LocalWhen(startsAt, timezone)} скасовано. Нагадувань про нього не буде. Записатися знову — /book.";

    public static string CancelKept(DateTimeOffset startsAt, string timezone) => $"Добре, запис залишається: {LocalWhen(startsAt, timezone)}.";

    public static string AttendOk(DateTimeOffset startsAt, string timezone) => $"Дякуємо, чекаємо вас {LocalWhen(startsAt, timezone)}!";

    /// <summary>«пт 31.10 14:30» для підпису кнопки.</summary>
    public static string ShortWhen(DateTimeOffset at, string? timezone)
    {
        var local = ToLocal(at, timezone);
        return $"{Weekdays[(int)local.DayOfWeek]} {local.ToString("dd.MM", CultureInfo.InvariantCulture)} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static string Clean(string? v) => AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(v)).Trim();

    /// <summary>T-confirmed: дані запису з БД (назви й час - з рядка запису, час у зоні закладу).</summary>
    /// <param name="buttons">Під повідомленням є кнопки (TASK-753): останній рядок вказує на кнопку/команду замість «напишіть сюди».</param>
    public static string Confirmed(AppointmentDto a, string currencyLabel, bool buttons = false)
    {
        string C(string? v) => AiTextNormalizer.NeutralizeTags(AiTextNormalizer.Clean(v)).Trim();
        var inv = CultureInfo.InvariantCulture;
        var local = ToLocal(a.StartsAt, a.Timezone);
        var price = a.PriceFinal < a.PriceOriginal || a.PromotionId is not null
            ? $"{Money(a.PriceOriginal)} → {Money(a.PriceFinal)} {currencyLabel}"
            : $"{Money(a.PriceFinal)} {currencyLabel}";
        var sb = new StringBuilder();
        sb.AppendLine(ConfirmedHeader);
        sb.AppendLine($"Послуга: {C(a.ServiceName)}");
        sb.AppendLine($"Майстер: {C(a.SpecialistName)}");
        sb.AppendLine($"Заклад: {C(a.LocationName)}");
        sb.AppendLine($"Дата й час: {Weekdays[(int)local.DayOfWeek]}, {local.ToString("dd.MM", inv)}, {local.ToString("HH:mm", inv)}" +
                      (string.IsNullOrWhiteSpace(a.Timezone) ? "" : $", {C(a.Timezone)}"));
        sb.AppendLine($"Тривалість: {a.DurationMinutes} хв");
        sb.AppendLine($"Ціна: {price}");
        sb.AppendLine("Оплата в закладі.");
        sb.Append(buttons
            ? "Переглянути чи скасувати запис можна командою /my. Щоб змінити час, напишіть сюди — адміністратор допоможе."
            : "Щоб скасувати чи змінити запис, напишіть сюди, адміністратор допоможе.");
        return sb.ToString();
    }

    private static DateTime ToLocal(DateTimeOffset at, string? timezone)
    {
        try
        {
            return string.IsNullOrWhiteSpace(timezone) ? at.UtcDateTime : TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return at.UtcDateTime;
        }
    }

    private static string Money(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Детермінована перевірка відповіді клієнта на підсумок запису (§21.6 крок (в)). LLM не бере участі.</summary>
public static class ChatConfirmationText
{
    private static readonly HashSet<string> Yes = new(StringComparer.Ordinal) { "так", "так підтверджую", "підтверджую", "yes", "да" };
    private static readonly HashSet<string> No = new(StringComparer.Ordinal) { "ні", "нет", "no", "не треба", "скасувати" };

    /// <summary>lowercase, без пунктуації, символів, емодзі й повторних пробілів. Цифри й літери зберігаються («так, але о 15:00» не стає «так»).</summary>
    public static string Normalize(string? text)
    {
        var s = AiTextNormalizer.Clean(text).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        var space = true;
        foreach (var rune in s.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune)) { sb.Append(rune.ToString()); space = false; }
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format) continue;
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    public static bool IsYes(string? text) => Yes.Contains(Normalize(text));
    public static bool IsNo(string? text) => No.Contains(Normalize(text));
}
