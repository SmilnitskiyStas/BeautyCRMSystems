using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <param name="Mode">Лише для внутрішніх викликів і тестів; ніколи не береться з тексту клієнта. Null = режим tenant.</param>
/// <param name="ConversationId">Розмова каналу (scope ClientChat): з контексту ходу, не від моделі. Йде в tools і журнал.</param>
/// <param name="History">
/// Попередні повідомлення розмови (старі -> нові), БЕЗ поточного. Модель бачить обмежене вікно (<see cref="ChatPromptOptions"/>);
/// повідомлення клієнта проходять <see cref="PromptInjectionGuard.Prepare"/> і йдуть у &lt;client_message&gt;. Пакет із кількох
/// швидких повідомлень: усі, крім останнього, - у History, останнє - у <paramref name="ClientMessage"/>.
/// </param>
/// <param name="DefaultLocationId">Заклад каналу за замовчуванням для tools ClientChat.</param>
/// <param name="RetryEmptyReply">
/// Лише код конвеєра (TASK-735): попередня спроба цього ходу не дала тексту для клієнта; модель отримує службову підказку відповісти
/// коротким текстом. Не береться з тексту клієнта.
/// </param>
public sealed record AssistantRequest(
    Guid TenantId, Guid? ClientId, string ClientMessage, AiMode? Mode = null, AiScope Scope = AiScope.Client,
    Guid? ConversationId = null, IReadOnlyList<ChatHistoryItem>? History = null, Guid? DefaultLocationId = null,
    bool RetryEmptyReply = false);

/// <param name="Chat">Підсумок ходу ClientChat (підсумок запису від коду, handoff моделі, помилки tools, чернетка); null в інших scope.</param>
/// <param name="LlmCalls">Скільки запитів до провайдера зроблено за хід (облік ліміту розмови, M2).</param>
/// <param name="Deferred">
/// Лише ClientChat: тимчасова причина (частота викликів tenant/чату, 429 провайдера). Передачі менеджеру немає, відповіді немає:
/// конвеєр лишає вхідні `received` і повторює хід із затримкою.
/// </param>
public sealed record AssistantResult(
    string? ReplyText, bool HandedOff, string? HandoffReason, bool InjectionSuspected, AiMode Mode, ChatTurnOutcome? Chat = null,
    int LlmCalls = 0, bool Deferred = false);

/// <summary>
/// Оркестрація: правила передачі -> захист від injection -> цикл tool-use з AI-клієнтом.
/// Режим і додаток до промпту беруться з налаштувань tenant (<see cref="AiTenantRuntime"/>), а не з глобального конфігу.
/// Недоступність ШІ (ключ, ліміт, провайдер) = передача менеджеру (<c>ai_unavailable</c>), без автовідповіді.
/// У scope <see cref="AiScope.ClientChat"/> додатково: історія розмови, tools чату, завершення ходу після request_handoff.
/// Асистент НЕ надсилає відповідь і НЕ підтверджує запис: це робить конвеєр розмов (TASK-728).
/// </summary>
public sealed class BeautyAssistant
{
    public const string HandoffAiUnavailable = "ai_unavailable";

    private readonly IAiClient _ai;
    private readonly AiToolExecutor _tools;
    private readonly IAiActionJournal _journal;
    private readonly AiSettings _settings;
    private readonly TimeProvider _time;
    private readonly AiTenantRuntime? _runtime;
    private readonly ChatPromptOptions _chatOptions;
    private readonly ChatContextBuilder? _chatContext;

    /// <summary>Службова підказка повторної спроби (E): формує код, потрапляє в системний промпт, не в текст клієнта.</summary>
    public const string RetryHint =
        "Попередня спроба не дала тексту для клієнта. Напиши клієнту коротку відповідь звичайним текстом (без ідентифікаторів і службових даних) " +
        "або постав уточнювальне запитання.";

    /// <param name="runtime">Налаштування tenant. Null лише в юніт-тестах: тоді діє <see cref="AiSettings.DefaultMode"/> і типовий додаток.</param>
    /// <param name="chatOptions">Вікно історії чату; null = типові (20 повідомлень, 6000 символів).</param>
    /// <param name="chatContext">Дата/час і каталог закладу для системного промпту чату (TASK-735); null = без блоку (юніт-тести).</param>
    public BeautyAssistant(IAiClient ai, AiToolExecutor tools, IAiActionJournal journal, AiSettings settings, TimeProvider time,
        AiTenantRuntime? runtime = null, ChatPromptOptions? chatOptions = null, ChatContextBuilder? chatContext = null)
    {
        _ai = ai; _tools = tools; _journal = journal; _settings = settings; _time = time; _runtime = runtime;
        _chatOptions = chatOptions ?? new ChatPromptOptions();
        _chatContext = chatContext;
    }

    public async Task<AssistantResult> HandleAsync(AssistantRequest req, CancellationToken ct)
    {
        // Режим: явний внутрішній параметр -> режим tenant (БД; без рядка = confirm) -> (лише без runtime) DefaultMode.
        AiEffectiveSettings? tenantSettings = _runtime is null ? null : await _runtime.GetEffectiveAsync(req.TenantId, ct);
        var mode = req.Mode ?? tenantSettings?.Mode ?? _settings.DefaultMode;
        var chat = req.Scope == AiScope.ClientChat;
        var target = chat && req.ConversationId is { } conv ? $"conversation:{conv}" : req.ClientId is null ? "-" : $"client:{req.ClientId}";

        // Нормалізація, обрізання до ліміту й знешкодження тегів — один раз на вході, до всіх перевірок (M3, L9).
        var clientText = PromptInjectionGuard.Prepare(req.ClientMessage);

        var handoff = HandoffRules.Evaluate(clientText);
        if (handoff.Handoff)
        {
            await LogAsync(req, "handoff", target, AiActionStatus.HandedOff, mode, JsonSerializer.Serialize(new { reason = handoff.Reason }), ct);
            return new AssistantResult(null, true, handoff.Reason, false, mode);
        }

        var suspicious = PromptInjectionGuard.IsSuspicious(clientText);
        if (suspicious)
            await LogAsync(req, "injection_suspected", target, AiActionStatus.Flagged, mode, "{}", ct);

        // Tenant, розмова й клієнт - лише з запиту (контекст ходу). Стан ходу ClientChat збирається виконавцем tools.
        var turn = chat ? new ChatTurnState() : null;
        var ctx = new AiToolContext(req.TenantId, req.ClientId, mode, req.Scope, req.ConversationId, req.DefaultLocationId, turn);

        var messages = ChatHistory.Build(req.History, _chatOptions);
        ChatHistory.AddUser(messages, PromptInjectionGuard.Wrap(clientText));
        var defs = AiToolCatalog.For(req.Scope);
        // Службовий блок ходу (дата/час закладу, каталог): формує код з довіреного порту, поза <client_message>.
        string? turnContext = null;
        if (chat && _chatContext is not null)
            turnContext = await _chatContext.BuildAsync(req.TenantId, req.DefaultLocationId, ct, req.ClientId, req.ConversationId);
        if (chat && req.RetryEmptyReply)
            turnContext = turnContext is null ? RetryHint : $"{turnContext}\n{RetryHint}";
        var system = PromptComposer.Compose(mode, tenantSettings?.PromptAddendum, req.Scope, turnContext);
        string? lastText = null;
        var finished = false; // модель закінчила текстом (без нових tool-викликів) або хід завершено handoff
        var calls = 0;
        // M2: у чаті клієнта ітерацій менше (кожна - платний запит), ніж у менеджерському сценарії.
        var maxIterations = chat ? Math.Max(1, Math.Min(_chatOptions.MaxToolIterations, _settings.MaxToolIterations)) : _settings.MaxToolIterations;

        try
        {
            for (var n = 0; n < maxIterations; n++)
            {
                var resp = await _ai.CompleteAsync(new AiRequest(system, messages, defs, AiRoles.Chat, ClientChat: chat), ct);
                calls++;
                if (!string.IsNullOrWhiteSpace(resp.Text)) lastText = resp.Text;
                var toolCalls = resp.ToolCalls;
                if (toolCalls.Count == 0) { finished = true; break; }

                messages.Add(new AiMessage("assistant", resp.Blocks));
                var results = new List<AiBlock>();
                foreach (var call in toolCalls)
                {
                    var r = await _tools.ExecuteAsync(ctx, call.Name, call.Input, ct);
                    results.Add(new AiToolResultBlock(call.Id, PromptInjectionGuard.WrapToolResult(r.Content), r.IsError));
                }
                messages.Add(new AiMessage("user", results));

                // request_handoff завершує хід: далі клієнту піде шаблон передачі, а не текст моделі.
                if (turn?.HandoffReason is not null) { finished = true; break; }
            }
        }
        catch (Exception ex) when (ex is AiUnavailableException or AiProviderException)
        {
            // Чат клієнта: частота (tenant/чат) і 429 провайдера минуть самі, тож хід відкладається, а не віддається менеджеру на 12 год (M2).
            if (chat && (ex is AiRateLimitedException || ex is AiProviderException { Error: AiProviderError.RateLimited }))
                return new AssistantResult(null, false, null, suspicious, mode, Outcome(turn, mode), calls, Deferred: true);
            if (ex is AiProviderException) calls++; // запит дійшов до провайдера
            // Ліміт, ключ або збій провайдера: клієнту не відповідаємо від ШІ, діалог іде менеджеру.
            // TenantMismatchException сюди не потрапляє й перериває запуск.
            var reason = (ex as AiUnavailableException)?.Reason ?? (ex as AiProviderException)?.Error.ToString();
            await LogAsync(req, "handoff", target, AiActionStatus.HandedOff, mode,
                JsonSerializer.Serialize(new { reason = HandoffAiUnavailable, detail = reason }), ct);
            return new AssistantResult(null, true, HandoffAiUnavailable, suspicious, mode, Outcome(turn, mode), calls);
        }

        if (turn?.HandoffReason is { } modelReason)
            return new AssistantResult(null, true, modelReason, suspicious, mode, Outcome(turn, mode), calls);

        // Ітерації вичерпано, а модель усе ще викликала tools: замість загального «не вдалося» відповідає код (TASK-735).
        if (chat && !finished && turn is { Prepared: null })
        {
            await LogAsync(req, "chat_iterations_exhausted", target, AiActionStatus.Flagged, mode,
                JsonSerializer.Serialize(new { iterations = maxIterations, hadSlots = turn.LastSlots is not null }), ct);
            lastText = ChatFallbackText.Build(turn.LastSlots);
        }
        return new AssistantResult(lastText, false, null, suspicious, mode, Outcome(turn, mode), calls);
    }

    private static ChatTurnOutcome? Outcome(ChatTurnState? turn, AiMode mode) =>
        turn is null ? null : new ChatTurnOutcome(turn.Prepared, turn.HandoffReason, turn.ToolErrors, mode == AiMode.Suggest, turn.LastSlots);

    private Task LogAsync(AssistantRequest req, string action, string target, string status, AiMode mode, string payload, CancellationToken ct) =>
        _journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), req.TenantId, action, target, _time.GetUtcNow(), status, false, mode, payload,
            req.ConversationId), ct);
}
