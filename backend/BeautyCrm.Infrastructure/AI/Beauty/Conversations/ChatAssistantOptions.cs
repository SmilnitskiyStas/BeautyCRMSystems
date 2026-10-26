using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

/// <summary>
/// Параметри конвеєра розмов (`ChatAssistant:*`, контракт §21.2). `Enabled=false` за замовчуванням: воркер нічого не робить,
/// вхідні лишаються `received`. Некоректні значення замінюються безпечними межами (clamp), старт не ламають.
/// </summary>
public sealed class ChatAssistantOptions
{
    public bool Enabled { get; init; }
    /// <summary>
    /// Ходи клієнта (пакет повідомлень = хід) на розмову за хвилину; перевищення = T-rate-limited («надсилаєте дуже часто»). `ChatAssistant:MaxTurnsPerConversationPerMinute`.
    /// TASK-735: це міра ПОВІДОМЛЕНЬ клієнта, окремо від бюджету запитів LLM.
    /// </summary>
    public int MaxTurnsPerConversationPerMinute { get; init; } = 8;
    /// <summary>Бюджет запитів LLM на розмову за хвилину (кожна ітерація tools = запит). Мусить вміщувати кілька ходів по <c>MaxToolIterations</c> (6): за замовч. 30. TASK-735: було 6.</summary>
    public int MaxCallsPerConversationPerMinute { get; init; } = 30;
    /// <summary>Бюджет запитів LLM на розмову за добу (БД). За замовч. 300 (було 100).</summary>
    public int MaxCallsPerConversationPerDay { get; init; } = 300;
    public int HandoffPauseHours { get; init; } = 12;
    public int LeaseSeconds { get; init; } = 120;
    public int SweepIntervalSeconds { get; init; } = 30;
    /// <summary>Скільки паралельних розмов обробляє воркер.</summary>
    public int Workers { get; init; } = 4;

    // Внутрішні константи (не конфіг контракту).
    /// <summary>Рядок `received` молодший за цей вік sweeper не чіпає: його веде сигнал ingest.</summary>
    public TimeSpan SweepMinAge { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Вхідні старші за цей вік не обробляються (відповідь через добу втрачає сенс; захист від «лавини» після вмикання).</summary>
    public TimeSpan MaxInboundAge { get; init; } = TimeSpan.FromHours(24);
    /// <summary>`sending` старше -> `pending` (reaper).</summary>
    public TimeSpan SendingStaleAfter { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>`pending` відповіді ШІ старше цього повторює воркер .NET (за відсутності Node-воркера).</summary>
    public TimeSpan PendingRetryAfter { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>T-text-only не частіше за цей інтервал на розмову.</summary>
    public TimeSpan TextOnlyThrottle { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Скільки разів хід може впасти на одному пакеті, перш ніж пакет позначається `skipped`.</summary>
    public int MaxTurnAttempts { get; init; } = 3;
    /// <summary>Захист від нескінченного циклу ходів в одному запуску.</summary>
    public int MaxTurnsPerRun { get; init; } = 8;
    public int MaxBatch { get; init; } = 50;

    // ---- бюджет і ліміти чату (M2) ----
    /// <summary>Нових розмов (перше повідомлення) на канал за годину, яким відповідає ШІ; решта отримує нейтральний шаблон. `ChatAssistant:MaxNewConversationsPerChannelPerHour`.</summary>
    public int MaxNewConversationsPerChannelPerHour { get; init; } = 30;
    /// <summary>Частка місячного ліміту запитів tenant, доступна чату (0.05..1). `ChatAssistant:TenantMonthlyShare`. Решта - менеджерським функціям.</summary>
    public double TenantMonthlyShare { get; init; } = 0.7;
    /// <summary>Пауза перед повтором ходу, відкладеного через тимчасову причину (ліміт частоти, 429), с. `ChatAssistant:RateLimitRetrySeconds`.</summary>
    public int RateLimitRetrySeconds { get; init; } = 45;
    /// <summary>Скільки разів хід можна відкласти; потім клієнт отримує нейтральний шаблон «зараз багато звернень» (без handoff).</summary>
    public int MaxDeferrals { get; init; } = 8;
    /// <summary>TTL питання про нагадування (§22.2): `min(starts_at, now + TTL)`, годин. `ChatAssistant:ReminderPromptTtlHours` (1..72).</summary>
    public int ReminderPromptTtlHours { get; init; } = 24;
    /// <summary>Скільки майбутніх записів переплановує «Стоп» (§22.2 `Reminders:MaxUpcomingReplan`, 1..500).</summary>
    public int MaxUpcomingReplan { get; init; } = 50;
    /// <summary>
    /// M3 (TASK-759): шаблонні відповіді без LLM (команди, меню нагадувань, текст-fallback, інформаційні кнопки) на розмову за хвилину.
    /// Понад ліміт: один T-rate-limited на хвилину, далі тиша. `ChatAssistant:MaxTemplateRepliesPerConversationPerMinute` (1..120).
    /// </summary>
    public int MaxTemplateRepliesPerConversationPerMinute { get; init; } = 6;
    /// <summary>M3: однакова довідкова відповідь (/start, /help, /book) не частіше за цей інтервал на розмову. `ChatAssistant:IdenticalTemplateThrottleSeconds` (1..3600).</summary>
    public int IdenticalTemplateThrottleSeconds { get; init; } = 30;
    public TimeSpan IdenticalTemplateThrottle => TimeSpan.FromSeconds(IdenticalTemplateThrottleSeconds);
    public TimeSpan RateLimitRetryDelay => TimeSpan.FromSeconds(RateLimitRetrySeconds);

    public static ChatAssistantOptions Read(IConfiguration c)
    {
        static int I(IConfiguration c, string key, int def, int min, int max) =>
            int.TryParse(c[$"ChatAssistant:{key}"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : def;
        return new ChatAssistantOptions
        {
            Enabled = bool.TryParse(c["ChatAssistant:Enabled"], out var e) && e,
            MaxTurnsPerConversationPerMinute = I(c, "MaxTurnsPerConversationPerMinute", 8, 1, 600),
            MaxCallsPerConversationPerMinute = I(c, "MaxCallsPerConversationPerMinute", 30, 1, 600),
            MaxCallsPerConversationPerDay = I(c, "MaxCallsPerConversationPerDay", 300, 1, 100_000),
            HandoffPauseHours = I(c, "HandoffPauseHours", 12, 1, 24 * 30),
            LeaseSeconds = I(c, "LeaseSeconds", 120, 10, 3600),
            SweepIntervalSeconds = I(c, "SweepIntervalSeconds", 30, 1, 3600),
            Workers = I(c, "Workers", 4, 1, 32),
            MaxNewConversationsPerChannelPerHour = I(c, "MaxNewConversationsPerChannelPerHour", 30, 1, 100_000),
            TenantMonthlyShare = double.TryParse(c["ChatAssistant:TenantMonthlyShare"], NumberStyles.Float, CultureInfo.InvariantCulture, out var share)
                ? Math.Clamp(share, 0.05, 1.0) : 0.7,
            RateLimitRetrySeconds = I(c, "RateLimitRetrySeconds", 45, 5, 3600),
            MaxDeferrals = I(c, "MaxDeferrals", 8, 1, 100),
            MaxTemplateRepliesPerConversationPerMinute = I(c, "MaxTemplateRepliesPerConversationPerMinute", 6, 1, 120),
            IdenticalTemplateThrottleSeconds = I(c, "IdenticalTemplateThrottleSeconds", 30, 1, 3600),
            ReminderPromptTtlHours = I(c, "ReminderPromptTtlHours", 24, 1, 72),
            MaxUpcomingReplan = int.TryParse(c["Reminders:MaxUpcomingReplan"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mur)
                ? Math.Clamp(mur, 1, 500) : 50,
        };
    }
}

/// <summary>Журнал розмов (`beauty_ai_actions`), службові записи обліку.</summary>
public static class ChatJournal
{
    /// <summary>Облік запитів LLM за хід: payload {"calls":n}. Джерело добового ліміту розмови й місячного бюджету чату. Не показується в `GET /ai/actions`.</summary>
    public const string LlmUsage = "chat_llm_usage";
    public const string OutputFlagged = "output_flagged";
    /// <summary>Вибір нагадування клієнтом у чаті (TASK-740): payload {appointmentId, method, offsets, via}.</summary>
    public const string ReminderChoice = "reminder_choice";
    /// <summary>«Стоп» від клієнта (TASK-740): payload {replanned, via}.</summary>
    public const string ReminderStop = "reminder_stop";
}

/// <summary>
/// Ліміти розмови за хвилину (in-memory, ковзне вікно; як <see cref="AiRateLimiter"/> - один інстанс): запити LLM (бюджет) і ходи клієнта
/// (TASK-735: «надсилаєте дуже часто» рахує повідомлення/ходи, а не ітерації tools). Добовий ліміт запитів - у БД.
/// </summary>
public sealed class ChatRateLimiter(TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _hits = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _turns = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _templates = new();

    private Queue<DateTimeOffset> Fresh(Guid conversationId, Dictionary<Guid, Queue<DateTimeOffset>>? map = null)
    {
        map ??= _hits;
        var now = clock.GetUtcNow();
        if (!map.TryGetValue(conversationId, out var q)) map[conversationId] = q = new Queue<DateTimeOffset>();
        while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
        return q;
    }

    public bool IsLimited(Guid conversationId, int perMinute)
    {
        lock (_gate) return Fresh(conversationId).Count >= perMinute;
    }

    /// <summary>Ходів клієнта за хвилину вже не менше ліміту.</summary>
    public bool IsTurnLimited(Guid conversationId, int perMinute)
    {
        lock (_gate) return Fresh(conversationId, _turns).Count >= perMinute;
    }

    /// <summary>M3: шаблонних відповідей без LLM за хвилину вже не менше ліміту.</summary>
    public bool IsTemplateLimited(Guid conversationId, int perMinute)
    {
        lock (_gate) return Fresh(conversationId, _templates).Count >= perMinute;
    }

    /// <summary>M3: фіксує шаблонну відповідь без LLM (команда, меню, довідкова кнопка).</summary>
    public void RecordTemplate(Guid conversationId)
    {
        lock (_gate)
        {
            Fresh(conversationId, _templates).Enqueue(clock.GetUtcNow());
            if (_templates.Count > 10_000)
                foreach (var k in _templates.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) _templates.Remove(k);
        }
    }

    /// <summary>Фіксує хід клієнта, що йде до моделі (один раз на хід, не на ітерацію).</summary>
    public void RecordTurn(Guid conversationId)
    {
        lock (_gate)
        {
            Fresh(conversationId, _turns).Enqueue(clock.GetUtcNow());
            if (_turns.Count > 10_000)
                foreach (var k in _turns.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) _turns.Remove(k);
        }
    }

    /// <summary>Фіксує запити LLM (перший - безпосередньо перед запитом до моделі; решта ітерацій tools - після ходу).</summary>
    public void Record(Guid conversationId, int count = 1)
    {
        lock (_gate)
        {
            var q = Fresh(conversationId);
            for (var i = 0; i < count; i++) q.Enqueue(clock.GetUtcNow());
            if (_hits.Count > 10_000) // прибирання порожніх черг
                foreach (var k in _hits.Where(x => x.Value.Count == 0).Select(x => x.Key).ToList()) _hits.Remove(k);
        }
    }
}
