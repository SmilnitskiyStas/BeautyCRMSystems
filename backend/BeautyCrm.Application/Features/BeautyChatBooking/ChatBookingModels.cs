using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyChatBooking;

// Запис з чату клієнта (TASK-726, §21.6, ADR-011 §2.5). Усе в межах tenant (RLS) і розмови; tenant береться з контексту каналу.

public sealed class ChatBookingOptions
{
    private int _ttl = 15;
    /// <summary>Скільки діє показаний підсумок, хв (5..60; `ChatAssistant:ProposalTtlMinutes`).</summary>
    public int ProposalTtlMinutes { get => _ttl; set => _ttl = Math.Clamp(value, 5, 60); }
    /// <summary>Скільки пропозиція може лишатися в `confirmed` без запису, перш ніж повтор підтвердження перезапустить створення (падіння після CAS).</summary>
    public TimeSpan StaleConfirmAfter { get; set; } = TimeSpan.FromSeconds(30);
}

public static class ChatProposalStatus
{
    public const string Prepared = "prepared";
    public const string Shown = "shown";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Superseded = "superseded";
    public const string Failed = "failed";
}

/// <param name="TokenHash">SHA-256 серверного confirmation_token; сам токен не зберігається й не віддається ні моделі, ні клієнту.</param>
public sealed record ChatProposal(
    Guid Id, Guid ConversationId, Guid? ClientId, Guid LocationId, Guid SpecialistId, Guid ServiceId, DateTimeOffset StartsAt,
    int DurationMinutes, decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string Status, string TokenHash,
    Guid? SummaryMessageId, DateTimeOffset ExpiresAt, Guid? AppointmentId, string? FailureCode, DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public sealed record NewChatProposal(
    Guid Id, Guid ConversationId, Guid? ClientId, Guid LocationId, Guid SpecialistId, Guid ServiceId, DateTimeOffset StartsAt,
    int DurationMinutes, decimal PriceOriginal, decimal PriceFinal, Guid? PromotionId, string TokenHash,
    DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt);

public sealed record ChatConversationRef(Guid Id, Guid? ClientId);

/// <summary>
/// Порт даних запису з чату. Реалізація — Infrastructure (EF над `beauty_chat_booking_proposals`, TASK-724) під RLS tenant-а;
/// усі методи, що приймають conversationId, фільтрують за ним (пропозиція чужої розмови = не знайдена).
/// </summary>
public interface IChatBookingStore
{
    /// <summary>Розмова tenant-а; null — немає (або чужий tenant).</summary>
    Task<ChatConversationRef?> GetConversationAsync(Guid conversationId, CancellationToken ct);
    /// <summary>В одній транзакції: активну (prepared|shown) пропозицію розмови -> superseded, нова -> prepared (частковий UNIQUE на розмову).</summary>
    Task<ChatProposal> CreateProposalAsync(NewChatProposal proposal, CancellationToken ct);
    Task<ChatProposal?> GetProposalAsync(Guid conversationId, Guid proposalId, CancellationToken ct);
    /// <summary>Активна (prepared|shown) пропозиція розмови.</summary>
    Task<ChatProposal?> FindActiveProposalAsync(Guid conversationId, CancellationToken ct);
    /// <summary>prepared -> shown + summary_message_id (CAS). false — стан уже інший.</summary>
    Task<bool> MarkShownAsync(Guid conversationId, Guid proposalId, Guid summaryMessageId, CancellationToken ct);
    /// <summary>CAS статусу (`UPDATE ... WHERE status = from`): true лише для ОДНОГО з паралельних викликів. Термінальні стани фіксують decided_at.</summary>
    Task<bool> TryTransitionAsync(Guid conversationId, Guid proposalId, string from, string to, string? failureCode, DateTimeOffset at, CancellationToken ct);
    /// <summary>Усі активні пропозиції розмови -> superseded; повертає кількість.</summary>
    Task<int> SupersedeActiveAsync(Guid conversationId, DateTimeOffset at, CancellationToken ct);
    /// <summary>
    /// Атомарно: якщо у розмови немає client_id — створити клієнта (phone/email = NULL, marketing_consent = false) і записати
    /// conversation.client_id; інакше повернути наявного. Паралельні виклики дають одного клієнта.
    /// </summary>
    Task<Guid> EnsureClientAsync(Guid conversationId, string fullName, CancellationToken ct);
    /// <summary>appointment_id + client_id пропозиції (статус уже confirmed), decided_at.</summary>
    Task CompleteAsync(Guid conversationId, Guid proposalId, Guid appointmentId, Guid clientId, DateTimeOffset at, CancellationToken ct);
    /// <summary>Майбутні активні (pending|confirmed) записи клієнта з source ∈ telegram|instagram.</summary>
    Task<int> CountActiveByClientAsync(Guid clientId, DateTimeOffset now, CancellationToken ct);
    /// <summary>Записи клієнта (source ∈ telegram|instagram, будь-який статус), створені після since.</summary>
    Task<int> CountRecentByClientAsync(Guid clientId, DateTimeOffset since, CancellationToken ct);
}

// ---- вхід / вихід сервісу ----

public sealed record ChatPrepareRequest(Guid ConversationId, Guid LocationId, Guid ServiceId, Guid SpecialistId, DateTimeOffset StartsAt);

/// <summary>Дані для канонічного підсумку (T-summary формує КОД, не LLM). Без id і токена.</summary>
public sealed record ChatSummary(
    string ServiceName, string SpecialistName, string LocationName, string? Address, DateOnly LocalDate, TimeOnly LocalTime,
    string Timezone, int DurationMinutes, decimal PriceOriginal, decimal PriceFinal, string? PromotionName);

/// <summary>
/// Результат етапу 1. <see cref="ConfirmationToken"/> — серверний секрет: не серіалізується й не друкується; шар ШІ віддає моделі
/// лише <see cref="Summary"/> (без ProposalId і токена).
/// </summary>
public sealed record ChatPrepared(Guid ProposalId, ChatSummary Summary, DateTimeOffset ExpiresAt,
    [property: JsonIgnore] string ConfirmationToken)
{
    private bool PrintMembers(System.Text.StringBuilder sb)
    {
        sb.Append("ProposalId = ").Append(ProposalId).Append(", ExpiresAt = ").Append(ExpiresAt);
        return true;
    }
}

/// <summary>Профіль Telegram для імені нового клієнта (телефон не запитується й не зберігається).</summary>
public sealed record TelegramProfile(string? FirstName, string? LastName, string? Username);

/// <param name="ClientConfirmed">
/// Явний вхід «клієнт підтвердив»: перевірку тексту («так/підтверджую/yes/да», єдине повідомлення, перше після підсумку)
/// виконує конвеєр (TASK-728). Сервіс сам перевіряє пропозицію, розмову, TTL, слот і ціну.
/// </param>
/// <param name="ConfirmationToken">Необов'язково: якщо передано, мусить відповідати пропозиції (чужий токен = не знайдено).</param>
public sealed record ChatConfirmRequest(Guid ConversationId, Guid ProposalId, bool ClientConfirmed,
    TelegramProfile? Profile = null, string? ConfirmationToken = null);

/// <param name="AlreadyConfirmed">true — запис уже існував (повторне «Так» / повтор після гонки): T-already-confirmed, нового запису немає.</param>
/// <param name="ReminderChoiceNeeded">
/// §22.4/§22.7: у клієнта немає вподобання нагадувань - запис створено з планом none, конвеєр (TASK-740) додає T-reminder-offer
/// у те саме повідомлення й ставить pending_prompt. false - нагадування вже за вподобанням (рядок про них у T-confirmed).
/// </param>
public sealed record ChatConfirmed(AppointmentDto Appointment, bool AlreadyConfirmed, bool ReminderChoiceNeeded = false);

/// <summary>Ім'я нового клієнта з профілю Telegram: first+last, інакше username, інакше «Клієнт Telegram»; 2..100 символів, без керівних.</summary>
public static class ChatClientName
{
    public const string Fallback = "Клієнт Telegram";

    public static string Build(TelegramProfile? p)
    {
        var full = Clean($"{p?.FirstName} {p?.LastName}");
        if (full.Length >= 2) return Truncate(full);
        var user = Clean(p?.Username).TrimStart('@');
        return user.Length >= 2 ? Truncate(user) : Fallback;
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var chars = s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
            .Select(c => char.IsWhiteSpace(c) ? ' ' : c);
        return string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string s)
    {
        if (s.Length <= 100) return s;
        var cut = s[..100];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut.TrimEnd();
    }
}
