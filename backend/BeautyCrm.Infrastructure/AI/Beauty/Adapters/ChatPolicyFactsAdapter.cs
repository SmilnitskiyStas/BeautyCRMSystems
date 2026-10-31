using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Infrastructure.Data;
using BeautyCrm.Infrastructure.Data.Tenancy;
using BeautyCrm.Infrastructure.Integrations.Channels;
using Microsoft.EntityFrameworkCore;

namespace BeautyCrm.Infrastructure.AI.Beauty.Adapters;

/// <summary>
/// Факти політики з БД свого tenant (TASK-741): <c>beauty_reminder_settings</c> (немає рядка = типові), <c>beauty_cancellation_settings</c>,
/// наявність телефону клієнта (сам телефон не читається далі за булеве значення), телефон закладу. Tenant звіряється зі scope
/// (<see cref="TenantGuard"/>), решту обмежує RLS: чужі рядки недоступні навіть при помилці у фільтрі.
/// </summary>
public sealed class ChatPolicyFactsAdapter(BeautyDbContext db, TenantContext tenant, ChatPhoneOptions? phone = null, TimeProvider? clock = null) : IChatPolicyFactsPort
{
    public async Task<ChatPolicyFacts> GetAsync(Guid tenantId, Guid? clientId, Guid? locationId, CancellationToken ct)
    {
        TenantGuard.Ensure(tenant, tenantId);

        var reminders = await db.ReminderSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var cancel = await db.CancellationSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        var hasPhone = false;
        var capture = PhoneCaptureFact.Off;
        if (clientId is { } cid)
        {
            var row = await db.Clients.AsNoTracking().Where(c => c.Id == cid && c.DeletedAt == null)
                .Select(c => new { c.Phone, c.PhoneRequestedAt }).FirstOrDefaultAsync(ct);
            hasPhone = !string.IsNullOrWhiteSpace(row?.Phone);
            // TASK-770 (§25.5.8): лише булеві/кодові стани, без самого номера
            if (phone is { Enabled: true } && row is not null && !hasPhone)
                capture = row.PhoneRequestedAt is { } at && at > (clock ?? TimeProvider.System).GetUtcNow().AddDays(-phone.CooldownDays)
                    ? PhoneCaptureFact.Cooldown : PhoneCaptureFact.Allowed;
        }

        string? locationPhone = null;
        if (locationId is { } lid)
            locationPhone = await db.Locations.AsNoTracking().Where(l => l.Id == lid).Select(l => l.Phone).FirstOrDefaultAsync(ct);

        var settings = cancel is null
            ? CancellationSettings.Defaults
            : new CancellationSettings(cancel.WindowHours, cancel.RefundPercentInWindow, cancel.RefundPercentOutside, cancel.DeductFee, cancel.FeePercent);
        return new ChatPolicyFacts(
            reminders?.TelegramEnabled ?? true, reminders?.CallEnabled ?? true, hasPhone,
            reminders?.QuietStart ?? new TimeOnly(22, 0), reminders?.QuietEnd ?? new TimeOnly(8, 0), settings, locationPhone)
        {
            PhoneCapture = capture,
        };
    }
}
