using System.Security.Cryptography;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Application.Features.BeautyPublicBooking;
using BeautyCrm.Application.Features.BeautyReminders;
using Microsoft.Extensions.DependencyInjection;

namespace BeautyCrm.Application.Features.BeautyChatBooking;

/// <summary>
/// Двоетапний запис з чату (§21.6). Етап 1 <see cref="PrepareAsync"/> — для tool моделі <c>prepare_booking</c>: сервіс лише
/// зберігає пропозицію й повертає дані підсумку, запису не створює. Етап 2 <see cref="ConfirmAsync"/> — НЕ tool: його викликає
/// конвеєр після перевіреного кодом «Так» клієнта. Правила слотів, горизонту, масок і ідемпотентності - ті самі, що в публічному записі
/// (<see cref="OnlineBookingCore"/>). Логування свідомо немає: тут проходить ім'я клієнта.
/// </summary>
public sealed class ChatBookingService(
    IChatBookingStore chat, IPublicBookingStore pub, IBookingStore bookings, OnlineBookingCore core,
    ChatBookingOptions options, TimeProvider clock, IReminderContextStore? reminderContext = null)
{
    // ---------- етап 1 ----------

    public async Task<Result<ChatPrepared>> PrepareAsync(ChatPrepareRequest req, CancellationToken ct)
    {
        if (await chat.GetConversationAsync(req.ConversationId, ct) is not { } conversation)
            return Error.NotFound("conversation_not_found", "Conversation not found.");

        var now = clock.GetUtcNow();
        // Існування закладу/послуги/майстра — під RLS tenant-а; майстер і послуга — лише придатні до запису (як у публічному каталозі).
        var location = (await pub.ListActiveLocationsAsync(now, ct)).FirstOrDefault(l => l.Id == req.LocationId);
        if (location is null) return Error.NotFound("location_not_found", "Location not found.");
        var service = (await pub.ListServicesAsync(req.LocationId, null, ct)).FirstOrDefault(s => s.Id == req.ServiceId);
        if (service is null) return Error.NotFound("service_not_found", "Service not found.");
        var specialist = (await pub.ListSpecialistsAsync(req.LocationId, req.ServiceId, ct)).FirstOrDefault(s => s.Id == req.SpecialistId);
        if (specialist is null) return Error.NotFound("specialist_not_found", "Specialist not found.");

        if (core.ValidateStart(req.StartsAt, now) is not null) return Error.Validation("invalid_start", "startsAt must be one of the offered slots.");

        // Слот мусить бути серед тих, що віддає find_slots (GetSlots на локальну дату закладу).
        var date = SlotCalculator.LocalDate(req.StartsAt, location.Timezone);
        var slots = await core.GetSlotsAsync(req.LocationId, req.SpecialistId, req.ServiceId, date, ct);
        if (!slots.IsOk)
            return slots.Error!.Code == "invalid_date" ? Error.Validation("invalid_start", "startsAt must be one of the offered slots.") : slots.Error;
        if (!slots.Value!.Any(s => s.SpecialistId == req.SpecialistId && s.StartsAt.UtcTicks == req.StartsAt.UtcTicks))
            return Error.Validation("slot_not_offered", "The slot is not available.");

        var quote = PromotionPricing.Quote(service.Price, req.LocationId, req.ServiceId, now, await bookings.GetPromotionRulesAsync(ct));
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // 128 біт
        var proposal = await chat.CreateProposalAsync(new NewChatProposal(
            Guid.NewGuid(), conversation.Id, conversation.ClientId, req.LocationId, req.SpecialistId, req.ServiceId, req.StartsAt.ToUniversalTime(),
            service.DurationMinutes, quote.Original, quote.Final, quote.PromotionId, PublicTokenService.Hash(token),
            now.AddMinutes(options.ProposalTtlMinutes), now), ct);

        var local = TimeZoneInfo.ConvertTime(req.StartsAt, TimeZoneInfo.FindSystemTimeZoneById(location.Timezone));
        var summary = new ChatSummary(service.Name, specialist.Name, location.Name, location.Address, DateOnly.FromDateTime(local.DateTime),
            TimeOnly.FromDateTime(local.DateTime), location.Timezone, service.DurationMinutes, quote.Original, quote.Final, quote.PromotionName);
        return new ChatPrepared(proposal.Id, summary, proposal.ExpiresAt, token);
    }

    // ---------- допоміжні переходи для конвеєра (TASK-728) ----------

    public Task<ChatProposal?> FindActiveAsync(Guid conversationId, CancellationToken ct) => chat.FindActiveProposalAsync(conversationId, ct);

    /// <summary>Підсумок показано клієнту: prepared -> shown (summary_message_id — повідомлення з T-summary).</summary>
    public Task<bool> MarkShownAsync(Guid conversationId, Guid proposalId, Guid summaryMessageId, CancellationToken ct) =>
        chat.MarkShownAsync(conversationId, proposalId, summaryMessageId, ct);

    /// <summary>Клієнт відмовився: shown -> rejected.</summary>
    public Task<bool> DeclineAsync(Guid conversationId, Guid proposalId, CancellationToken ct) =>
        chat.TryTransitionAsync(conversationId, proposalId, ChatProposalStatus.Shown, ChatProposalStatus.Rejected, null, clock.GetUtcNow(), ct);

    /// <summary>Клієнт відповів інакше: активна пропозиція -> superseded (нове «Так» лише на новий підсумок).</summary>
    public Task<int> SupersedeAsync(Guid conversationId, CancellationToken ct) => chat.SupersedeActiveAsync(conversationId, clock.GetUtcNow(), ct);

    /// <summary>«Так» після TTL: shown -> expired.</summary>
    public Task<bool> ExpireAsync(Guid conversationId, Guid proposalId, CancellationToken ct) =>
        chat.TryTransitionAsync(conversationId, proposalId, ChatProposalStatus.Shown, ChatProposalStatus.Expired, null, clock.GetUtcNow(), ct);

    // ---------- етап 2 ----------

    /// <summary>
    /// Створює запис за показаною пропозицією цієї розмови. Помилки (коди для шаблонів): proposal_not_found, proposal_not_shown,
    /// proposal_expired, proposal_not_active, slot_unavailable, price_changed, booking_limit_reached, slot_in_past, invalid_start,
    /// confirmation_in_progress, confirmation_required.
    /// </summary>
    public async Task<Result<ChatConfirmed>> ConfirmAsync(ChatConfirmRequest req, CancellationToken ct)
    {
        if (!req.ClientConfirmed) return Error.Validation("confirmation_required", "The client has not confirmed.");
        if (await chat.GetProposalAsync(req.ConversationId, req.ProposalId, ct) is not { } p
            || (req.ConfirmationToken is not null && !TokenMatches(req.ConfirmationToken, p.TokenHash)))
            return Error.NotFound("proposal_not_found", "Proposal not found.");

        var now = clock.GetUtcNow();
        switch (p.Status)
        {
            case ChatProposalStatus.Confirmed: return await ReplayAsync(p, req.Profile, now, ct);
            case ChatProposalStatus.Shown: break;
            case ChatProposalStatus.Prepared: return Error.Conflict("proposal_not_shown", "The summary was not shown to the client.");
            case ChatProposalStatus.Expired: return Error.Conflict("proposal_expired", "The proposal has expired.");
            default: return Error.Conflict("proposal_not_active", "The proposal is no longer active.");
        }

        if (now >= p.ExpiresAt)
        {
            await chat.TryTransitionAsync(p.ConversationId, p.Id, ChatProposalStatus.Shown, ChatProposalStatus.Expired, null, now, ct);
            return Error.Conflict("proposal_expired", "The proposal has expired.");
        }

        // CAS shown -> confirmed: переможець один.
        if (!await chat.TryTransitionAsync(p.ConversationId, p.Id, ChatProposalStatus.Shown, ChatProposalStatus.Confirmed, null, now, ct))
        {
            var current = await chat.GetProposalAsync(p.ConversationId, p.Id, ct);
            return current is { Status: ChatProposalStatus.Confirmed }
                ? await ReplayAsync(current, req.Profile, now, ct)
                : Error.Conflict("proposal_not_active", "The proposal is no longer active.");
        }
        return await ExecuteAsync(p, req.Profile, now, ct);
    }

    /// <summary>Пропозиція вже confirmed: віддаємо наявний запис; якщо запису ще немає — чекаємо переможця або (після падіння) перезапускаємо.</summary>
    private async Task<Result<ChatConfirmed>> ReplayAsync(ChatProposal p, TelegramProfile? profile, DateTimeOffset now, CancellationToken ct)
    {
        var current = p;
        for (var i = 0; i < 5; i++)
        {
            if (current.AppointmentId is { } id)
                return await bookings.GetAppointmentAsync(id, ct) is { } appt
                    ? await ConfirmedAsync(appt, true, ct)
                    : Error.Conflict("confirmation_in_progress", "The booking is being processed.");
            if (await core.FindReplayAsync(KeyHash(current.Id), ct) is { } hit) return await FinishReplayAsync(current, hit, now, ct);
            if (i + 1 < 5) await Task.Delay(100, ct);
            current = await chat.GetProposalAsync(p.ConversationId, p.Id, ct) ?? current;
            if (current.Status == ChatProposalStatus.Failed) return Error.Conflict(current.FailureCode ?? "proposal_not_active", "The booking failed.");
        }
        // Переможець так і не завершив (падіння між CAS і записом): повтор безпечний — ідемпотентний ключ не дасть дубль.
        return current.DecidedAt is { } at && now - at >= options.StaleConfirmAfter
            ? await ExecuteAsync(current, profile, now, ct)
            : Error.Conflict("confirmation_in_progress", "The booking is being processed.");
    }

    private async Task<Result<ChatConfirmed>> ExecuteAsync(ChatProposal p, TelegramProfile? profile, DateTimeOffset now, CancellationToken ct)
    {
        var keyHash = KeyHash(p.Id);
        if (await core.FindReplayAsync(keyHash, ct) is { } hit) return await FinishReplayAsync(p, hit, now, ct);
        if (core.ValidateStart(p.StartsAt, now) is { } startError) return await FailAsync(p, startError, now, ct);

        // Клієнт створюється лише тут (після «Так»); у розмови вже може бути client_id.
        var clientId = await chat.EnsureClientAsync(p.ConversationId, ChatClientName.Build(profile), ct);
        if (core.LimitReached(await chat.CountActiveByClientAsync(clientId, now, ct), await chat.CountRecentByClientAsync(clientId, now.AddHours(-1), ct)))
            return await FailAsync(p, Error.Validation("booking_limit_reached", "Too many active bookings. Try again later."), now, ct);

        var create = new CreateAppointmentRequest(p.LocationId, p.SpecialistId, p.ServiceId, p.StartsAt,
            new ClientInput(clientId, null, null, null), null, "cash", "telegram", p.PriceFinal); // reminder не задано -> за вподобанням клієнта (§22.4)
        var opts = new PublicCreateOptions(Guid.NewGuid(), null, keyHash, RequestHash(p));
        var outcome = await core.CreateAsync(create, opts, ct);
        if (outcome.Replay is { } raced) return await FinishReplayAsync(p, raced, now, ct);
        if (outcome.Error is { } error) return await FailAsync(p, error, now, ct);

        var appt = outcome.Appointment!;
        await chat.CompleteAsync(p.ConversationId, p.Id, appt.Id, appt.ClientId, now, ct);
        return await ConfirmedAsync(appt, false, ct);
    }

    /// <summary>
    /// Запис за вподобанням клієнта (§22.4): немає вподобання -> план none (нічого не створено) і ознака «потрібен вибір»
    /// для конвеєра (§22.7). Без порту вподобань вибір вважається потрібним.
    /// </summary>
    private async Task<ChatConfirmed> ConfirmedAsync(AppointmentDto appt, bool already, CancellationToken ct) =>
        new(appt, already, !await new ReminderPlanning(reminderContext, clock).HasPreferenceAsync(appt.ClientId, ct));

    private async Task<Result<ChatConfirmed>> FinishReplayAsync(ChatProposal p, IdempotencyHit hit, DateTimeOffset now, CancellationToken ct)
    {
        if (hit.RequestHash != RequestHash(p)) return Error.Validation("idempotency_key_reused", "Idempotency key conflict.");
        if (await bookings.GetAppointmentAsync(hit.AppointmentId, ct) is not { } appt)
            return Error.Conflict("confirmation_in_progress", "The booking is being processed.");
        await chat.CompleteAsync(p.ConversationId, p.Id, appt.Id, appt.ClientId, now, ct);
        return await ConfirmedAsync(appt, true, ct);
    }

    private async Task<Result<ChatConfirmed>> FailAsync(ChatProposal p, Error error, DateTimeOffset now, CancellationToken ct)
    {
        await chat.TryTransitionAsync(p.ConversationId, p.Id, ChatProposalStatus.Confirmed, ChatProposalStatus.Failed, error.Code, now, ct);
        return error;
    }

    // ---------- ключі ----------

    /// <summary>idempotency_key_hash = SHA-256("chat:" + proposalId) (§21.6).</summary>
    private static string KeyHash(Guid proposalId) => PublicTokenService.Hash("chat:" + proposalId);

    private static string RequestHash(ChatProposal p) =>
        PublicTokenService.Hash(string.Join('|', "chat", p.LocationId, p.SpecialistId, p.ServiceId, p.StartsAt.UtcTicks, p.PriceFinal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));

    private static bool TokenMatches(string token, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(PublicTokenService.Hash(token)), System.Text.Encoding.ASCII.GetBytes(expectedHash));
}

public static class ChatBookingServiceExtensions
{
    /// <summary>
    /// Реєструє <see cref="ChatBookingService"/>. Потребує <see cref="IChatBookingStore"/> (EF-реалізація - після TASK-724) та
    /// AddBeautyPublicBookingApplication; тому до появи сховища в DI API не викликається.
    /// </summary>
    public static IServiceCollection AddBeautyChatBookingApplication(this IServiceCollection services, ChatBookingOptions? options = null)
    {
        services.AddSingleton(options ?? new ChatBookingOptions());
        services.AddScoped<ChatBookingService>();
        return services;
    }
}
