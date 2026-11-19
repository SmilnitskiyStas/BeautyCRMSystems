namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

/// <summary>
/// Клавіатури маркетингу (TASK-781, §26.9.1, §26.10): options і розкладка для <c>marketing_consent</c> та <c>campaign_offer</c>.
/// Лише форма кнопок і текстовий фолбек; тексти повідомлень, вибір моменту й запис згоди - конвеєр (TASK-782) / воркер (TASK-780).
/// </summary>
public static class MarketingKeyboards
{
    public const string FullLabel = "📰 Повна інформація";
    public const string SelectiveLabel = "🏷 Лише акції на мої послуги";
    public const string NoneLabel = "🔕 Не надсилати";
    public const string OfferWithCouponLabel = "🎟 Записатися зі знижкою";
    public const string OfferPlainLabel = "📅 Записатися";
    public const string OffLabel = "🔕 Не надсилати акції";

    /// <summary>Рівно 3 <c>mc</c> у порядку full, selective, none (index 0..2); <paramref name="textVersion"/> - <c>Marketing:ConsentTextVersion</c>.</summary>
    public static IReadOnlyList<KeyboardOption> ConsentOptions(string textVersion) =>
    [
        new(ButtonVerbs.MarketingConsent, FullLabel, Level: "full", TextVersion: textVersion),
        new(ButtonVerbs.MarketingConsent, SelectiveLabel, Level: "selective", TextVersion: textVersion),
        new(ButtonVerbs.MarketingConsent, NoneLabel, Level: "none", TextVersion: textVersion),
    ];

    /// <summary><c>co</c> (з купоном або без) + <c>i:mkoff</c>.</summary>
    public static IReadOnlyList<KeyboardOption> OfferOptions(bool withCoupon) =>
    [
        new(ButtonVerbs.CampaignOffer, withCoupon ? OfferWithCouponLabel : OfferPlainLabel),
        new(ButtonVerbs.MarketingOff, OffLabel),
    ];

    /// <summary>Розкладка згоди: по кнопці в рядку (довгі підписи); callback_data будується з токена створеної клавіатури.</summary>
    public static IReadOnlyList<IReadOnlyList<ActionButton>> ConsentRows(CreatedKeyboard kb, IReadOnlyList<KeyboardOption> options) =>
        options.Select((o, i) => (IReadOnlyList<ActionButton>)[new ActionButton(o.Label, kb.Data(o.Verb, i))]).ToList();

    /// <summary>Розкладка пропозиції: [co] і [i:mkoff] окремими рядками.</summary>
    public static IReadOnlyList<IReadOnlyList<ActionButton>> OfferRows(CreatedKeyboard kb, IReadOnlyList<KeyboardOption> options) =>
        options.Select(o => (IReadOnlyList<ActionButton>)[new ActionButton(o.Label, kb.Data(o.Verb))]).ToList();

    /// <summary>
    /// Фолбек для каналів без кнопок (§26.10): згоду текстом не даємо (лише кнопкою) - пропозиції немає; для розсилки - підказки текстом.
    /// <paramref name="code"/> (персональний або загальний) викликач не логує.
    /// </summary>
    public static string OfferFallback(string? code) =>
        (string.IsNullOrEmpty(code) ? "Щоб записатися, напишіть «Записатися»." : $"Щоб записатися зі знижкою, напишіть сюди код {code}.")
        + " Щоб не отримувати акції, напишіть «не надсилати акції» (або «Стоп» - тоді вимкнуться й нагадування).";
}
