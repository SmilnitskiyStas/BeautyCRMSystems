using System.Text.Json;
using BeautyCrm.Application.Features.BeautyReminders;
using BeautyCrm.Infrastructure.AI.Beauty.Adapters;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Entities;
using BeautyCrm.Infrastructure.Data.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.AI.Beauty.Conversations;

public enum ReminderApplyOutcome { Applied, CallUnavailable, AppointmentInactive, NoClient, ConversationNotFound }

/// <summary>Оформлений результат вибору: <see cref="Reply"/> - готовий шаблон клієнту (T-reminder-set / T-reminder-call-unavailable / T-appointment-inactive).</summary>
public sealed record ReminderApplyResult(ReminderApplyOutcome Outcome, string Reply, ReminderChoice? Choice = null)
{
    public bool Applied => Outcome == ReminderApplyOutcome.Applied;
}

/// <param name="Replanned">Скільки майбутніх записів перепланувало.</param>
/// <param name="NoClient">Клієнта ще немає: поставлено <c>reminders_stop_at</c>, вподобання створить лінивий клієнт.</param>
public sealed record ReminderStopResult(string Reply, int Replanned, bool NoClient);

/// <summary>Спільне: транзакція (поточна або власна), стан pending_prompt, журнал. Сервіси виконуються в транзакції відповіді конвеєра.</summary>
public abstract class ReminderChatServiceBase(BeautyDbContext db, TenantContext tenant, IAiActionJournal journal, AiTenantRuntime runtime)
{
    protected BeautyDbContext Db => db;

    /// <summary>Виконує роботу в поточній транзакції контексту (її комітить виклик) або у власній: commit лише при <paramref name="commitIf"/>.</summary>
    protected async Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<T, bool> commitIf, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await work();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var result = await work();
        if (commitIf(result)) await tx.CommitAsync(ct); else await tx.RollbackAsync(ct);
        db.ChangeTracker.Clear();
        return result;
    }

    protected Task ClearPromptAsync(Guid conversationId, CancellationToken ct) =>
        db.Conversations.Where(c => c.Id == conversationId && c.PendingPrompt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PendingPrompt, (string?)null)
                .SetProperty(c => c.PendingPromptAppointmentId, (Guid?)null).SetProperty(c => c.PendingPromptExpiresAt, (DateTimeOffset?)null), ct);

    protected async Task AppendJournalAsync(Guid conversationId, string tool, string target, object payload, DateTimeOffset now, CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new TenantScopeMissingException();
        var mode = await runtime.GetModeAsync(tenantId, ct);
        await journal.AppendAsync(new AiActionRecord(Guid.NewGuid(), tenantId, tool, target, now, AiActionStatus.Done, false, mode,
            JsonSerializer.Serialize(payload), conversationId), ct);
    }

    /// <summary>Upsert вподобання клієнта (§22.3); telegram_conversation_id = розмова, з якої прийшов вибір.</summary>
    protected async Task UpsertPreferenceAsync(Guid clientId, Guid conversationId, ReminderChoice choice, string source, DateTimeOffset now, CancellationToken ct)
    {
        var pref = await db.ClientContactPreferences.FirstOrDefaultAsync(p => p.ClientId == clientId, ct);
        if (pref is null)
        {
            pref = new ClientContactPreference { ClientId = clientId, CreatedAt = now };
            db.ClientContactPreferences.Add(pref);
        }
        pref.Method = choice.Method;
        pref.OffsetsMinutes = choice.Offsets.ToArray();
        pref.Source = source;
        pref.TelegramConversationId = conversationId;
        pref.UpdatedByUserId = null;
        pref.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Застосування вибору нагадування клієнтом (§22.7, §23.16): upsert вподобання (<c>source</c>), переплан запису через
/// <see cref="IReminderReplanStore.ReplanAppointmentAsync"/>, <c>pending_prompt=NULL</c>, журнал <c>reminder_choice</c>. Викликає конвеєр
/// (текст, TASK-740) і TASK-753 (кнопка <c>rc</c>). Якщо транзакція контексту вже відкрита, працює в ній; при результаті, що не
/// <see cref="ReminderApplyResult.Applied"/>, власну транзакцію відкочує, а в чужій викликач має відкотити сам.
/// </summary>
public sealed class ReminderChoiceApplier(BeautyDbContext db, TenantContext tenant, IAiActionJournal journal, AiTenantRuntime runtime,
    IReminderReplanStore replan, IReminderContextStore context, TimeProvider clock) : ReminderChatServiceBase(db, tenant, journal, runtime)
{
    /// <summary>Дзвінок-нагадування доступний: у клієнта є телефон і дзвінки ввімкнено в налаштуваннях tenant (§22.7).</summary>
    public async Task<bool> CallAvailableAsync(Guid? clientId, CancellationToken ct)
    {
        if (clientId is not { } id) return false;
        var phone = await Db.Clients.AsNoTracking().Where(c => c.Id == id && c.DeletedAt == null).Select(c => c.Phone).FirstOrDefaultAsync(ct);
        return !string.IsNullOrWhiteSpace(phone) && (await context.GetSettingsAsync(ct)).CallEnabled;
    }

    public Task<ReminderApplyResult> ApplyAsync(Guid conversationId, Guid appointmentId, ReminderChoice choice, string source, CancellationToken ct,
        string via = "text") =>
        InTransactionAsync(() => ApplyCoreAsync(conversationId, appointmentId, choice, source, via, ct), r => r.Applied, ct);

    private async Task<ReminderApplyResult> ApplyCoreAsync(Guid conversationId, Guid appointmentId, ReminderChoice choice, string source, string via, CancellationToken ct)
    {
        var conv = await Db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => new { c.ClientId }).FirstOrDefaultAsync(ct);
        if (conv is null) return new ReminderApplyResult(ReminderApplyOutcome.ConversationNotFound, ChatTemplates.Fallback);
        if (conv.ClientId is not { } clientId) return new ReminderApplyResult(ReminderApplyOutcome.NoClient, ChatTemplates.ReminderAppointmentInactive);

        var now = clock.GetUtcNow();
        // Запис мусить належати клієнту розмови (IDOR), бути активним і майбутнім.
        var appt = await Db.Appointments.AsNoTracking().Where(a => a.Id == appointmentId && a.ClientId == clientId)
            .Select(a => new { a.Status, a.StartsAt }).FirstOrDefaultAsync(ct);
        if (appt is null || appt.StartsAt <= now || appt.Status is not (AppointmentStatus.Pending or AppointmentStatus.Confirmed))
        {
            await ClearPromptAsync(conversationId, ct);
            return new ReminderApplyResult(ReminderApplyOutcome.AppointmentInactive, ChatTemplates.ReminderAppointmentInactive);
        }
        if (choice.Method == ReminderMethods.Call && !await CallAvailableAsync(clientId, ct))
            return new ReminderApplyResult(ReminderApplyOutcome.CallUnavailable, ChatTemplates.ReminderCallUnavailable);

        await UpsertPreferenceAsync(clientId, conversationId, choice, "chat", now, ct);
        if (!await replan.ReplanAppointmentAsync(appointmentId, choice.Method, choice.Offsets, now, ct))
            return new ReminderApplyResult(ReminderApplyOutcome.AppointmentInactive, ChatTemplates.ReminderAppointmentInactive);
        await ClearPromptAsync(conversationId, ct);
        await AppendJournalAsync(conversationId, ChatJournal.ReminderChoice, $"appointment:{appointmentId}",
            new { appointmentId, method = choice.Method, offsets = choice.Offsets, source, via }, now, ct);
        Db.ChangeTracker.Clear();
        return new ReminderApplyResult(ReminderApplyOutcome.Applied, ChatTemplates.ReminderSet(choice), choice);
    }
}

/// <summary>
/// «Стоп» (§22.7, ADR-012 §2.5): вподобання <c>none</c> (<c>source=stop</c>), переплан майбутніх записів
/// (<see cref="IReminderReplanStore.ReplanUpcomingAsync"/>, ≤ <see cref="ChatAssistantOptions.MaxUpcomingReplan"/>), <c>beauty_clients.unsubscribed=true</c>,
/// pending_prompt зняте, журнал <c>reminder_stop</c>. Клієнта ще немає - <c>reminders_stop_at=now</c> (лінивий клієнт створюється з none/stop).
/// Ідемпотентний. Викликає конвеєр (текст) і TASK-753 (<c>/stop</c>, <c>i:stop</c>).
/// </summary>
public sealed class ReminderStopHandler(BeautyDbContext db, TenantContext tenant, IAiActionJournal journal, AiTenantRuntime runtime,
    IReminderReplanStore replan, ChatAssistantOptions opts, TimeProvider clock, BeautyCrm.Infrastructure.Data.Beauty.MarketingConsentService? marketing = null)
    : ReminderChatServiceBase(db, tenant, journal, runtime)
{
    public Task<ReminderStopResult> StopAsync(Guid conversationId, CancellationToken ct, string via = "text") =>
        InTransactionAsync(() => StopCoreAsync(conversationId, via, ct), _ => true, ct);

    private async Task<ReminderStopResult> StopCoreAsync(Guid conversationId, string via, CancellationToken ct)
    {
        var conv = await Db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => new { c.ClientId }).FirstOrDefaultAsync(ct);
        var now = clock.GetUtcNow();
        if (conv is null) return new ReminderStopResult(ChatTemplates.Stop, 0, true);

        var replanned = 0;
        if (conv.ClientId is not { } clientId)
        {
            await Db.Conversations.Where(c => c.Id == conversationId && c.RemindersStopAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RemindersStopAt, (DateTimeOffset?)now), ct);
        }
        else
        {
            await UpsertPreferenceAsync(clientId, conversationId, ReminderChoice.None, "stop", now, ct);
            await Db.Clients.Where(c => c.Id == clientId && c.DeletedAt == null && !c.Unsubscribed)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Unsubscribed, true), ct);
            replanned = await replan.ReplanUpcomingAsync(clientId, ReminderMethods.None, [], opts.MaxUpcomingReplan, now, ct);
            // TASK-782 (§26.4, §26.9.2): «Стоп» вимикає й маркетинг - none/stop для всіх каналів (працює й за Marketing:Enabled=false)
            if (marketing is not null) await marketing.StopAsync(clientId, via is "button" or "command" ? via : "text", conversationId, ct);
        }
        await ClearPromptAsync(conversationId, ct);
        await AppendJournalAsync(conversationId, ChatJournal.ReminderStop, $"conversation:{conversationId}", new { replanned, via }, now, ct);
        Db.ChangeTracker.Clear();
        return new ReminderStopResult(ChatTemplates.Stop, replanned, conv.ClientId is null);
    }
}
