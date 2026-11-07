using BeautyCrm.Application.Features.BeautyChatFlow;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Integrations.Channels;
using BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

// TASK-837 (ADR-020, §28.9): транзієнтна геолокація клієнта на кроці закладу. Координати живуть ЛИШЕ в пам'яті процесу
// (ConversationSignals: TTL, видалення при читанні) і лише як аргументи haversine-сортування; ні БД, ні логів, ні API, ні LLM.
// Кроки без LLM (як і ядро потоку): цей файл не залежить від шару ШІ.
public sealed partial class ConversationTurnProcessor
{
    /// <summary>Закладів для гео — від 2 до 8: усі мають вміститись в options однієї клавіатури (index однією цифрою).</summary>
    private const int GeoMinLocations = 2, GeoMaxLocations = 8;

    private static bool GeoKeyboardOpen(Conversation c) =>
        c.ReplyKeyboardOpenAt is not null && c.PendingPrompt != ReminderChoiceMenu.PhonePromptKind;

    /// <summary>§28.9 «Коли пропонуємо»: крок закладу ∧ GeoEnabled ∧ 2..8 закладів ∧ ≥ 1 із координатами ∧ немає питання про номер ∧ reply-клавіатура не відкрита.</summary>
    private bool GeoOfferApplicable(Turn t, FlowResult? r) =>
        FlowOpts is { GeoEnabled: true }
        && r is { Next.Status: FlowStatuses.Active, View: { Step: FlowSteps.Location, AllLocations: { } all } }
        && all.Count is >= GeoMinLocations and <= GeoMaxLocations && all.Any(l => l.HasCoordinates)
        && t.Conv.ReplyKeyboardOpenAt is null && t.Conv.PendingPrompt != ReminderChoiceMenu.PhonePromptKind;

    /// <summary>T-flow-geo-offer: reply-клавіатура [📍 Поділитись локацією] / [Обрати зі списку] окремим повідомленням (ключ <c>…:g</c>).</summary>
    private static Reply GeoOfferReply() =>
        new(FlowTemplates.GeoOffer, MessageSenderType.System, ActionsJson: ChatReplyKeyboard.LocationRequest().ToActionsJson(), KeySuffix: ":g");

    /// <summary>
    /// Вибір закладу за відкритої гео-клавіатури: окреме коротке «Обрано: заклад …» з remove_keyboard перед наступним кроком (ключ <c>…:r</c>).
    /// null - клавіатура не відкрита, інша дія або крок не перейшов далі.
    /// </summary>
    private static Reply? GeoChosenPrefix(Conversation conv, string verb, FlowResult r) =>
        verb == FlowVerbs.Location && GeoKeyboardOpen(conv) && r is { Code: FlowResultCodes.Ok, View.Names: { Location: { } place } names }
            ? new Reply(FlowTemplates.ChosenLines([new FlowChosen("location", place, names.Address)]), MessageSenderType.System,
                ActionsJson: ChatActionLayout.RemoveReplyJson, KeySuffix: ":r")
            : null;

    /// <summary>Координати з пам'яті цього повідомлення (читання видаляє запис); решта у пакеті відкидаються.</summary>
    private void DiscardLocations(IEnumerable<Message> batch)
    {
        foreach (var m in batch.Where(m => m.Kind == InboundKinds.Location)) signals.TakeLocation(m.ConversationId, m.Id);
    }

    /// <summary>
    /// kind=location (крок 4f): пересортувати заклади за відстанню (haversine), якщо потік на кроці закладу. Немає координат у пам'яті
    /// (рестарт, інший інстанс, TTL) -> T-location-retry + той самий список (нова версія) + remove_keyboard. Потоку/кроку немає ->
    /// T-location-unused (раз на 10 хв). Потоку взагалі немає в конфігурації -> як раніше (T-text-only). У suggest/handoff - без відповіді.
    /// </summary>
    /// <returns>true - хід оброблено тут.</returns>
    private async Task<bool> TryHandleLocationAsync(Turn t, CancellationToken ct)
    {
        var locations = t.Batch.Where(m => m.Kind == InboundKinds.Location).ToList();
        if (locations.Count == 0) return false;
        var conv = t.Conv;
        if (locations.Count < t.Batch.Count) // змішаний пакет: геолокація не потрібна, текст піде далі
        {
            DiscardLocations(locations);
            return false;
        }
        var point = signals.TakeLocation(conv.Id, locations[^1].Id);
        DiscardLocations(locations.Take(locations.Count - 1));
        if (!FlowAvailable(conv)) // потоку немає: поведінка до ADR-020
        {
            await HandleNonTextAsync(t, ct);
            return true;
        }
        if (!t.AutoReplies)
        {
            await SetStatusAsync(t.Batch, Processed, ct);
            return true;
        }
        // M1 (§28.6): геолокація рахується в ліміті натискань; понад ліміт - T-rate-limited не частіше разу на хвилину, решта тиша
        if (!CallbackLimiter.TryAcquire(conv.Id))
        {
            Reply? limited = !await RecentTemplateAsync(conv.Id, ChatTemplates.RateLimited, TimeSpan.FromMinutes(1), t.Now, ct)
                ? new Reply(ChatTemplates.RateLimited, MessageSenderType.System) : null;
            await CommitManyAsync(t, new CommitSpec(limited is null ? [] : [limited]) { InboundStatus = Skipped }, ct);
            return true;
        }
        var flow = await Flows.GetActiveAsync(conv.Id, ct);
        if (flow is null || flow.Step != FlowSteps.Location || t.Now >= flow.ExpiresAt)
        {
            FlowLog(t, flow, "location", "geo_unused");
            if (await RecentTemplateAsync(conv.Id, FlowTemplates.LocationUnused, StaleThrottle, t.Now, ct))
                await SetStatusAsync(t.Batch, Processed, ct);
            else
                await CommitAsync(t, new Reply(FlowTemplates.LocationUnused, MessageSenderType.System), null, null, ct);
            return true;
        }

        var r = await Flows.ReshowAsync(flow, FlowCtx(conv), null, ct);
        var sortable = r is { IsStep: true, View: { Step: FlowSteps.Location, AllLocations: { } all } }
                       && all.Count is >= GeoMinLocations and <= GeoMaxLocations && all.Any(l => l.HasCoordinates);
        IReadOnlyList<Reply> replies;
        string result;
        if (point is { } p && sortable)
        {
            var next = r.Next!;
            // Збережена версія (body, actions, options) - порядок назви, без відстаней; відстані лише в override для inline-відправки.
            var plan = KeyboardBuilder.FlowLocationNear(r.View!, next.Id, next.Version, next.ExpiresAt, p.Latitude, p.Longitude);
            replies =
            [
                new Reply(FlowTemplates.LocationThanks, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson, KeySuffix: ":r"),
                new Reply(FlowTemplates.LocationNear, MessageSenderType.System, Keyboard: plan),
            ];
            result = "geo_sorted";
        }
        else
        {
            replies =
            [
                new Reply(FlowTemplates.LocationRetry, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson, KeySuffix: ":r"),
                FlowReply(r, t.Now),
            ];
            result = "geo_missing";
        }
        FlowLog(t, flow, "location", result);
        await CommitManyAsync(t, FlowSpec(t, null, flow, r, replies, consume: false, geoOffer: false), ct);
        return true;
    }

    /// <summary>«Обрати зі списку» за відкритої гео-клавіатури: T-flow-geo-declined + remove_keyboard, список і потік не змінюються (§28.9 «відмова»).</summary>
    private async Task<bool> HandleGeoDeclineTextAsync(Turn t, List<Message> texts, CancellationToken ct)
    {
        var conv = t.Conv;
        if (t.Batch.Count != 1 || texts.Count != 1 || !GeoKeyboardOpen(conv) || FlowOpts is not { GeoEnabled: true } || !FlowAvailable(conv)) return false;
        if (NormalizeChoice(texts[0].Body) != NormalizeChoice(ChatReplyKeyboard.DeclineLocationLabel)) return false;
        var flow = await Flows.GetActiveAsync(conv.Id, ct);
        if (flow is null || flow.Step != FlowSteps.Location) return false;
        if (!t.AutoReplies)
        {
            await SetStatusAsync(t.Batch, Processed, ct);
            return true;
        }
        FlowLog(t, flow, "text", "geo_declined");
        await CommitManyAsync(t, new CommitSpec([new Reply(FlowTemplates.GeoDeclined, MessageSenderType.System, ActionsJson: ChatActionLayout.RemoveReplyJson)]), ct);
        return true;
    }
}
