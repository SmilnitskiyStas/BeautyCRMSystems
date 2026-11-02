using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BeautyCrm.Infrastructure.AI.Beauty;

/// <summary>Результат успішного prepare_booking у цьому ході: пропозиція існує (prepared), але запису ще немає.</summary>
/// <param name="SummaryText">Канонічний підсумок (T-summary), сформований кодом; його додає конвеєр до відповіді.</param>
public sealed record ChatPreparedBooking(
    Guid ProposalId, ChatBookingSummaryDto Summary, DateTimeOffset ExpiresAt, int TtlMinutes, string SummaryText);

/// <summary>Стан одного ходу в scope ClientChat. Заповнює виконавець tools; читає BeautyAssistant і конвеєр.</summary>
public sealed class ChatTurnState
{
    public ChatPreparedBooking? Prepared { get; set; }
    public string? HandoffReason { get; set; }
    public int ToolErrors { get; set; }
    /// <summary>Остання відповідь find_slots цього ходу з вільними слотами (те, що побачила модель). Для коду-fallback при вичерпанні ітерацій (TASK-735).</summary>
    public ChatOfferedSlots? LastSlots { get; set; }
}

/// <param name="Date">Локальна дата закладу, яку шукали.</param>
/// <param name="Labels">Локальні години HH:mm слотів, показаних моделі (за зростанням, унікальні).</param>
/// <param name="Slots">
/// TASK-753: ті самі слоти зі структурою (майстер + момент), з якої КОД будує кнопки `slot_pick`; модель їх не бачить у такому вигляді.
/// Null - слоти невідомі (юніт-тести, старі виклики).
/// </param>
/// <param name="LocationId">Заклад і послуга цього find_slots (для `PrepareAsync` по кнопці).</param>
/// <param name="PreferredTime">Бажаний час із виклику find_slots: кнопки беруться найближчі до нього.</param>
public sealed record ChatOfferedSlots(
    DateOnly Date, string Timezone, IReadOnlyList<string> Labels, IReadOnlyList<ChatOfferedSlot>? Slots = null,
    Guid? LocationId = null, Guid? ServiceId = null, TimeOnly? PreferredTime = null);

public sealed record ChatOfferedSlot(Guid SpecialistId, DateTimeOffset StartsAt);

/// <param name="Prepared">Остання пропозиція цього ходу. Підсумок для клієнта - <see cref="ChatPreparedBooking.SummaryText"/>, не текст моделі.</param>
/// <param name="ModelHandoffReason">Причина, якщо модель викликала request_handoff (хід завершено).</param>
/// <param name="ToolErrors">Кількість відхилених/помилкових tool-викликів за хід (конвеєр: >= 3 = handoff).</param>
/// <param name="DraftOnly">suggest: відповідь не надсилається клієнту, лише чернетка.</param>
/// <param name="LastSlots">TASK-753: останній успішний find_slots цього ходу зі слотами (null - не було або слотів 0); джерело кнопок `slot_pick`.</param>
public sealed record ChatTurnOutcome(
    ChatPreparedBooking? Prepared, string? ModelHandoffReason, int ToolErrors, bool DraftOnly, ChatOfferedSlots? LastSlots = null);

public static class ClientChatToolNames
{
    public const string ListLocations = "list_locations";
    public const string ListServices = "list_services";
    public const string ListSpecialists = "list_specialists";
    public const string FindSlots = "find_slots";
    public const string PrepareBooking = "prepare_booking";
    public const string RequestHandoff = "request_handoff";
    // get_active_promotions спільний з іншими scope: AiToolNames.GetActivePromotions

    /// <summary>Єдиний список дозволених у ClientChat. Усе інше (відоме чи ні) недоступне, навіть за явного виклику моделі.</summary>
    public static readonly string[] Allowed =
    [
        ListLocations, ListServices, ListSpecialists, FindSlots, AiToolNames.GetActivePromotions, PrepareBooking, RequestHandoff,
    ];

    /// <summary>Tools інших scope і ім'я, яке модель могла б «вгадати» (confirm_booking не tool моделі): відповідь tool_not_allowed_in_client_scope.</summary>
    public static readonly string[] KnownForbidden =
    [
        AiToolNames.CreateAppointment, AiToolNames.GetClientContext, AiToolNames.DraftReply, AiToolNames.FindFreeSlots,
        AiToolNames.GetPrices, AiToolNames.SuggestPromotion, AiToolNames.BuildAudience, "confirm_booking",
    ];

    public static readonly string[] HandoffReasons = ["complaint", "unclear", "human_requested", "medical", "policy_unknown", "other"];

    /// <summary>Питання про політику (дзвінки, канали, нагадування, оплата, скасування), відповіді на яке немає в блоку «ФАКТИ ЗАКЛАДУ» (§22.9).</summary>
    public const string PolicyUnknown = "policy_unknown";
}

/// <summary>
/// Tools scope ClientChat (§21.7, ADR-011 §2.4). tenant, conversationId, clientId беруться ЛИШЕ з <see cref="AiToolContext"/>;
/// схеми не мають цих полів, а зайві поля у вході ігноруються. Невідомий id - not_found без розкриття існування.
/// Підсумок для клієнта формує код (<see cref="ChatSummaryText"/>); токен підтвердження до цього шару не доходить.
/// </summary>
public sealed partial class ClientChatToolbox(
    IClientChatPort port, IGetActivePromotionsPort promos, IAiActionJournal journal, TimeProvider time, ChatPromptOptions options)
{
    private const int MaxServices = 100;
    private const int MaxSlots = 20;
    /// <summary>Скільки найближчих до <c>preferredTime</c> слотів повертає find_slots (TASK-735: 4-6 варіантів замість сітки на весь день).</summary>
    private const int PreferredSlots = 6;

    [GeneratedRegex(@"(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex OffsetRx();

    public async Task<AiToolResult> ExecuteAsync(AiToolContext ctx, string tool, JsonElement input, CancellationToken ct)
    {
        if (ctx.Turn is null) return await RejectAsync(ctx, tool, "chat_turn_missing", ct);
        if (!ClientChatToolNames.Allowed.Contains(tool))
            return await RejectAsync(ctx, tool, ClientChatToolNames.KnownForbidden.Contains(tool) ? "tool_not_allowed_in_client_scope" : "unknown_tool", ct);
        if (input.ValueKind is not JsonValueKind.Object and not JsonValueKind.Undefined and not JsonValueKind.Null)
            return await RejectAsync(ctx, tool, "invalid_arguments", ct);

        return tool switch
        {
            ClientChatToolNames.ListLocations => await ListLocationsAsync(ctx, ct),
            ClientChatToolNames.ListServices => await ListServicesAsync(ctx, input, ct),
            ClientChatToolNames.ListSpecialists => await ListSpecialistsAsync(ctx, input, ct),
            ClientChatToolNames.FindSlots => await FindSlotsAsync(ctx, input, ct),
            AiToolNames.GetActivePromotions => await PromotionsAsync(ctx, ct),
            ClientChatToolNames.PrepareBooking => await PrepareAsync(ctx, input, ct),
            _ => await HandoffAsync(ctx, input, ct),
        };
    }

    // ---- читання ----

    private async Task<AiToolResult> ListLocationsAsync(AiToolContext ctx, CancellationToken ct)
    {
        var list = await port.ListLocationsAsync(ctx.TenantId, ct);
        return await OkAsync(ctx, ClientChatToolNames.ListLocations, "locations", list.Select(l => new
        {
            id = l.Id, name = l.Name, address = l.Address, timezone = l.Timezone, workingHours = l.WorkingHours, closedWeekdays = l.ClosedWeekdays,
        }), ct);
    }

    private async Task<AiToolResult> ListServicesAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        var loc = await ResolveLocationAsync(ctx, i, ct);
        if (loc.Error is not null) return await RejectAsync(ctx, ClientChatToolNames.ListServices, loc.Error, ct);
        var r = await port.ListServicesAsync(ctx.TenantId, loc.Id, ct);
        if (!r.IsOk) return await RejectAsync(ctx, ClientChatToolNames.ListServices, r.ErrorCode!, ct);
        return await OkAsync(ctx, ClientChatToolNames.ListServices, "services", r.Value!.Take(MaxServices).Select(s => new
        {
            id = s.Id, name = s.Name, category = s.Category, durationMinutes = s.DurationMinutes,
            priceOriginal = s.PriceOriginal, priceFinal = s.PriceFinal, promotionName = s.PromotionName,
        }), ct);
    }

    private async Task<AiToolResult> ListSpecialistsAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        if (!TryOptionalGuid(i, "serviceId", out var serviceId)) return await RejectAsync(ctx, ClientChatToolNames.ListSpecialists, "invalid_arguments", ct);
        var loc = await ResolveLocationAsync(ctx, i, ct);
        if (loc.Error is not null) return await RejectAsync(ctx, ClientChatToolNames.ListSpecialists, loc.Error, ct);
        var r = await port.ListSpecialistsAsync(ctx.TenantId, loc.Id, serviceId, ct);
        if (!r.IsOk) return await RejectAsync(ctx, ClientChatToolNames.ListSpecialists, r.ErrorCode!, ct);
        return await OkAsync(ctx, ClientChatToolNames.ListSpecialists, "specialists", r.Value!.Select(s => new { id = s.Id, name = s.Name, title = s.Title }), ct);
    }

    private async Task<AiToolResult> FindSlotsAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        const string tool = ClientChatToolNames.FindSlots;
        if (!TryRequiredGuid(i, "serviceId", out var serviceId) || !TryOptionalGuid(i, "specialistId", out var specialistId)
            || !TryDate(i, out var date) || !TryPreferredTime(i, out var preferred))
            return await RejectAsync(ctx, tool, "invalid_arguments", ct);
        var loc = await ResolveLocationAsync(ctx, i, ct);
        if (loc.Error is not null) return await RejectAsync(ctx, tool, loc.Error, ct);

        var r = await port.FindSlotsAsync(ctx.TenantId, loc.Id, serviceId, specialistId, date, ct);
        if (!r.IsOk) return await RejectAsync(ctx, tool, r.ErrorCode!, ct);
        var all = r.Value!.Slots;

        // preferredTime: 4-6 найближчих до бажаного часу слотів (за зростанням часу); без нього - до 20 слотів доби, як раніше.
        IReadOnlyList<ChatSlotDto> shown;
        bool more;
        if (preferred is { } pt)
        {
            shown = all.OrderBy(s => Math.Abs(s.StartsAt.Hour * 60 + s.StartsAt.Minute - (pt.Hour * 60 + pt.Minute))).ThenBy(s => s.StartsAt)
                .ThenBy(s => s.SpecialistId).Take(PreferredSlots).OrderBy(s => s.StartsAt).ThenBy(s => s.SpecialistId).ToList();
            more = all.Count > shown.Count;
        }
        else
        {
            shown = all.Take(MaxSlots).ToList();
            more = all.Count > MaxSlots;
        }

        static string Label(ChatSlotDto s) => s.StartsAt.ToString("HH:mm", CultureInfo.InvariantCulture);
        ctx.Turn!.LastSlots = shown.Count == 0 ? null
            : new ChatOfferedSlots(date, r.Value.Timezone, shown.Select(Label).Distinct().Order().ToList(),
                shown.Select(s => new ChatOfferedSlot(s.SpecialistId, s.StartsAt)).ToList(), loc.Id, serviceId, preferred);

        var data = new
        {
            timezone = r.Value.Timezone,
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            slots = shown.Select(s => new
            {
                specialistId = s.SpecialistId,
                startsAt = s.StartsAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                label = Label(s),
            }),
            totalAvailable = all.Count,
            firstAvailable = all.Count == 0 ? null : Label(all.OrderBy(s => s.StartsAt).First()),
            lastAvailable = all.Count == 0 ? null : Label(all.OrderBy(s => s.StartsAt).Last()),
            requestedTimeAvailable = preferred is { } want ? (bool?)all.Any(s => TimeOnly.FromDateTime(s.StartsAt.DateTime) == want) : null,
            moreAvailable = more,
        };
        return await OkAsync(ctx, tool, "slots", data, ct);
    }

    private async Task<AiToolResult> PromotionsAsync(AiToolContext ctx, CancellationToken ct)
    {
        var list = await promos.GetAsync(ctx.TenantId, ct);
        // Без внутрішніх id; знижки лише з БД (порт читає активні акції).
        return await OkAsync(ctx, AiToolNames.GetActivePromotions, "promotions",
            list.Select(p => new { title = p.Title, discountPercent = p.DiscountPercent, endsAt = p.EndsAt }), ct);
    }

    // ---- етап 1 запису ----

    private async Task<AiToolResult> PrepareAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        const string tool = ClientChatToolNames.PrepareBooking;
        if (ctx.ConversationId is not { } conversationId) return await RejectAsync(ctx, tool, "conversation_required", ct);
        if (!TryRequiredGuid(i, "serviceId", out var serviceId) || !TryRequiredGuid(i, "specialistId", out var specialistId))
            return await RejectAsync(ctx, tool, "invalid_arguments", ct);
        // Час: або startsAt (ISO-8601 з offset, рівно як у find_slots - сувора перевірка), або локальні date+time закладу (TASK-735):
        // код сам ставить offset зони закладу. Слот у будь-якому разі мусить бути серед запропонованих сервером (slot_not_offered).
        var hasStartsAt = i.TryGetProperty("startsAt", out var sa) && sa.ValueKind != JsonValueKind.Null;
        DateTimeOffset startsAt = default;
        if (hasStartsAt
            && (sa.ValueKind != JsonValueKind.String || sa.GetString() is not { } raw || !OffsetRx().IsMatch(raw.Trim())
                || !DateTimeOffset.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out startsAt)))
            return await RejectAsync(ctx, tool, "invalid_start", ct); // потрібен ISO-8601 з offset, як у find_slots
        var loc = await ResolveLocationAsync(ctx, i, ct);
        if (loc.Error is not null) return await RejectAsync(ctx, tool, loc.Error, ct);
        if (!hasStartsAt && !TryLocalStart(i, loc.Timezone, out startsAt))
            return await RejectAsync(ctx, tool, "invalid_start", ct);

        var payload = JsonSerializer.Serialize(new { locationId = loc.Id, serviceId, specialistId, startsAt });

        // suggest: пропозиція не створюється, лише запис «підготовлено» в журналі; клієнту підсумок не показується.
        if (ctx.Mode == AiMode.Suggest)
        {
            await LogAsync(ctx, tool, $"conversation:{conversationId}", AiActionStatus.Drafted, payload, ct);
            return new AiToolResult(Json(new
            {
                status = "drafted",
                note = "Режим чернеток: запис не готується автоматично. Підготуй відповідь, її перегляне адміністратор.",
            }), false, AiActionStatus.Drafted);
        }

        var r = await port.PrepareBookingAsync(ctx.TenantId, conversationId, loc.Id, serviceId, specialistId, startsAt, ct);
        if (!r.IsOk) return await RejectAsync(ctx, tool, r.ErrorCode!, ct);

        var p = r.Value!;
        var ttl = Math.Max(1, (int)Math.Round((p.ExpiresAt - time.GetUtcNow()).TotalMinutes));
        ctx.Turn!.Prepared = new ChatPreparedBooking(p.ProposalId, p.Summary, p.ExpiresAt, ttl,
            ChatSummaryText.Format(p.Summary, ttl, options.CurrencyLabel));
        await LogAsync(ctx, tool, $"proposal:{p.ProposalId}", AiActionStatus.Done, payload, ct);

        // Моделі: лише підсумок. Ні proposalId, ні токена, ні expiresAt.
        var s = p.Summary;
        return new AiToolResult(Json(new
        {
            status = "prepared",
            summary = new
            {
                serviceName = s.ServiceName, specialistName = s.SpecialistName, locationName = s.LocationName, address = s.Address,
                localDate = s.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                localTime = s.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                timezone = s.Timezone, durationMinutes = s.DurationMinutes,
                priceOriginal = s.PriceOriginal, priceFinal = s.PriceFinal, promotionName = s.PromotionName,
            },
            note = "Запис ще НЕ створено. Підсумок покаже система; попроси клієнта відповісти «Так».",
        }), false, AiActionStatus.Done);
    }

    private async Task<AiToolResult> HandoffAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        var reason = i.ValueKind == JsonValueKind.Object && i.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
            && r.GetString() is { } v && ClientChatToolNames.HandoffReasons.Contains(v) ? v : "other";
        ctx.Turn!.HandoffReason = reason;
        await LogAsync(ctx, "handoff", ctx.ConversationId is { } c ? $"conversation:{c}" : "-", AiActionStatus.HandedOff,
            JsonSerializer.Serialize(new { reason, source = "model" }), ct);
        return new AiToolResult(Json(new { status = "handed_off" }), false, AiActionStatus.HandedOff);
    }

    // ---- допоміжне ----

    private readonly record struct LocationRef(Guid Id, string? Timezone, string? Error);

    /// <summary>Заклад: аргумент моделі (має існувати під RLS) -> заклад каналу -> єдиний активний заклад -> location_required.</summary>
    private async Task<LocationRef> ResolveLocationAsync(AiToolContext ctx, JsonElement i, CancellationToken ct)
    {
        if (!TryOptionalGuid(i, "locationId", out var requested)) return new(default, null, "invalid_arguments");
        var locations = await port.ListLocationsAsync(ctx.TenantId, ct);
        if (requested is { } id)
            return locations.FirstOrDefault(l => l.Id == id) is { } r ? new(id, r.Timezone, null) : new(default, null, "location_not_found");
        if (ctx.DefaultLocationId is { } def && locations.FirstOrDefault(l => l.Id == def) is { } d) return new(def, d.Timezone, null);
        return locations.Count == 1
            ? new(locations[0].Id, locations[0].Timezone, null)
            : new(default, null, locations.Count == 0 ? "location_not_found" : "location_required");
    }

    /// <summary>
    /// Локальні <c>date</c> (yyyy-MM-dd) + <c>time</c> (HH:mm) закладу -> момент з offset зони закладу. Неіснуючий локальний час (перехід на
    /// літній) і невідома зона - false. Неоднозначний час (кінець літнього) береться за першим входженням; сервер усе одно звірить зі слотами.
    /// </summary>
    private static bool TryLocalStart(JsonElement i, string? timezone, out DateTimeOffset startsAt)
    {
        startsAt = default;
        if (string.IsNullOrWhiteSpace(timezone) || !TryDate(i, out var date)) return false;
        if (!i.TryGetProperty("time", out var tp) || tp.ValueKind != JsonValueKind.String
            || !TimeOnly.TryParseExact(tp.GetString(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return false;
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            var local = date.ToDateTime(time, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(local)) return false;
            var offset = tz.IsAmbiguousTime(local) ? tz.GetAmbiguousTimeOffsets(local).Max() : tz.GetUtcOffset(local);
            startsAt = new DateTimeOffset(local, offset);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>Відсутнє/null = без побажання (true); присутнє, але не HH:mm - false (invalid_arguments).</summary>
    private static bool TryPreferredTime(JsonElement i, out TimeOnly? value)
    {
        value = null;
        if (i.ValueKind != JsonValueKind.Object || !i.TryGetProperty("preferredTime", out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.String
            || !TimeOnly.TryParseExact(p.GetString(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
        value = t;
        return true;
    }

    private static bool TryRequiredGuid(JsonElement i, string name, out Guid value)
    {
        value = default;
        return i.ValueKind == JsonValueKind.Object && i.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            && Guid.TryParse(p.GetString(), out value);
    }

    /// <summary>Відсутнє/null = null (true); присутнє, але не GUID = false (invalid_arguments).</summary>
    private static bool TryOptionalGuid(JsonElement i, string name, out Guid? value)
    {
        value = null;
        if (i.ValueKind != JsonValueKind.Object || !i.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return true;
        if (p.ValueKind != JsonValueKind.String || !Guid.TryParse(p.GetString(), out var g)) return false;
        value = g;
        return true;
    }

    private static bool TryDate(JsonElement i, out DateOnly date)
    {
        date = default;
        return i.ValueKind == JsonValueKind.Object && i.TryGetProperty("date", out var p) && p.ValueKind == JsonValueKind.String
            && DateOnly.TryParseExact(p.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // Кирилиця без escape-послідовностей (економія токенів); теги-подоби знешкоджує WrapToolResult.
    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(object o) => JsonSerializer.Serialize(o, JsonOpts);

    private async Task<AiToolResult> OkAsync(AiToolContext ctx, string tool, string target, object data, CancellationToken ct)
    {
        await LogAsync(ctx, tool, target, AiActionStatus.Done, "{}", ct);
        return new AiToolResult(Json(data), false, AiActionStatus.Done);
    }

    private async Task<AiToolResult> RejectAsync(AiToolContext ctx, string tool, string reason, CancellationToken ct)
    {
        if (ctx.Turn is not null) ctx.Turn.ToolErrors++;
        await LogAsync(ctx, tool, "-", AiActionStatus.Rejected, JsonSerializer.Serialize(new { reason }), ct);
        return new AiToolResult(reason, true, AiActionStatus.Rejected);
    }

    private Task LogAsync(AiToolContext ctx, string action, string target, string status, string payload, CancellationToken ct) =>
        journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), ctx.TenantId, action, target, time.GetUtcNow(), status, false, ctx.Mode, payload,
            ctx.ConversationId), ct);
}
