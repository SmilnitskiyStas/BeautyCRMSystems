using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>Результат перевірки доступу до запису: scope перегляду (404) і scope керування (403 forbidden_role, §30.6.2).</summary>
public enum AppointmentAccess { Allowed, NotFound, ManageForbidden }

/// <summary>
/// ADR-023 (§30.2, §30.6.2): scope календаря за <c>calendar.view</c>/<c>calendar.manage</c> (<c>own</c> = записи власного
/// <c>specialist_id</c>; неактивний профіль або відсутня прив'язка дають порожню вибірку). Чужий запис поза scope перегляду - 404
/// (існування не розкривається); видимий, але поза scope керування - 403 forbidden_role {calendar.manage, all}.
/// </summary>
public sealed class AppointmentAccessService(BookingService booking)
{
    /// <summary>Власний профіль для own-scope: без прив'язки або з неактивним профілем - <see cref="Guid.Empty"/> (порожня вибірка).</summary>
    private static Guid OwnOrEmpty(Actor actor) =>
        actor.Access.SpecialistInactive ? Guid.Empty : actor.SpecialistId ?? Guid.Empty;

    /// <summary>
    /// <c>calendar.view = all</c>: як запитано; інакше (own) - завжди власний профіль (навіть без профілю: порожня вибірка).
    /// </summary>
    public static Guid? EffectiveSpecialistId(Actor actor, Guid? requested) =>
        actor.Access.AtLeast(PermissionKeys.CalendarView, PermissionLevels.All) ? requested : OwnOrEmpty(actor);

    /// <summary><c>calendar.manage = all</c> - будь-кому; <c>own</c> - лише власний профіль.</summary>
    public static bool CanCreateFor(Actor actor, Guid specialistId) =>
        actor.Access.AtLeast(PermissionKeys.CalendarManage, PermissionLevels.All)
        || (actor.Access.AtLeast(PermissionKeys.CalendarManage, PermissionLevels.Own) && OwnOrEmpty(actor) == specialistId && specialistId != Guid.Empty);

    /// <summary>Чи бачить актор і чи може змінювати запис. Неіснуючий запис = <see cref="AppointmentAccess.Allowed"/> (404 дасть сервіс).</summary>
    public async Task<AppointmentAccess> CheckAsync(Actor actor, Guid appointmentId, bool manage, CancellationToken ct)
    {
        var viewAll = actor.Access.AtLeast(PermissionKeys.CalendarView, PermissionLevels.All);
        var manageAll = actor.Access.AtLeast(PermissionKeys.CalendarManage, PermissionLevels.All);
        if (viewAll && (!manage || manageAll)) return AppointmentAccess.Allowed;

        var r = await booking.GetAsync(appointmentId, ct);
        if (!r.IsOk) return viewAll ? AppointmentAccess.Allowed : AppointmentAccess.NotFound; // чужий/неіснуючий: 404 без розкриття
        var own = OwnOrEmpty(actor);
        var isOwn = own != Guid.Empty && r.Value!.SpecialistId == own;
        if (!viewAll && !isOwn) return AppointmentAccess.NotFound;
        if (manage && !manageAll && !isOwn) return AppointmentAccess.ManageForbidden;
        return AppointmentAccess.Allowed;
    }

    /// <summary>Перегляд запису (деталь): поза scope - false.</summary>
    public async Task<bool> CanAccessAsync(Actor actor, Guid appointmentId, CancellationToken ct) =>
        await CheckAsync(actor, appointmentId, manage: false, ct) == AppointmentAccess.Allowed;
}
