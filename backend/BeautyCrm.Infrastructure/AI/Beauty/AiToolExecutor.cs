using System.Text.Json;

namespace BeautyCrm.Infrastructure.AI.Beauty;

public enum AiScope { Client, Manager }

public sealed record AiToolContext(Guid TenantId, Guid? ClientId, AiMode Mode, AiScope Scope);
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

    public static IReadOnlyList<AiToolDefinition> For(AiScope scope) =>
        scope == AiScope.Manager ? All : All.Where(t => !AiToolNames.ManagerOnly.Contains(t.Name)).ToList();
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

    public AiToolExecutor(
        IFindFreeSlotsPort slots, ICreateAppointmentPort appointments, IGetPricesPort prices,
        IGetActivePromotionsPort promos, IGetClientContextPort clients, IDraftReplyPort drafts,
        ISuggestPromotionPort promoSuggest, IBuildAudiencePort audience, IAiActionJournal journal,
        AiSettings settings, TimeProvider time)
    {
        _slots = slots; _appointments = appointments; _prices = prices; _promos = promos; _clients = clients;
        _drafts = drafts; _promoSuggest = promoSuggest; _audience = audience; _journal = journal;
        _settings = settings; _time = time;
    }

    public bool IsBroadcastHourAllowed(DateTimeOffset sendAt) =>
        sendAt.Hour >= _settings.BroadcastFromHour && sendAt.Hour < _settings.BroadcastToHour;

    public async Task<AiToolResult> ExecuteAsync(AiToolContext ctx, string tool, JsonElement input, CancellationToken ct)
    {
        try
        {
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
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or InvalidOperationException or JsonException)
        {
            return await RejectAsync(ctx, tool, "-", "invalid_arguments", ct);
        }
    }

    // ---- підтвердження / скасування людиною ----
    public async Task<AiToolResult> ConfirmAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || rec.Status != AiActionStatus.PendingConfirmation)
            return new AiToolResult("action_not_pending", true);

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
                if (pct > _settings.MaxDiscountPercent) return new AiToolResult("discount_limit_exceeded", true);
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
                return new AiToolResult("action_not_confirmable", true);
        }
    }

    public async Task<AiToolResult> RejectPendingAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || rec.Status != AiActionStatus.PendingConfirmation) return new AiToolResult("action_not_pending", true);
        await _journal.UpdateAsync(rec with { Status = AiActionStatus.Rejected }, ct);
        return new AiToolResult("rejected", false, AiActionStatus.Rejected);
    }

    public async Task<AiToolResult> RevertAsync(Guid tenantId, Guid actionId, CancellationToken ct)
    {
        var rec = await _journal.GetAsync(tenantId, actionId, ct);
        if (rec is null || !rec.Revertible || rec.Status != AiActionStatus.Done || !rec.Target.StartsWith("appointment:"))
            return new AiToolResult("not_revertible", true);
        await _appointments.CancelAsync(tenantId, Guid.Parse(rec.Target["appointment:".Length..]), ct);
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
        await LogAsync(ctx, tool, target, AiActionStatus.Rejected, false, JsonSerializer.Serialize(new { reason }), ct);
        return new AiToolResult(reason, true, AiActionStatus.Rejected);
    }

    private async Task<Guid> LogAsync(AiToolContext ctx, string action, string target, string status, bool revertible, string payload, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await _journal.AppendAsync(new AiActionRecord(id, ctx.TenantId, action, target, _time.GetUtcNow(), status, revertible, ctx.Mode, payload), ct);
        return id;
    }
}
