using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Inbox;

// Inbox розмов (TASK-739, контракт §22.6.5, ADR-012 §2.6). DTO - immutable records, сутності назовні не віддаються.

public sealed record InboxChannelDto(Guid Id, string Type, string Name);
public sealed record InboxLocationDto(Guid Id, string Name);
/// <param name="PhoneMasked">TASK-769 (§25.7): «+», потім • замість цифр, крім останніх двох; повний номер inbox не віддає ніколи.</param>
/// <param name="PhonePending">Клієнт надав інший номер у чаті, очікує рішення адміністратора (сам номер - лише в картці клієнта й call-tasks).</param>
public sealed record InboxClientDto(
    Guid Id, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PhoneMasked = null,
    bool PhonePending = false);

/// <param name="Title">Ім'я клієнта, інакше «Клієнт {канал}». <c>external_chat_id</c> не віддається ніколи.</param>
/// <param name="AiPausedUntil">Null при <c>aiState=handoff</c> = пауза безстроково (до «Повернути ШІ»).</param>
public record InboxConversationDto(
    Guid Id, InboxChannelDto Channel, InboxLocationDto? Location, InboxClientDto? Client, string Title, string AiState,
    string? HandoffReason, DateTimeOffset? AiPausedUntil, DateTimeOffset? AwaitingStaffSince, DateTimeOffset? LastMessageAt,
    string LastMessagePreview, string? LastMessageSenderType, int UnreadCount);

public sealed record InboxConversationDetailDto(
    Guid Id, InboxChannelDto Channel, InboxLocationDto? Location, InboxClientDto? Client, string Title, string AiState,
    string? HandoffReason, DateTimeOffset? AiPausedUntil, DateTimeOffset? AwaitingStaffSince, DateTimeOffset? LastMessageAt,
    string LastMessagePreview, string? LastMessageSenderType, int UnreadCount, bool CanReply, string? ReplyBlockedReason)
    : InboxConversationDto(Id, Channel, Location, Client, Title, AiState, HandoffReason, AiPausedUntil, AwaitingStaffSince,
        LastMessageAt, LastMessagePreview, LastMessageSenderType, UnreadCount);

public sealed record InboxListMeta(int NeedsReplyCount);
public sealed record InboxListDto(IReadOnlyList<InboxConversationDto> Items, string? NextCursor, InboxListMeta Meta);

public sealed record InboxUserDto(Guid Id, string Name);

/// <param name="FailureCode">Лише для <c>failed</c>: bot_blocked | chat_not_found | channel_inactive | token_missing | send_failed.</param>
public sealed record InboxMessageDto(
    Guid Id, string Direction, string SenderType, InboxUserDto? SenderUser, string Kind, string Body, string Status,
    string? FailureCode, DateTimeOffset SentAt, DateTimeOffset CreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Buttons = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PressedLabel = null,
    // TASK-768 (§25.4): own | foreign | invalid лише для kind=contact; номера немає.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContactVerdict = null);

public sealed record InboxProposalDto(string Status, DateTimeOffset ExpiresAt, string? SummaryText);
public sealed record InboxReminderPlanDto(string Method, IReadOnlyList<short> OffsetsMinutes);
public sealed record InboxAppointmentDto(
    Guid Id, DateTimeOffset StartsAt, string ServiceName, string LocationName, InboxReminderPlanDto Reminders,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? ClientConfirmedAt = null);

/// <param name="HasMore">Адитивно до §22.6.5: є ще старіші повідомлення (наступна сторінка: <c>before</c> = id першого елемента).</param>
public sealed record InboxDetailDto(
    InboxConversationDetailDto Conversation, IReadOnlyList<InboxMessageDto> Messages, bool HasMore, InboxProposalDto? ActiveProposal,
    IReadOnlyList<InboxAppointmentDto> UpcomingAppointments);

public sealed record InboxReplyMessageDto(Guid Id, string Status);
public sealed record InboxReplyAcceptedDto(InboxReplyMessageDto Message);

/// <summary>Коди помилок inbox (§22.6.5). Тексти повідомлень англійською, без ПД.</summary>
public static class InboxErrors
{
    public const string ConversationNotFound = "conversation_not_found";
    public const string MessageNotFound = "message_not_found";
    public const string ForbiddenRole = "forbidden_role";
    public const string ChannelInactive = "channel_inactive";
    public const string ChannelUnsupported = "channel_unsupported";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string InvalidText = "invalid_text";
    /// <summary>L8: текст у межах 3500 кодових точок, але понад 4096 одиниць UTF-16 (ліміт Telegram sendMessage).</summary>
    public const string InvalidLength = "invalid_length";
    public const string InvalidFilter = "invalid_filter";
    public const string InvalidCursor = "invalid_cursor";
    public const string RateLimited = "rate_limited";
}

/// <summary>Налаштування inbox (§22.2): <c>Inbox:*</c>.</summary>
public sealed class InboxOptions
{
    public const int MaxTextLength = 3500;
    /// <summary>Ліміт Telegram sendMessage в одиницях UTF-16 (емодзі = 2 одиниці), L8.</summary>
    public const int MaxUtf16Length = 4096;
    public int ReplyPerUserPerMinute { get; init; } = 20;
    public int ReplyPerConversationPerMinute { get; init; } = 10;
    public int ReplyPerTenantPerHour { get; init; } = 300;
    public int ReadPerUserPerMinute { get; init; } = 120;

    public static InboxOptions Read(IConfiguration c)
    {
        static int I(IConfiguration c, string key, int def) => int.TryParse(c[$"Inbox:{key}"], out var v) && v is >= 1 and <= 1_000_000 ? v : def;
        return new InboxOptions
        {
            ReplyPerUserPerMinute = I(c, "ReplyPerUserPerMinute", 20),
            ReplyPerConversationPerMinute = I(c, "ReplyPerConversationPerMinute", 10),
            ReplyPerTenantPerHour = I(c, "ReplyPerTenantPerHour", 300),
            ReadPerUserPerMinute = I(c, "ReadPerUserPerMinute", 120),
        };
    }
}

/// <summary>
/// Ліміти inbox (in-memory, ковзне вікно; як <c>ChatRateLimiter</c> - один інстанс). Відповіді: користувач / розмова / tenant
/// (перевіряються разом, ліміти не списуються, якщо хоч один вичерпано); читання GET: користувач.
/// </summary>
public sealed class InboxThrottle(InboxOptions options, TimeProvider clock)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private const int PruneAbove = 5000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _hits = new();

    /// <returns>false = перевищено; <paramref name="retryAfter"/> - скільки чекати до найближчого звільнення.</returns>
    public bool TryAcquireReply(Guid tenantId, Guid userId, Guid conversationId, out TimeSpan retryAfter)
    {
        var rules = new (string Key, int Limit, TimeSpan Window)[]
        {
            ($"ru:{tenantId:N}:{userId:N}", options.ReplyPerUserPerMinute, Minute),
            ($"rc:{tenantId:N}:{conversationId:N}", options.ReplyPerConversationPerMinute, Minute),
            ($"rt:{tenantId:N}", options.ReplyPerTenantPerHour, Hour),
        };
        return TryAcquire(rules, out retryAfter);
    }

    public bool TryAcquireRead(Guid tenantId, Guid userId, out TimeSpan retryAfter) =>
        TryAcquire([($"gu:{tenantId:N}:{userId:N}", options.ReadPerUserPerMinute, Minute)], out retryAfter);

    private bool TryAcquire((string Key, int Limit, TimeSpan Window)[] rules, out TimeSpan retryAfter)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_hits.Count > PruneAbove) Prune(now);
            retryAfter = TimeSpan.Zero;
            var queues = new Queue<DateTimeOffset>[rules.Length];
            for (var i = 0; i < rules.Length; i++)
            {
                var (key, limit, window) = rules[i];
                if (!_hits.TryGetValue(key, out var q)) _hits[key] = q = new Queue<DateTimeOffset>();
                while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
                queues[i] = q;
                if (q.Count >= limit)
                {
                    var wait = q.Peek() + window - now;
                    if (wait > retryAfter) retryAfter = wait;
                }
            }
            if (retryAfter > TimeSpan.Zero) return false;
            foreach (var q in queues) q.Enqueue(now);
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() >= Hour).Select(kv => kv.Key).ToList())
            _hits.Remove(key);
    }
}
