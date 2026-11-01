using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>Кнопка плану: підпис, verb і (для станових verb з індексом) позиція опції в <see cref="KeyboardPlan.Options"/>.</summary>
public sealed record PlannedButton(string Label, string Verb, int? Option = null);

/// <summary>
/// План клавіатури (TASK-753, §23.8): що показати, до створення токена. Токен з'являється після створення рядка
/// `beauty_chat_keyboards` (<see cref="ChatKeyboardService.CreateAsync"/>); тоді <see cref="Render"/> дає розкладку для `actions`.
/// <see cref="Kind"/> = null - лише безстанові кнопки (`i:*`), рядка в БД немає (вони дійсні завжди, як команда).
/// </summary>
public sealed record KeyboardPlan(
    string? Kind, IReadOnlyList<KeyboardOption> Options, IReadOnlyList<IReadOnlyList<PlannedButton>> Rows,
    Guid? ProposalId = null, Guid? AppointmentId = null, DateTimeOffset? ExpiresAt = null, int MaxPerRow = 3)
{
    public bool IsStateless => Kind is null;

    /// <summary>Розкладка з `callback_data` (для станових - з токеном створеної клавіатури).</summary>
    public IReadOnlyList<IReadOnlyList<ActionButton>> Render(CreatedKeyboard? keyboard) =>
        Rows.Select(row => (IReadOnlyList<ActionButton>)row.Select(b => new ActionButton(b.Label, DataOf(b, keyboard))).ToList()).ToList();

    private string DataOf(PlannedButton b, CreatedKeyboard? kb) =>
        ButtonVerbs.Stateless.Contains(b.Verb)
            ? CallbackAddress.Build(b.Verb, null)
            : kb!.Data(b.Verb, ButtonVerbs.NeedsIndex(Kind ?? "", b.Verb) ? b.Option : null);

    /// <summary>Розкладка валідна за §23.8 (≤ 10 кнопок, ≤ MaxPerRow у рядку, ≤ 6 рядків, підпис ≤ 32, callback_data ≤ 64 байти)? Перевірка на пробному токені.</summary>
    public bool IsValid()
    {
        if (Rows.Count == 0) return false;
        if (!IsStateless && (Options.Count is 0 or > ChatKeyboardLayout.MaxButtons)) return false;
        var probe = new CreatedKeyboard(Guid.Empty, new string('A', 22), DateTimeOffset.MaxValue, []);
        foreach (var b in Rows.SelectMany(r => r))
        {
            if (!IsStateless && !ButtonVerbs.Stateless.Contains(b.Verb))
            {
                if (!ButtonVerbs.AllowedFor(Kind!, b.Verb)) return false;
                if (ButtonVerbs.NeedsIndex(Kind!, b.Verb) && (b.Option is not { } i || i < 0 || i >= Options.Count || i > 9)) return false;
            }
            else if (IsStateless && !ButtonVerbs.Stateless.Contains(b.Verb)) return false;
        }
        return ChatKeyboardLayout.Validate(Render(probe), MaxPerRow) is null;
    }
}

/// <summary>Підписи безстанових кнопок: і для кнопок, і для тіла вхідного callback в inbox (`"[кнопка] …"`).</summary>
public static class StatelessLabels
{
    public const string Book = "📅 Записатися";
    public const string My = "🗓 Мої записи";
    public const string Human = "👤 Адміністратор";
    public const string HumanLong = "👤 Передати адміністратору";
    public const string Phone = "📞 Змінити спосіб";

    /// <summary>Підпис натиснутої безстанової кнопки без емодзі (inbox: «Клієнт натиснув: Записатися»).</summary>
    public static string BodyLabel(string verb) => verb switch
    {
        ButtonVerbs.Book => "Записатися",
        ButtonVerbs.My => "Мої записи",
        ButtonVerbs.Help => "Допомога",
        ButtonVerbs.Human => "Передати адміністратору",
        ButtonVerbs.Stop => "Не нагадувати",
        ButtonVerbs.Phone => "Змінити спосіб",
        ButtonVerbs.MarketingMenu => "Новини й акції",
        ButtonVerbs.MarketingOff => "Не надсилати акції",
        _ => "",
    };
}

/// <summary>
/// Конструктор клавіатур (TASK-753, §23.6, §23.8, ADR-013 §2.5): розкладки формує лише КОД зі структурованих даних (підсумок, слоти з
/// find_slots, меню нагадувань, записи клієнта). Модель не бачить токенів і не має tool для кнопок. Чиста логіка без доступу до БД:
/// створення рядка в БД і токен - <see cref="ChatKeyboardService"/>.
/// </summary>
public sealed class ChatKeyboardBuilder(ChatButtonsOptions opts)
{
    private const int MaxListAppointments = 4; // 4 × (скасувати + змінити час) + «Записатися» = 9 кнопок (≤ 10)

    /// <summary>[✅ Так, записатися] [Ні] / [🕐 Інший час].</summary>
    public KeyboardPlan BookingConfirm(Guid proposalId) => new(
        KeyboardKinds.BookingConfirm,
        [new(ButtonVerbs.ConfirmBooking, "✅ Так, записатися"), new(ButtonVerbs.DeclineBooking, "Ні"), new(ButtonVerbs.OtherTime, "🕐 Інший час")],
        [
            [new("✅ Так, записатися", ButtonVerbs.ConfirmBooking), new("Ні", ButtonVerbs.DeclineBooking)],
            [new("🕐 Інший час", ButtonVerbs.OtherTime)],
        ],
        ProposalId: proposalId);

    /// <summary>
    /// `slot_pick` із ≤ SlotButtons слотів (§23.6): з бажаним часом - найближчі до нього, інакше перші за часом. Підпис «14:30» (сьогодні) або «пт 14:30»;
    /// кілька майстрів - «14:30 · Олена», по 2 у рядку (інакше по 3). Null - слотів немає.
    /// </summary>
    public KeyboardPlan? SlotPick(ChatOfferedSlots? offered, IReadOnlyDictionary<Guid, string> specialistNames, DateTimeOffset now)
    {
        if (offered?.Slots is not { Count: > 0 } all || offered.LocationId is not { } loc || offered.ServiceId is not { } svc) return null;
        var n = Math.Clamp(opts.SlotButtons, 1, 8);
        static int Minutes(DateTimeOffset t) => t.Hour * 60 + t.Minute;
        var picked = (offered.PreferredTime is { } pt
                ? all.OrderBy(s => Math.Abs(Minutes(s.StartsAt) - (pt.Hour * 60 + pt.Minute))).ThenBy(s => s.StartsAt).ThenBy(s => s.SpecialistId).Take(n)
                : all.OrderBy(s => s.StartsAt).ThenBy(s => s.SpecialistId).Take(n))
            .OrderBy(s => s.StartsAt).ThenBy(s => s.SpecialistId).ToList();
        var named = picked.Select(s => s.SpecialistId).Distinct().Count() > 1;
        var today = ToLocalDate(now, offered.Timezone);

        var options = new List<KeyboardOption>();
        var buttons = new List<PlannedButton>();
        foreach (var s in picked)
        {
            var label = ToLocalDate(s.StartsAt, offered.Timezone) == today
                ? s.StartsAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                : ChatTemplates.ShortWhen(s.StartsAt, offered.Timezone).Split(' ', 2)[0] + " " + s.StartsAt.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            if (named && specialistNames.TryGetValue(s.SpecialistId, out var name))
            {
                var room = ChatKeyboardLayout.MaxLabel - label.Length - 3;
                var shortName = room >= 3 ? ChatKeyboardLayout.SanitizeLabel(name, room) : "";
                if (shortName.Length > 0) label = $"{label} · {shortName}";
            }
            buttons.Add(new PlannedButton(label, ButtonVerbs.SlotPick, options.Count));
            options.Add(new KeyboardOption(ButtonVerbs.SlotPick, label, loc, svc, s.SpecialistId, s.StartsAt));
        }
        var perRow = named ? 2 : 3;
        var rows = buttons.Chunk(perRow).Select(c => (IReadOnlyList<PlannedButton>)c.ToList()).ToList();
        return new KeyboardPlan(KeyboardKinds.SlotPick, options, rows, MaxPerRow: perRow);
    }

    /// <summary>[За 1 год] [За 2 год] [За 24 год] / [🔕 Не нагадувати] / [📞 Зателефонувати] (лише з телефоном і call_enabled: його вже немає в <paramref name="menu"/>).</summary>
    public KeyboardPlan ReminderChoice(Guid appointmentId, IReadOnlyList<ReminderChoiceOption> menu, DateTimeOffset expiresAt, CallOffer? offer = null)
    {
        string Label(ReminderChoiceOption o) => o.Number switch
        {
            1 => "За 1 год", 2 => "За 2 год", 3 => "За 24 год", 4 => "🔕 Не нагадувати", _ => "📞 Зателефонувати",
        };
        var options = menu.Select(o => new KeyboardOption(ButtonVerbs.ReminderChoice, Label(o), Method: o.Choice.Method, Offsets: o.Choice.Offsets.ToList())).ToList();
        var rows = new List<IReadOnlyList<PlannedButton>>();
        var first = menu.Select((o, i) => (o, i)).Where(x => x.o.Number <= 3).Select(x => new PlannedButton(Label(x.o), ButtonVerbs.ReminderChoice, x.i)).ToList();
        if (first.Count > 0) rows.Add(first);
        foreach (var (o, i) in menu.Select((o, i) => (o, i)).Where(x => x.o.Number > 3))
            rows.Add([new PlannedButton(Label(o), ButtonVerbs.ReminderChoice, i)]);
        // §25.5.1: пауза після відмови - пункту «Зателефонувати» немає, є явний обхід (безстанова i:phone, не споживає rc).
        if (offer == CallOffer.Cooldown) rows.Add([new PlannedButton(StatelessLabels.Phone, ButtonVerbs.Phone)]);
        return new KeyboardPlan(KeyboardKinds.ReminderChoice, options, rows, AppointmentId: appointmentId, ExpiresAt: expiresAt);
    }

    /// <summary>Рядок на запис: [❌ Скасувати пт 31.10 14:30] [🕐 Змінити час]; останній рядок [📅 Записатися].</summary>
    public KeyboardPlan AppointmentList(IReadOnlyList<ChatAppointmentInfo> appointments)
    {
        var options = new List<KeyboardOption>();
        var rows = new List<IReadOnlyList<PlannedButton>>();
        foreach (var a in appointments.Take(MaxListAppointments))
        {
            var cancel = $"❌ Скасувати {ChatTemplates.ShortWhen(a.StartsAt, a.Timezone)}";
            var cancelAt = options.Count;
            options.Add(new KeyboardOption(ButtonVerbs.CancelAsk, cancel, AppointmentId: a.Id));
            var resched = "🕐 Змінити час";
            var reschedAt = options.Count;
            options.Add(new KeyboardOption(ButtonVerbs.Reschedule, resched, AppointmentId: a.Id));
            rows.Add([new PlannedButton(cancel, ButtonVerbs.CancelAsk, cancelAt), new PlannedButton(resched, ButtonVerbs.Reschedule, reschedAt)]);
        }
        rows.Add([new PlannedButton(StatelessLabels.Book, ButtonVerbs.Book)]);
        return new KeyboardPlan(KeyboardKinds.AppointmentList, options, rows);
    }

    /// <summary>[Так, скасувати] [Ні, залишити].</summary>
    public KeyboardPlan CancelConfirm(Guid appointmentId) => new(
        KeyboardKinds.CancelConfirm,
        [new(ButtonVerbs.CancelYes, "Так, скасувати", AppointmentId: appointmentId), new(ButtonVerbs.CancelNo, "Ні, залишити", AppointmentId: appointmentId)],
        [[new("Так, скасувати", ButtonVerbs.CancelYes), new("Ні, залишити", ButtonVerbs.CancelNo)]],
        AppointmentId: appointmentId);

    /// <summary>Під T-confirmed, коли вибір нагадування не потрібен: [❌ Скасувати візит].</summary>
    public KeyboardPlan AppointmentActions(Guid appointmentId) => new(
        KeyboardKinds.AppointmentActions,
        [new(ButtonVerbs.CancelAsk, "❌ Скасувати візит", AppointmentId: appointmentId)],
        [[new("❌ Скасувати візит", ButtonVerbs.CancelAsk)]],
        AppointmentId: appointmentId);

    /// <summary>/start: [📅 Записатися] [🗓 Мої записи] / [👤 Адміністратор].</summary>
    public KeyboardPlan MainMenu() => Stateless(
    [
        [new PlannedButton(StatelessLabels.Book, ButtonVerbs.Book), new PlannedButton(StatelessLabels.My, ButtonVerbs.My)],
        [new PlannedButton(StatelessLabels.Human, ButtonVerbs.Human)],
    ]);

    /// <summary>[📅 Записатися] (порожній список записів).</summary>
    public KeyboardPlan BookOnly() => Stateless([[new PlannedButton(StatelessLabels.Book, ButtonVerbs.Book)]]);

    /// <summary>
    /// TASK-782 (§26.9.1): три кнопки <c>mc</c> (повна / вибіркова / не надсилати), по одній у рядку; options несуть рівень і версію тексту
    /// (<paramref name="textVersion"/> = <c>Marketing:ConsentTextVersion</c>). TTL ставить <see cref="ChatKeyboardService"/>.
    /// </summary>
    public KeyboardPlan MarketingConsent(string textVersion)
    {
        var options = MarketingKeyboards.ConsentOptions(textVersion);
        return new KeyboardPlan(KeyboardKinds.MarketingConsent, options,
            options.Select((o, i) => (IReadOnlyList<PlannedButton>)[new PlannedButton(o.Label, ButtonVerbs.MarketingConsent, i)]).ToList(), MaxPerRow: 1);
    }

    /// <summary>[👤 Передати адміністратору] (T-help, помилка підготовки запису).</summary>
    public KeyboardPlan HumanOnly() => Stateless([[new PlannedButton(StatelessLabels.HumanLong, ButtonVerbs.Human)]]);

    private static KeyboardPlan Stateless(IReadOnlyList<IReadOnlyList<PlannedButton>> rows) => new(null, [], rows);

    private static DateOnly ToLocalDate(DateTimeOffset at, string? timezone)
    {
        try
        {
            return DateOnly.FromDateTime(string.IsNullOrWhiteSpace(timezone)
                ? at.UtcDateTime : TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(timezone)).DateTime);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(at.UtcDateTime);
        }
    }
}
