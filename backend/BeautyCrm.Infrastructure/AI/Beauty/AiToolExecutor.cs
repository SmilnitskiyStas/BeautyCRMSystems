using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary><c>ClientChat</c> - чат клієнта в каналі (§21.7): окремий набір tools; <c>Client</c>/<c>Manager</c> не змінено.</summary>
public enum AiScope { Client, Manager, ClientChat }

/// <param name="ConversationId">Лише з контексту ходу (канал/розмова), ніколи з аргументів моделі.</param>
/// <param name="DefaultLocationId">Заклад каналу за замовчуванням для tools ClientChat.</param>
/// <param name="Turn">Стан ходу ClientChat (підсумок запису, handoff моделі, помилки tools); null поза ClientChat.</param>
public sealed record AiToolContext(
    Guid TenantId, Guid? ClientId, AiMode Mode, AiScope Scope,
    Guid? ConversationId = null, Guid? DefaultLocationId = null, ChatTurnState? Turn = null);
public sealed record AiToolResult(string Content, bool IsError, string? ActionStatus = null);

public static class AiToolNames
{
    public const string FindFreeSlots = "find_free_slots";
    public const string CreateAppointment = "create_appointment";
    public const string GetPrices = "get_prices";
    public const string GetActivePromotions = "get_active_promotions";
    public const string GetClientContext = "get_client_context";
    public const string DraftReply = "draft_reply";
    public const string SuggestPromotion = "suggest_promotion";
    public const string BuildAudience = "build_audience";

    public static readonly string[] ManagerOnly = { SuggestPromotion, BuildAudience };
}

public static class AiToolCatalog
{
    private static AiToolDefinition T(string n, string d, string props, string required) =>
        new(n, d, $"{{\"type\":\"object\",\"properties\":{{{props}}},\"required\":[{required}]}}");

    private const string Str = "\"type\":\"string\"";

    public static readonly IReadOnlyList<AiToolDefinition> All = new[]
    {
        T(AiToolNames.FindFreeSlots, "Вільні слоти на дату для послуги.",
            $"\"serviceId\":{{{Str}}},\"staffId\":{{{Str}}},\"date\":{{{Str},\"description\":\"YYYY-MM-DD\"}}", "\"serviceId\",\"date\""),
        T(AiToolNames.CreateAppointment, "Створити запис поточного клієнта (залежить від режиму: може потребувати підтвердження).",
            $"\"serviceId\":{{{Str}}},\"staffId\":{{{Str}}},\"start\":{{{Str},\"description\":\"ISO-8601\"}}", "\"serviceId\",\"staffId\",\"start\""),
        T(AiToolNames.GetPrices, "Актуальні послуги й ціни.", "", ""),
        T(AiToolNames.GetActivePromotions, "Активні акції.", "", ""),
        T(AiToolNames.GetClientContext, "Контекст поточного клієнта (візити, нотатки, алергії).", "", ""),
        T(AiToolNames.DraftReply, "Підготувати текст відповіді клієнту.", $"\"text\":{{{Str}}}", "\"text\""),
        T(AiToolNames.SuggestPromotion, "Запропонувати акцію менеджеру (лише менеджерський контекст).",
            $"\"goal\":{{{Str}}},\"discountPercent\":{{\"type\":\"number\"}},\"text\":{{{Str}}},\"sendAt\":{{{Str},\"description\":\"ISO-8601 локальний час розсилки\"}}",
            "\"goal\",\"discountPercent\",\"text\""),
        T(AiToolNames.BuildAudience, "Підібрати аудиторію (лише клієнти зі згодою) під сегмент.", $"\"segment\":{{{Str}}}", "\"segment\""),
    };

    private const string Uuid = "\"type\":\"string\",\"description\":\"UUID з результатів tools\"";

    /// <summary>
    /// Tools ClientChat (§21.7). Схеми без tenantId/clientId/conversationId. create_appointment, get_client_context, draft_reply,
    /// confirm_booking тут немає: модель їх не бачить, а виконавець відхиляє навіть явний виклик.
    /// </summary>
    public static readonly IReadOnlyList<AiToolDefinition> ClientChat = new[]
    {
        T(ClientChatToolNames.ListLocations, "Активні заклади: назва, адреса, зона, години роботи, вихідні.", "", ""),
        T(ClientChatToolNames.ListServices, "Послуги закладу з актуальною ціною й акцією (ціни лише звідси).",
            $"\"locationId\":{{{Uuid}}}", ""),
        T(ClientChatToolNames.ListSpecialists, "Майстри закладу (за потреби - що виконують послугу).",
            $"\"locationId\":{{{Uuid}}},\"serviceId\":{{{Uuid}}}", ""),
        T(ClientChatToolNames.FindSlots,
            "Вільні слоти на дату. Якщо клієнт назвав бажаний час, ЗАВЖДИ передай preferredTime: отримаєш 4-6 найближчих до нього слотів " +
            "(requestedTimeAvailable каже, чи є саме цей час). Клієнту показуй 4-6 варіантів, а не всю сітку. " +
            "startsAt зі слота передавай у prepare_booking без змін.",
            $"\"locationId\":{{{Uuid}}},\"serviceId\":{{{Uuid}}},\"specialistId\":{{{Uuid}}},\"date\":{{{Str},\"description\":\"YYYY-MM-DD, локальна дата закладу\"}}," +
            $"\"preferredTime\":{{{Str},\"description\":\"HH:mm, локальний бажаний час закладу (необов'язково)\"}}",
            "\"serviceId\",\"date\""),
        All.First(t => t.Name == AiToolNames.GetActivePromotions),
        T(ClientChatToolNames.PrepareBooking,
            "Підготувати запис на слот з find_slots. Запису НЕ створює: підсумок покаже система, запис - лише після відповіді клієнта «Так». " +
            "Час задай АБО startsAt зі слота find_slots без змін, АБО локальними date (YYYY-MM-DD) і time (HH:mm) закладу - offset додасть система. " +
            "Слот мусить бути серед вільних слотів find_slots.",
            $"\"locationId\":{{{Uuid}}},\"serviceId\":{{{Uuid}}},\"specialistId\":{{{Uuid}}},\"startsAt\":{{{Str},\"description\":\"ISO-8601 з offset, рівно як у find_slots\"}}," +
            $"\"date\":{{{Str},\"description\":\"YYYY-MM-DD, локальна дата закладу (разом з time, замість startsAt)\"}}," +
            $"\"time\":{{{Str},\"description\":\"HH:mm, локальний час закладу (разом з date, замість startsAt)\"}}",
            "\"serviceId\",\"specialistId\""),
        T(ClientChatToolNames.RequestHandoff, "Передати розмову адміністратору (скарга, оплата, людина, скасування/перенесення, незрозуміле). " +
            "reason=policy_unknown - питання про політику закладу (дзвінки, канали, нагадування, оплата, скасування, повернення), відповіді на яке немає в блоку ФАКТИ ЗАКЛАДУ.",
            "\"reason\":{\"type\":\"string\",\"enum\":[\"complaint\",\"unclear\",\"human_requested\",\"medical\",\"policy_unknown\",\"other\"]}", "\"reason\""),
    };

    public static IReadOnlyList<AiToolDefinition> For(AiScope scope) => scope switch
    {
        AiScope.ClientChat => ClientChat,
        AiScope.Manager => All,
        _ => All.Where(t => !AiToolNames.ManagerOnly.Contains(t.Name)).ToList(),
    };
}

/// <summary>
/// Виконує tool-виклики моделі. Режими, ліміти й згода перевіряються ТУТ (код), а не в промпті,
/// тому prompt injection не може їх змінити. Кожен виклик пишеться в beauty_ai_actions.
/// </summary>
public sealed class AiToolExecutor
{
    private readonly IFindFreeSlotsPort _slots;
    private readonly ICreateAppointmentPort _appointments;
    private readonly IGetPricesPort _prices;
    private readonly IGetActivePromotionsPort _promos;
    private readonly IGetClientContextPort _clients;
    private readonly IDraftReplyPort _drafts;
    private readonly ISuggestPromotionPort _promoSuggest;
    private readonly IBuildAudiencePort _audience;
    private readonly IAiActionJournal _journal;
    private readonly AiSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<AiToolExecutor>? _logger;
    private readonly ClientChatToolbox? _chat;

    /// <param name="chat">Tools scope ClientChat (TASK-727). Null лише в тестах без чату: тоді ClientChat-виклики відхиляються.</param>
    public AiToolExecutor(
        IFindFreeSlotsPort slots, ICreateAppointmentPort appointments, IGetPricesPort prices,
        IGetActivePromotionsPort promos, IGetClientContextPort clients, IDraftReplyPort drafts,
        ISuggestPromotionPort promoSuggest, IBuildAudiencePort audience, IAiActionJournal journal,
        AiSettings settings, TimeProvider time, ILogger<AiToolExecutor>? logger = null, ClientChatToolbox? chat = null)
    {
        _slots = slots; _appointments = appointments; _prices = prices; _promos = promos; _clients = clients;
        _drafts = drafts; _promoSuggest = promoSuggest; _audience = audience; _journal = journal;
        _settings = settings; _time = time; _logger = logger; _chat = chat;
    }

    public bool IsBroadcastHourAllowed(DateTimeOffset sendAt) =>
        sendAt.Hour >= _settings.BroadcastFromHour && sendAt.Hour < _settings.BroadcastToHour;

    public async Task<AiToolResult> ExecuteAsync(AiToolContext ctx, string tool, JsonElement input, CancellationToken ct)
    {
        try
        {
            // ClientChat: окремий закритий набір. Усе поза ним (create_appointment, get_client_context, draft_reply, confirm_booking...)
            // відхиляється тут, до будь-якого порту, незалежно від того, що «викликала» модель.
            if (ctx.Scope == AiScope.ClientChat)
            {
                if (_chat is null) return await RejectAsync(ctx, tool, "-", "client_chat_unavailable", ct);
                return await _chat.ExecuteAsync(ctx, tool, input, ct);
            }

            if (AiToolNames.ManagerOnly.Contains(tool) && ctx.Scope != AiScope.Manager)
                return await RejectAsync(ctx, tool, "-", "tool_not_allowed_in_client_scope", ct);

            return tool switch
            {
                AiToolNames.FindFreeSlots => await FindSlotsAsync(ctx, input, ct),
                AiToolNames.CreateAppointment => await CreateAppointmentAsync(ctx, input, ct),
                AiToolNames.GetPrices => await ReadAsync(ctx, tool, "prices", await _prices.GetAsync(ctx.TenantId, ct), ct),
                AiToolNames.GetActivePromotions => await ReadAsync(ctx, tool, "promotions", await _promos.GetAsync(ctx.TenantId, ct), ct),
                AiToolNames.GetClientContext => await ClientContextAsync(ctx, ct),
                AiToolNames.DraftReply => await DraftReplyAsync(ctx, input, ct),
                AiToolNames.SuggestPromotion => await SuggestPromotionAsync(ctx, input, ct),
                AiToolNames.BuildAudience => await BuildAudienceAsync(ctx, input, ct),
                _ => await RejectAsync(ctx, tool, "-", "unknown_tool", ct),
            };
        }
        catch (TenantMismatchException ex)
        {
            // Безпекова подія: НЕ invalid_arguments. Запуск переривається (виняток летить до викликача).
            await ReportTenantMismatchAsync(ctx, tool, ex);
            throw;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or JsonException)
        {
            return await RejectAsync(ctx, tool, "-", "invalid_arguments", ct);
        }
    }

    private async Task ReportTenantMismatchAsync(AiToolContext ctx, string tool, TenantMismatchException ex)
    {
        _logger?.LogError("ai_security_event {Event} tool={Tool} scopeTenant={ScopeTenant} requestedTenant={RequestedTenant}",
            "tenant_mismatch", tool, ex.ScopeTenantId, ex.RequestedTenantId);
        try
        {
            // Запис у журнал — під tenant автентифікованого scope (журнал сам звіряє tenant зі scope).
            await _journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), ex.ScopeTenantId, "security_tenant_mismatch", tool,
                _time.GetUtcNow(), AiActionStatus.Flagged, false, ctx.Mode, "{\"event\":\"tenant_mismatch\"}"), CancellationToken.None);
        }
        catch (Exception jex)
        {
            _logger?.LogError("ai_security_event journal_write_failed type={Type}", jex.GetType().Name);
        }
    }

    // ---- підтвердження / скасування людиною ----
    public async Task<AiToolResult> ConfirmAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || rec.Status != AiActionStatus.PendingConfirmation)
            return new AiToolResult("action_not_pending", true);

        // Атомарний CAS pending -> executing: паралельний approve/reject отримає action_not_pending, дія виконується один раз.
        if (!await _journal.TryTransitionAsync(tenantId, actionId, AiActionStatus.PendingConfirmation, AiActionStatus.Executing, ct))
            return new AiToolResult("action_not_pending", true);
        try
        {
            return await ExecuteConfirmedAsync(tenantId, rec, ct);
        }
        catch
        {
            // Дія не виконалась: повертаємо на підтвердження, щоб людина могла повторити або відхилити.
            await _journal.TryTransitionAsync(tenantId, actionId, AiActionStatus.Executing, AiActionStatus.PendingConfirmation, CancellationToken.None);
            throw;
        }
    }

    private async Task<AiToolResult> ExecuteConfirmedAsync(Guid tenantId, AiActionRecord rec, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(rec.PayloadJson);
        var p = doc.RootElement;
        switch (rec.Action)
        {
            case AiToolNames.CreateAppointment:
            {
                var created = await _appointments.CreateAsync(tenantId, Guid.Parse(p.GetProperty("clientId").GetString()!),
                    Guid.Parse(p.GetProperty("serviceId").GetString()!), Guid.Parse(p.GetProperty("staffId").GetString()!),
                    DateTimeOffset.Parse(p.GetProperty("start").GetString()!), ct);
                await _journal.UpdateAsync(rec with { Status = AiActionStatus.Done, Revertible = true, Target = $"appointment:{created.AppointmentId}" }, ct);
                return new AiToolResult($"appointment_created:{created.AppointmentId}", false, AiActionStatus.Done);
            }
            case AiToolNames.SuggestPromotion:
            {
                var pct = p.GetProperty("discountPercent").GetDecimal();
                if (pct > _settings.MaxDiscountPercent)
                {
                    await _journal.TryTransitionAsync(tenantId, rec.Id, AiActionStatus.Executing, AiActionStatus.PendingConfirmation, ct);
                    return new AiToolResult("discount_limit_exceeded", true);
                }
                var prop = await _promoSuggest.ProposeAsync(tenantId, p.GetProperty("goal").GetString()!, pct, p.GetProperty("text").GetString()!, ct);
                await _journal.UpdateAsync(rec with { Status = AiActionStatus.Done, Target = $"promotion_proposal:{prop.ProposalId}" }, ct);
                return new AiToolResult($"promotion_proposed:{prop.ProposalId}", false, AiActionStatus.Done);
            }
            case AiToolNames.DraftReply:
            {
                await _drafts.SendAsync(tenantId, Guid.Parse(p.GetProperty("draftId").GetString()!), ct);
                await _journal.UpdateAsync(rec with { Status = AiActionStatus.Done }, ct);
                return new AiToolResult("reply_sent", false, AiActionStatus.Done);
            }
            default:
                await _journal.TryTransitionAsync(tenantId, rec.Id, AiActionStatus.Executing, AiActionStatus.PendingConfirmation, ct);
                return new AiToolResult("action_not_confirmable", true);
        }
    }

    public async Task<AiToolResult> RejectPendingAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || rec.Status != AiActionStatus.PendingConfirmation) return new AiToolResult("action_not_pending", true);
        // CAS: reject проти паралельного approve/reject — переможе один.
        if (!await _journal.TryTransitionAsync(tenantId, actionId, AiActionStatus.PendingConfirmation, AiActionStatus.Rejected, ct))
            return new AiToolResult("action_not_pending", true);
        return new AiToolResult("rejected", false, AiActionStatus.Rejected);
    }

    public async Task<AiToolResult> RevertAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || !rec.Revertible || rec.Status != AiActionStatus.Done || !rec.Target.StartsWith("appointment:"))
            return new AiToolResult("not_revertible", true);
        // CAS done -> reverting: двічі паралельний revert скасовує запис один раз.
        if (!await _journal.TryTransitionAsync(tenantId, actionId, AiActionStatus.Done, AiActionStatus.Reverting, ct))
            return new AiToolResult("not_revertible", true);
        try
        {
            await _appointments.CancelAsync(tenantId, Guid.Parse(rec.Target["appointment:".Length..]), ct);
        }
        catch
        {
            await _journal.TryTransitionAsync(tenantId, actionId, AiActionStatus.Reverting, AiActionStatus.Done, CancellationToken.None);
            throw;
        }
        await _journal.UpdateAsync(rec with { Status = AiActionStatus.Reverted, Revertible = false }, ct);
        return new AiToolResult("reverted", false, AiActionStatus.Reverted);
    }

    // ---- tools ----
    private async Task<AiToolResult> FindSlotsAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        Guid? staff = i.TryGetProperty("staffId", out var s) && s.ValueKind == JsonValueKind.String ? Guid.Parse(s.GetString()!) : null;
        var res = await _slots.FindAsync(ctx.TenantId, Guid.Parse(i.GetProperty("serviceId").GetString()!), staff,
            DateOnly.Parse(i.GetProperty("date").GetString()!), ct);
        return await ReadAsync(ctx, AiToolNames.FindFreeSlots, "slots", res, ct);
    }

    private async Task<AiToolResult> CreateAppointmentAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        // clientId береться з контексту розмови, а не від моделі.
        if (ctx.ClientId is null) return await RejectAsync(ctx, AiToolNames.CreateAppointment, "-", "client_required", ct);
        var serviceId = Guid.Parse(i.GetProperty("serviceId").GetString()!);
        var staffId = Guid.Parse(i.GetProperty("staffId").GetString()!);
        var start = DateTimeOffset.Parse(i.GetProperty("start").GetString()!);
        var payload = JsonSerializer.Serialize(new { clientId = ctx.ClientId, serviceId, staffId, start });
        var target = $"client:{ctx.ClientId}";

        switch (ctx.Mode)
        {
            case AiMode.Auto:
            {
                var created = await _appointments.CreateAsync(ctx.TenantId, ctx.ClientId.Value, serviceId, staffId, start, ct);
                await LogAsync(ctx, AiToolNames.CreateAppointment, $"appointment:{created.AppointmentId}", AiActionStatus.Done, true, payload, ct);
                return new AiToolResult($"appointment_created:{created.AppointmentId}", false, AiActionStatus.Done);
            }
            case AiMode.Confirm:
            {
                var id = await LogAsync(ctx, AiToolNames.CreateAppointment, target, AiActionStatus.PendingConfirmation, false, payload, ct);
                return new AiToolResult($"pending_confirmation:{id}. Запис ще НЕ створено; чекає підтвердження людини.", false, AiActionStatus.PendingConfirmation);
            }
            default:
                await LogAsync(ctx, AiToolNames.CreateAppointment, target, AiActionStatus.Drafted, false, payload, ct);
                return new AiToolResult("drafted_only. Запис не створюється в режимі suggest; запропонуй людині.", false, AiActionStatus.Drafted);
        }
    }

    private async Task<AiToolResult> ClientContextAsync(AiToolContext ctx, CancellationToken ct)
    {
        if (ctx.ClientId is null) return await RejectAsync(ctx, AiToolNames.GetClientContext, "-", "client_required", ct);
        var c = await _clients.GetAsync(ctx.TenantId, ctx.ClientId.Value, ct);
        return await ReadAsync(ctx, AiToolNames.GetClientContext, $"client:{ctx.ClientId}", c, ct);
    }

    private async Task<AiToolResult> DraftReplyAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        if (ctx.ClientId is null) return await RejectAsync(ctx, AiToolNames.DraftReply, "-", "client_required", ct);
        var text = i.GetProperty("text").GetString() ?? "";
        var draftId = await _drafts.SaveDraftAsync(ctx.TenantId, ctx.ClientId.Value, text, ct);
        var payload = JsonSerializer.Serialize(new { draftId, text });
        var target = $"client:{ctx.ClientId}";
        if (ctx.Mode == AiMode.Auto)
        {
            await _drafts.SendAsync(ctx.TenantId, draftId, ct);
            await LogAsync(ctx, AiToolNames.DraftReply, target, AiActionStatus.Done, false, payload, ct);
            return new AiToolResult("reply_sent", false, AiActionStatus.Done);
        }
        var status = ctx.Mode == AiMode.Confirm ? AiActionStatus.PendingConfirmation : AiActionStatus.Drafted;
        await LogAsync(ctx, AiToolNames.DraftReply, target, status, false, payload, ct);
        return new AiToolResult($"draft_saved:{draftId}. Надсилає людина.", false, status);
    }

    private async Task<AiToolResult> SuggestPromotionAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        var goal = i.GetProperty("goal").GetString() ?? "";
        var pct = i.GetProperty("discountPercent").GetDecimal();
        var text = i.GetProperty("text").GetString() ?? "";
        if (pct < 0 || pct > _settings.MaxDiscountPercent)
            return await RejectAsync(ctx, AiToolNames.SuggestPromotion, goal, $"discount_limit_exceeded:max={_settings.MaxDiscountPercent}", ct);
        if (i.TryGetProperty("sendAt", out var sa) && sa.ValueKind == JsonValueKind.String
            && !IsBroadcastHourAllowed(DateTimeOffset.Parse(sa.GetString()!)))
            return await RejectAsync(ctx, AiToolNames.SuggestPromotion, goal,
                $"broadcast_hour_not_allowed:{_settings.BroadcastFromHour}-{_settings.BroadcastToHour}", ct);

        var payload = JsonSerializer.Serialize(new { goal, discountPercent = pct, text });
        if (ctx.Mode == AiMode.Auto)
        {
            var prop = await _promoSuggest.ProposeAsync(ctx.TenantId, goal, pct, text, ct);
            await LogAsync(ctx, AiToolNames.SuggestPromotion, $"promotion_proposal:{prop.ProposalId}", AiActionStatus.Done, false, payload, ct);
            return new AiToolResult($"promotion_proposed:{prop.ProposalId}", false, AiActionStatus.Done);
        }
        var status = ctx.Mode == AiMode.Confirm ? AiActionStatus.PendingConfirmation : AiActionStatus.Drafted;
        await LogAsync(ctx, AiToolNames.SuggestPromotion, goal, status, false, payload, ct);
        return new AiToolResult($"promotion_{status}", false, status);
    }

    private async Task<AiToolResult> BuildAudienceAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        var segment = i.GetProperty("segment").GetString() ?? "";
        var members = await _audience.BuildAsync(ctx.TenantId, segment, ct);
        // Розсилки лише зі згодою й без відписки: фільтр у коді, незалежно від порту й моделі.
        var allowed = members.Where(m => m.MarketingConsent && !m.Unsubscribed).ToList();
        return await ReadAsync(ctx, AiToolNames.BuildAudience, $"segment:{segment}", new { count = allowed.Count, clientIds = allowed.Select(m => m.ClientId) }, ct);
    }

    // ---- журнал ----
    private async Task<AiToolResult> ReadAsync<T>(AiToolContext ctx, string tool, string target, T data, CancellationToken ct)
    {
        await LogAsync(ctx, tool, target, AiActionStatus.Done, false, "{}", ct);
        return new AiToolResult(JsonSerializer.Serialize(data), false, AiActionStatus.Done);
    }

    private async Task<AiToolResult> RejectAsync(AiToolContext ctx, string tool, string target, string reason, CancellationToken ct)
    {
        if (ctx.Turn is not null) ctx.Turn.ToolErrors++;
        await LogAsync(ctx, tool, target, AiActionStatus.Rejected, false, JsonSerializer.Serialize(new { reason }), ct);
        return new AiToolResult(reason, true, AiActionStatus.Rejected);
    }

    private async Task<Guid> LogAsync(AiToolContext ctx, string action, string target, string status, bool revertible, string payload, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await _journal.AppendAsync(new AiActionRecord(id, ctx.TenantId, action, target, _time.GetUtcNow(), status, revertible, ctx.Mode, payload, ctx.ConversationId), ct);
        return id;
    }
}
