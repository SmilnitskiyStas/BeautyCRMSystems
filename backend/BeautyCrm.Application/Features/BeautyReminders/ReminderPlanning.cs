using BeautyCrm.Application.Features.BeautyBooking;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyReminders;

/// <summary>Явний план з тіла запиту staff (§22.6.1): <c>reminders: {method: none|telegram|call, offsetsMinutes: int[]}</c>.</summary>
public sealed record ReminderPlanRequest(string? Method, int[]? OffsetsMinutes);

/// <summary>Вподобання клієнта (§22.3): відсутність = «не обрано» (це не те саме, що method=none).</summary>
public sealed record ClientReminderPreference(string Method, IReadOnlyList<short> Offsets);

/// <param name="ClientFound">false - клієнта за переданим id немає (помилку віддасть ResolveClient; перевірки 409 пропускаються).</param>
/// <param name="TelegramAvailable">У клієнта є розмова в активному Telegram-каналі.</param>
public sealed record ReminderClientContext(bool PhoneAvailable, bool TelegramAvailable, ClientReminderPreference? Preference, bool ClientFound = true);

/// <summary>Порт читання вхідних планувальника: налаштування tenant і стан клієнта. Реалізація - EF (під RLS tenant-а).</summary>
public interface IReminderContextStore
{
    Task<ReminderSettings> GetSettingsAsync(CancellationToken ct);
    /// <summary>Клієнт за id або за телефоном (нового клієнта ще немає: лише PhoneAvailable за введеним номером).</summary>
    Task<ReminderClientContext> GetClientContextAsync(ClientInput client, CancellationToken ct);
}

/// <summary>
/// Переплан наявних записів за новим способом/точками (§22.4 «Переплан»: зміна вподобання з applyToUpcoming, «Стоп», вибір у чаті).
/// Викликається всередині транзакції виклику: якщо в контексті вже відкрита транзакція (upsert вподобання), використовується вона,
/// інакше відкривається власна. Для кожного запису: план на записі + дзеркало reminder_option оновлюються, scheduled-рядки й планові
/// skipped -> cancelled:replanned, відкриті задачі reason=reminder -> cancelled:replanned, нові точки пишуться за ReminderPlanner.
/// </summary>
public interface IReminderReplanStore
{
    /// <returns>false, якщо запису немає, він не pending|confirmed або вже в минулому (нічого не змінено).</returns>
    Task<bool> ReplanAppointmentAsync(Guid appointmentId, string method, IReadOnlyList<short> offsets, DateTimeOffset now, CancellationToken ct);

    /// <summary>Майбутні pending|confirmed записи клієнта, найближчі першими, не більше <paramref name="max"/>; повертає кількість переплановних.</summary>
    Task<int> ReplanUpcomingAsync(Guid clientId, string method, IReadOnlyList<short> offsets, int max, DateTimeOffset now, CancellationToken ct);
}

/// <summary>Підготовлений намір: що саме планувати (до відомих часу й зони закладу).</summary>
public sealed record ReminderIntent(string Method, IReadOnlyList<short> Offsets, ReminderSettings Settings, bool PhoneAvailable);

/// <summary>
/// Визначає план запису з вхідних даних (§22.4 «Джерело плану»): явні <c>reminders</c> staff -> спадкове <c>reminder</c> (auto) ->
/// вподобання клієнта -> none. Валідація <c>reminders</c> - на межі запиту. Решту (рядки в БД) пише store в транзакції запису.
/// </summary>
public sealed class ReminderPlanning(IReminderContextStore? store, TimeProvider clock)
{
    public async Task<Result<ReminderIntent>> PrepareForCreateAsync(CreateAppointmentRequest req, CancellationToken ct)
    {
        var settings = store is null ? ReminderSettings.Default : await store.GetSettingsAsync(ct);
        var inputPhone = !string.IsNullOrWhiteSpace(req.Client.Phone);

        if (req.Reminders is { } explicitPlan)
        {
            var parsed = Validate(explicitPlan);
            if (!parsed.IsOk) return parsed.Error!;
            var (method, offsets) = parsed.Value;
            if (method == ReminderMethods.None) return new ReminderIntent(method, offsets, settings, inputPhone);

            var ctx = store is null
                ? new ReminderClientContext(inputPhone, false, null)
                : await store.GetClientContextAsync(req.Client, ct);
            if (ctx.ClientFound) // нема клієнта - помилку client_not_found віддасть ResolveClient
            {
                if (method == ReminderMethods.Telegram)
                {
                    if (!settings.TelegramEnabled) return Error.Conflict("telegram_reminders_disabled", "Telegram reminders are disabled.");
                    if (!ctx.TelegramAvailable) return Error.Conflict("telegram_unavailable", "The client has no Telegram chat with an active channel.");
                }
                else
                {
                    if (!settings.CallEnabled) return Error.Conflict("call_reminders_disabled", "Call reminders are disabled.");
                    if (!ctx.PhoneAvailable) return Error.Conflict("phone_required", "The client has no phone number.");
                }
            }
            return new ReminderIntent(method, offsets, settings, ctx.PhoneAvailable);
        }

        if (req.Reminder is { } legacy)
        {
            var (m, o) = ReminderOffsets.FromLegacy(legacy);
            return new ReminderIntent(m, o, settings, inputPhone);
        }

        // Нічого не передано (чат, staff без полів): вподобання клієнта, а якщо його немає - none.
        if (store is null) return new ReminderIntent(ReminderMethods.None, [], settings, inputPhone);
        var pref = await store.GetClientContextAsync(req.Client, ct);
        return pref.Preference is { } p
            ? new ReminderIntent(p.Method, p.Offsets, settings, pref.PhoneAvailable)
            : new ReminderIntent(ReminderMethods.None, [], settings, pref.PhoneAvailable);
    }

    public PlannedReminders Build(ReminderIntent intent, DateTimeOffset startsAt, string timezone, DateTimeOffset now) =>
        ReminderPlanner.Plan(new ReminderPlanInput(startsAt, timezone, intent.Method, intent.Offsets, intent.Settings, now, intent.PhoneAvailable));

    /// <summary>Перенос: той самий план запису (method + offsets), нові точки від нового часу.</summary>
    public async Task<PlannedReminders> ReplanForMoveAsync(AppointmentDto appt, DateTimeOffset newStart, string timezone, CancellationToken ct)
    {
        var (method, offsets) = EffectivePlan(appt);
        var settings = store is null ? ReminderSettings.Default : await store.GetSettingsAsync(ct);
        var phone = false;
        if (method == ReminderMethods.Call)
            phone = store is not null && (await store.GetClientContextAsync(new ClientInput(appt.ClientId, null, null, null), ct)).PhoneAvailable;
        return ReminderPlanner.Plan(new ReminderPlanInput(newStart, timezone, method, offsets, settings, clock.GetUtcNow(), phone));
    }

    /// <summary>Чи обрав клієнт спосіб нагадування (ознака «потрібен вибір» для чату, TASK-740).</summary>
    public async Task<bool> HasPreferenceAsync(Guid clientId, CancellationToken ct) =>
        store is not null
        && (await store.GetClientContextAsync(new ClientInput(clientId, null, null, null), ct)).Preference is not null;

    /// <summary>План запису: з DTO, а для спадкових DTO без <c>reminders</c> - з дзеркала <c>reminder_option</c>.</summary>
    public static (string Method, short[] Offsets) EffectivePlan(AppointmentDto appt) =>
        appt.Reminders is { } r
            ? (r.Method, r.OffsetsMinutes.Select(o => (short)o).ToArray())
            : ReminderOffsets.FromLegacy(appt.ReminderOption);

    /// <summary>422: invalid_reminder_method / invalid_reminder_offsets (§22.6.1).</summary>
    public static Result<(string Method, short[] Offsets)> Validate(ReminderPlanRequest r)
    {
        if (r.Method is null || Array.IndexOf(ReminderMethods.Requestable, r.Method) < 0)
            return Error.Validation("invalid_reminder_method", "Reminder method must be none, telegram or call.");
        var offsets = ReminderOffsets.TryNormalize(r.OffsetsMinutes);
        var ok = offsets is not null && r.Method switch
        {
            ReminderMethods.None => offsets.Length == 0,
            ReminderMethods.Call => offsets.Length == 1,
            _ => offsets.Length > 0,
        };
        return ok
            ? (r.Method, offsets!)
            : Error.Validation("invalid_reminder_offsets", "Offsets must be a non-empty subset of 60, 120, 1440 (exactly one for call, empty for none).");
    }
}
