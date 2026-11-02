using System.Globalization;
using System.Text.Json;
using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;
using BeautyCrm.Infrastructure.AI.Beauty;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AiStatus = BeautyCrm.Infrastructure.AI.Beauty.AiActionStatus;
using DbAiStatus = BeautyCrm.Infrastructure.Data.Entities.AiActionStatus;

namespace BeautyCrm.Infrastructure.Integrations.Channels.Buttons;

/// <summary>Власний запис клієнта розмови (без ПД інших клієнтів). Час - UTC; зона закладу для локального показу.</summary>
public sealed record ChatAppointmentInfo(
    Guid Id, DateTimeOffset StartsAt, string Timezone, string ServiceName, string SpecialistName, string LocationName,
    DateTimeOffset? ClientConfirmedAt);

public sealed record MyAppointmentsResult(IReadOnlyList<ChatAppointmentInfo> Items, bool NoClient);

/// <summary>Політика скасування (<see cref="CancellationPolicy.Calculate"/>) лише як текст: сам розрахунок і повернення робить <see cref="CancellationService"/>.</summary>
public sealed record CancelPolicyPreview(decimal PaidAmount, int RefundPercent, decimal RefundAmount, int FeePercent, string Text);

public enum ChatCancelOutcome
{
    /// <summary>Перевірку пройдено (екран підтвердження) або запис скасовано.</summary>
    Ok,
    /// <summary>Чужий/неіснуючий/неактивний/розпочатий запис або скасування в чаті вимкнено -> T-cancel-unavailable (без розкриття причини).</summary>
    Unavailable,
    NoClient,
    ConversationNotFound,
    /// <summary>Ліміт скасувань за добу -> handoff <c>cancel_limit</c> + T-cancel-limit.</summary>
    Limit,
    /// <summary>Повернення коштів не вдалося, запис лишився активним -> T-handoff + handoff <c>cancel_failed</c>.</summary>
    RefundFailed,
}

/// <param name="Policy">Для екрана підтвердження (Ok) і для скасованого запису: фактичне повернення.</param>
/// <param name="HandoffReason"><c>cancel_limit</c> | <c>cancel_failed</c> (ознака; стан розмови змінює конвеєр).</param>
public sealed record ChatCancelResult(
    ChatCancelOutcome Outcome, ChatAppointmentInfo? Appointment = null, CancelPolicyPreview? Policy = null, string? HandoffReason = null,
    bool Cancelled = false);

public enum ChatAttendOutcome { Confirmed, AlreadyConfirmed, Unavailable, NoClient }
public sealed record ChatAttendResult(ChatAttendOutcome Outcome, ChatAppointmentInfo? Appointment = null);

/// <summary>Журнал дій клієнта в чаті (<c>beauty_ai_actions</c>).</summary>
public static class ChatButtonJournal
{
    public const string ClientCancel = "client_cancel";
    public const string ClientAttendConfirm = "client_attend_confirm";
    public const string ConfirmBooking = "confirm_booking";
    public const string CancelReason = "Скасовано клієнтом у Telegram";
}

/// <summary>
/// Дії клієнта над ВЛАСНИМИ записами (§23.7 п. 5-8, §23.11): «Мої записи», скасування у два кроки, «Підтверджую візит». Усе в межах
/// tenant (RLS), розмови й <c>conversation.client_id</c>: запис іншого клієнта, неіснуючий або з іншого tenant = <c>Unavailable</c> без
/// розкриття причини. Скасування - через <see cref="CancellationService"/> (<see cref="CancelOrigin.Client"/>, ADR-007: політика й повернення
/// ті самі, що в календарі; <c>ReminderPlanner</c> закриває нагадування й задачі дзвінка). Логи: лише id і коди.
/// </summary>
public sealed class ClientChatAppointments(
    BeautyDbContext db, TenantContext tenant, ChatButtonsOptions opts, CancellationService cancellation, CancellationSettingsService cancelSettings,
    IBookingStore bookings, IAiActionJournal journal, AiTenantRuntime runtime, TimeProvider clock, ILogger<ClientChatAppointments> log)
{
    private sealed record ConvRow(Guid? ClientId);

    private async Task<ConvRow?> ConversationAsync(Guid conversationId, CancellationToken ct) =>
        await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => new ConvRow(c.ClientId)).FirstOrDefaultAsync(ct);

    private IQueryable<Appointment> Own(Guid clientId) => db.Appointments.AsNoTracking().Where(a => a.ClientId == clientId);

    private static ChatAppointmentInfo ToInfo(Guid id, DateTimeOffset startsAt, string tz, string service, string specialist, string location, DateTimeOffset? confirmed) =>
        new(id, startsAt, tz, service, specialist, location, confirmed);

    // ------------------------------------------------------------------ «Мої записи»

    /// <summary>≤ <c>MyAppointmentsMax</c> найближчих <c>pending|confirmed</c> майбутніх записів клієнта розмови (усі заклади tenant). Без клієнта - порожньо.</summary>
    public async Task<MyAppointmentsResult> ListUpcomingAsync(Guid conversationId, CancellationToken ct)
    {
        if (await ConversationAsync(conversationId, ct) is not { ClientId: { } clientId }) return new MyAppointmentsResult([], true);
        var now = clock.GetUtcNow();
        var rows = await Own(clientId).Where(a => a.StartsAt > now && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .OrderBy(a => a.StartsAt).ThenBy(a => a.Id).Take(opts.MyAppointmentsMax)
            .Select(a => new { a.Id, a.StartsAt, Tz = a.Location!.Timezone, Service = a.Service!.Name, Specialist = a.Specialist!.FullName, Location = a.Location!.Name, a.ClientConfirmedAt })
            .ToListAsync(ct);
        return new MyAppointmentsResult(rows.Select(r => ToInfo(r.Id, r.StartsAt, r.Tz, r.Service, r.Specialist, r.Location, r.ClientConfirmedAt)).ToList(), false);
    }

    /// <summary>Власний активний майбутній запис (pending|confirmed, starts_at &gt; now + <paramref name="minLead"/>) або null.</summary>
    private async Task<ChatAppointmentInfo?> OwnActiveAsync(Guid clientId, Guid appointmentId, TimeSpan minLead, CancellationToken ct)
    {
        var threshold = clock.GetUtcNow() + minLead;
        var r = await Own(clientId).Where(a => a.Id == appointmentId && a.StartsAt > threshold
                && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .Select(a => new { a.Id, a.StartsAt, Tz = a.Location!.Timezone, Service = a.Service!.Name, Specialist = a.Specialist!.FullName, Location = a.Location!.Name, a.ClientConfirmedAt })
            .FirstOrDefaultAsync(ct);
        return r is null ? null : ToInfo(r.Id, r.StartsAt, r.Tz, r.Service, r.Specialist, r.Location, r.ClientConfirmedAt);
    }

    /// <summary>Чи належить запис клієнту розмови й активний (для «Змінити час»).</summary>
    public async Task<ChatAppointmentInfo?> GetOwnActiveAsync(Guid conversationId, Guid appointmentId, CancellationToken ct) =>
        await ConversationAsync(conversationId, ct) is { ClientId: { } clientId } ? await OwnActiveAsync(clientId, appointmentId, TimeSpan.Zero, ct) : null;

    // ------------------------------------------------------------------ скасування

    /// <summary>
    /// Крок 1 (екран підтвердження) і передумова кроку 2: <c>ChatCancel:Enabled</c>, ліміт скасувань за добу, власний активний запис із
    /// запасом <c>MinLeadMinutes</c>. Ok несе політику для тексту. Нічого не змінює.
    /// </summary>
    public async Task<ChatCancelResult> CheckCancelAsync(Guid conversationId, Guid appointmentId, bool enforceLimit, CancellationToken ct)
    {
        if (await ConversationAsync(conversationId, ct) is not { } conv) return new ChatCancelResult(ChatCancelOutcome.ConversationNotFound);
        if (conv.ClientId is not { } clientId) return new ChatCancelResult(ChatCancelOutcome.NoClient);
        if (!opts.CancelEnabled) return new ChatCancelResult(ChatCancelOutcome.Unavailable);
        if (enforceLimit && await CountRecentCancelsAsync(clientId, ct) >= opts.CancelMaxPerClientPerDay)
            return new ChatCancelResult(ChatCancelOutcome.Limit, HandoffReason: "cancel_limit");
        if (await OwnActiveAsync(clientId, appointmentId, TimeSpan.FromMinutes(opts.CancelMinLeadMinutes), ct) is not { } appt)
            return new ChatCancelResult(ChatCancelOutcome.Unavailable);
        return new ChatCancelResult(ChatCancelOutcome.Ok, appt, await PolicyAsync(appt, ct));
    }

    /// <summary>
    /// Крок 2: перевірки (<see cref="CheckCancelAsync"/>) і скасування. Для виклику ПІСЛЯ CAS клавіатури використовуйте
    /// <see cref="PerformCancelAsync"/> (перевірки вже зроблено до CAS).
    /// </summary>
    public async Task<ChatCancelResult> CancelAsync(Guid conversationId, Guid appointmentId, string via, CancellationToken ct)
    {
        var check = await CheckCancelAsync(conversationId, appointmentId, enforceLimit: true, ct);
        return check.Outcome == ChatCancelOutcome.Ok ? await PerformCancelAsync(conversationId, appointmentId, via, false, ct) : Logged(check, appointmentId, conversationId);
    }

    /// <summary>
    /// Саме скасування (<see cref="CancelOrigin.Client"/>, причина «Скасовано клієнтом у Telegram») і журнал <c>client_cancel</c>.
    /// <paramref name="replay"/> - повторна обробка того самого натискання: вже скасований клієнтом запис вважається успіхом (без нового журналу).
    /// </summary>
    public async Task<ChatCancelResult> PerformCancelAsync(Guid conversationId, Guid appointmentId, string via, bool replay, CancellationToken ct)
    {
        var info = await db.Appointments.AsNoTracking().Where(a => a.Id == appointmentId)
            .Select(a => new { a.Id, a.StartsAt, Tz = a.Location!.Timezone, Service = a.Service!.Name, Specialist = a.Specialist!.FullName, Location = a.Location!.Name, a.ClientConfirmedAt, a.CancelledByType, a.ClientId })
            .FirstOrDefaultAsync(ct);
        var conv = await ConversationAsync(conversationId, ct);
        // IDOR-захист і тут: запис мусить належати клієнту розмови.
        if (info is null || conv?.ClientId is not { } cid || info.ClientId != cid) return Logged(new ChatCancelResult(ChatCancelOutcome.Unavailable), appointmentId, conversationId);
        var appt = ToInfo(info.Id, info.StartsAt, info.Tz, info.Service, info.Specialist, info.Location, info.ClientConfirmedAt);

        // L2 (TASK-759): ліміт скасувань/добу атомарний. Місце резервується (перевірка + запис журналу) під pg_advisory_xact_lock клієнта ДО
        // скасування; невдале скасування звільняє резерв. Повтор того самого натискання (replay) нового резерву не бере.
        Guid? reservation = null;
        if (!replay)
        {
            reservation = await TryReserveCancelAsync(cid, conversationId, appointmentId, via, ct);
            if (reservation is null)
                return Logged(new ChatCancelResult(ChatCancelOutcome.Limit, appt, HandoffReason: "cancel_limit"), appointmentId, conversationId);
        }

        Result<CancelResult> r;
        try
        {
            r = await cancellation.CancelAsync(appointmentId, ct, CancelOrigin.Client, ChatButtonJournal.CancelReason);
        }
        catch
        {
            if (reservation is { } failedId) await ReleaseReservationAsync(failedId, CancellationToken.None);
            throw;
        }
        if (r.IsOk)
        {
            var c = r.Value!;
            if (reservation is null)
                await AppendJournalAsync(ChatButtonJournal.ClientCancel, appointmentId, conversationId, new { appointmentId, conversationId, via }, ct);
            return Logged(new ChatCancelResult(ChatCancelOutcome.Ok, appt, RefundText(c.RefundAmount, c.RefundPercent, c.FeePercent, c.RefundAmount > 0), Cancelled: true),
                appointmentId, conversationId, "done");
        }
        if (reservation is { } releaseId) await ReleaseReservationAsync(releaseId, ct);
        switch (r.Error!.Code)
        {
            case "already_cancelled" when replay && info.CancelledByType == CancelledByTypes.Client:
                return Logged(new ChatCancelResult(ChatCancelOutcome.Ok, appt, null, Cancelled: true), appointmentId, conversationId, "done");
            case "refund_failed":
                return Logged(new ChatCancelResult(ChatCancelOutcome.RefundFailed, appt, HandoffReason: "cancel_failed"), appointmentId, conversationId, "refund_failed");
            default: // already_cancelled | cannot_cancel | appointment_not_found | invalid_reason
                return Logged(new ChatCancelResult(ChatCancelOutcome.Unavailable, appt), appointmentId, conversationId);
        }
    }

    /// <summary>
    /// Резерв місця під ліміт: у короткій транзакції під advisory-lock клієнта перераховує скасування за 24 год і додає запис
    /// <c>client_cancel</c>. null - ліміт досягнуто. Запис, що не став скасуванням, звільняє <see cref="ReleaseReservationAsync"/>.
    /// </summary>
    private async Task<Guid?> TryReserveCancelAsync(Guid clientId, Guid conversationId, Guid appointmentId, string via, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        var inOwnTx = db.Database.CurrentTransaction is null;
        await using var tx = inOwnTx ? await db.Database.BeginTransactionAsync(ct) : null;
        var key = $"client-cancel:{tenantId}:{clientId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);
        if (await CountRecentCancelsAsync(clientId, ct) >= opts.CancelMaxPerClientPerDay) return null;
        var id = Guid.NewGuid();
        var mode = await runtime.GetModeAsync(tenantId, ct);
        await journal.AppendAsync(new AiActionRecord(id, tenantId, ChatButtonJournal.ClientCancel, $"appointment:{appointmentId}", clock.GetUtcNow(),
            AiStatus.Done, false, mode, JsonSerializer.Serialize(new { appointmentId, conversationId, via }), conversationId), ct);
        if (tx is not null) await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Скасування не відбулося: резерв не рахується в ліміті (колонка status = failed, конверт - reverted).</summary>
    private async Task ReleaseReservationAsync(Guid id, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        var existing = await journal.GetAsync(tenantId, id, ct);
        if (existing is not null) await journal.UpdateAsync(existing with { Status = AiStatus.Reverted }, ct);
        await db.AiActions.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, DbAiStatus.Failed), ct);
    }

    private ChatCancelResult Logged(ChatCancelResult r, Guid appointmentId, Guid conversationId, string? code = null)
    {
        log.LogInformation("client_cancel appointment={Appointment} conversation={Conversation} result={Result}", appointmentId, conversationId,
            code ?? r.Outcome switch
            {
                ChatCancelOutcome.Limit => "limit", ChatCancelOutcome.NoClient => "no_client", ChatCancelOutcome.RefundFailed => "refund_failed",
                ChatCancelOutcome.ConversationNotFound => "conversation_not_found", ChatCancelOutcome.Ok => "ok", _ => "unavailable",
            });
        return r;
    }

    /// <summary>Скільки скасувань у чаті зробив клієнт за 24 год (журнал <c>client_cancel</c> по всіх його розмовах).</summary>
    public Task<int> CountRecentCancelsAsync(Guid clientId, CancellationToken ct)
    {
        var since = clock.GetUtcNow().AddHours(-24);
        return db.AiActions.AsNoTracking().CountAsync(a => a.ToolName == ChatButtonJournal.ClientCancel && a.CreatedAt >= since && a.Status != DbAiStatus.Failed
            && db.Conversations.Any(c => c.Id == a.ConversationId && c.ClientId == clientId), ct);
    }

    private async Task<CancelPolicyPreview> PolicyAsync(ChatAppointmentInfo appt, CancellationToken ct)
    {
        var payment = await bookings.GetPaymentAsync(appt.Id, ct);
        var paid = payment is { Status: "paid" } ? payment.Amount : 0m;
        var calc = CancellationPolicy.Calculate(appt.StartsAt, clock.GetUtcNow(), paid, await cancelSettings.GetAsync(ct));
        return RefundText(calc.Amount, calc.Percent, calc.FeePercent, paid > 0, paid);
    }

    /// <summary>«Оплата в закладі…» без оплати; «Повернемо X% передоплати (сума грн){, утримується комісія N%}.» з оплатою.</summary>
    private static CancelPolicyPreview RefundText(decimal amount, int percent, int fee, bool paidOnline, decimal paid = 0m)
    {
        if (!paidOnline) return new CancelPolicyPreview(0m, percent, 0m, fee, "Оплата в закладі, тож нічого повертати не потрібно.");
        var sum = amount.ToString("0.##", CultureInfo.InvariantCulture);
        var text = $"Повернемо {percent}% передоплати ({sum} грн)" + (fee > 0 ? $", утримується комісія {fee}%." : ".");
        return new CancelPolicyPreview(paid, percent, amount, fee, text);
    }

    // ------------------------------------------------------------------ «Підтверджую візит»

    /// <summary>
    /// <c>UPDATE beauty_appointments SET client_confirmed_at=now() WHERE id AND client_id AND client_confirmed_at IS NULL</c>:
    /// 1 рядок -> Confirmed (+ журнал <c>client_attend_confirm</c>), 0 -> AlreadyConfirmed (без відповіді). Запис - лише власний, активний, майбутній.
    /// </summary>
    public async Task<ChatAttendResult> ConfirmAttendanceAsync(Guid conversationId, Guid appointmentId, string via, CancellationToken ct)
    {
        if (await ConversationAsync(conversationId, ct) is not { ClientId: { } clientId }) return new ChatAttendResult(ChatAttendOutcome.NoClient);
        if (await OwnActiveAsync(clientId, appointmentId, TimeSpan.Zero, ct) is not { } appt) return new ChatAttendResult(ChatAttendOutcome.Unavailable);

        var now = clock.GetUtcNow();
        var n = await db.Appointments.Where(a => a.Id == appointmentId && a.ClientId == clientId && a.ClientConfirmedAt == null
                && (a.Status == AppointmentStatus.Pending || a.Status == AppointmentStatus.Confirmed))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ClientConfirmedAt, (DateTimeOffset?)now).SetProperty(a => a.UpdatedAt, now), ct);
        if (n == 0) return new ChatAttendResult(ChatAttendOutcome.AlreadyConfirmed, appt);
        await AppendJournalAsync(ChatButtonJournal.ClientAttendConfirm, appointmentId, conversationId, new { appointmentId, conversationId, via }, ct);
        return new ChatAttendResult(ChatAttendOutcome.Confirmed, appt with { ClientConfirmedAt = now });
    }

    // ------------------------------------------------------------------ журнал

    private async Task AppendJournalAsync(string tool, Guid appointmentId, Guid conversationId, object payload, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        var mode = await runtime.GetModeAsync(tenantId, ct);
        await journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), tenantId, tool, $"appointment:{appointmentId}", clock.GetUtcNow(),
            AiStatus.Done, false, mode, JsonSerializer.Serialize(payload), conversationId), ct);
    }
}
