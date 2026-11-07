namespace BeautyCrm.Application.Features.BeautyChatFlow;

/// <summary>Verb кнопок потоку (§28.5). Станові, index обов'язковий: <c>^(fl|fs|fp|fd|ft|fn):{token22}:[0-9]$</c>.</summary>
public static class FlowVerbs
{
    public const string Service = "fs";
    public const string Specialist = "fp";
    public const string Location = "fl";
    public const string Day = "fd";
    public const string Time = "ft";
    public const string Nav = "fn";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Service, Specialist, Location, Day, Time, Nav };

    public static bool IsFlow(string? verb) => verb is not null && All.Contains(verb);
}

/// <summary>Коди приміток над кроком (текст підбирає Infrastructure).</summary>
public static class FlowNotices
{
    public const string Unavailable = "unavailable";
    /// <summary>«Добре, цей час не бронюю. Ось наступний вільний час:» (кнопка «Інший час» на підсумку, §28.8).</summary>
    public const string OtherTime = "other_time";
}

/// <summary>Коди результату переходу.</summary>
public static class FlowResultCodes
{
    public const string Ok = "ok";
    public const string OptionUnavailable = "option_unavailable";
    public const string NoServices = "no_services";
    public const string Cancelled = "cancelled";
    public const string Invalid = "invalid";
}

/// <summary>Одна кнопка вибору: id/дата/час - з БД (потрапляють в <c>options</c> клавіатури); підпис - сира назва (санітизує Infrastructure).</summary>
public sealed record FlowItem(
    string Verb, string Label, Guid? ServiceId = null, Guid? SpecialistId = null, Guid? LocationId = null,
    string? Date = null, DateTimeOffset? StartsAt = null, string? Nav = null, string? Address = null);

/// <summary>Зроблений автоматично вибір для рядка «Обрано: …» (Kind: specialist | location).</summary>
public sealed record FlowChosen(string Kind, string Name, string? Address);

/// <summary>
/// Що показати клієнту на кроці (дані, не текст): вибір, навігація, примітка. Рендер у повідомлення й розкладку - Infrastructure.
/// </summary>
/// <param name="Nav">Навігація у порядку показу; <c>more</c> з <paramref name="MoreIsRestart"/> = «↩ На початок».</param>
/// <param name="NoSlots">Крок <c>time</c> без слотів у вікні (T-flow-no-slots), <c>NoDays</c> - крок <c>day</c> без дат.</param>
public sealed record FlowView(
    string Step, IReadOnlyList<FlowItem> Items, IReadOnlyList<string> Nav, IReadOnlyList<FlowChosen> Chosen, string? Notice, FlowNames Names,
    bool MoreIsRestart = false, bool NoSlots = false, bool NoDays = false);

/// <summary>Подія переходу: verb + посилання з <c>options</c> натиснутої кнопки (не з callback_data).</summary>
public sealed record FlowEvent(
    string Verb, Guid? ServiceId = null, Guid? SpecialistId = null, Guid? LocationId = null, string? Date = null,
    DateTimeOffset? StartsAt = null, string? Nav = null);

/// <summary>Префіл від LLM (<c>start_booking_flow</c>, §28.7). Усе перевіряється кодом; невалідне тихо відкидається.</summary>
public sealed record FlowPrefill(Guid? ServiceId = null, Guid? SpecialistId = null, Guid? LocationId = null, DateOnly? Day = null, TimeOnly? PreferredTime = null);

/// <summary>
/// Результат переходу: <see cref="Next"/> - новий стан (ще НЕ збережений; <c>Version</c> уже after-значення), <see cref="View"/> - що показати.
/// <see cref="IsNew"/> - потік створюється (старт). Збереження - <see cref="ChatBookingFlowService.PersistAsync"/>.
/// </summary>
public sealed record FlowResult(string Code, FlowState? Next, FlowView? View, bool IsNew = false)
{
    public bool IsStep => Next is { Status: FlowStatuses.Active } && View is not null;
}
