using BeautyCrm.Application.Features.BeautyBooking;

namespace BeautyCrm.Application.Features.BeautyStaff;

public enum LocationAssignOutcome { Assigned, AlreadyAssigned }
public enum LocationRemoveOutcome { Removed, NotAssigned, HasFutureAppointments }

/// <summary>Порт даних керування працівниками. Реалізація — Infrastructure (EF Core під RLS tenant).</summary>
public interface IStaffStore
{
    Task<IReadOnlyList<StaffSpecialistRecord>> ListSpecialistsAsync(CancellationToken ct);
    Task<StaffSpecialistRecord?> GetSpecialistAsync(Guid id, CancellationToken ct);
    Task<SpecialistLink?> GetSpecialistLinkAsync(Guid id, CancellationToken ct);
    Task<IReadOnlySet<Guid>> ExistingServiceIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);
    Task<IReadOnlySet<Guid>> ExistingLocationIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<Guid> CreateSpecialistAsync(NewSpecialist specialist, CancellationToken ct);
    /// <summary>
    /// Оновлює профіль. isActive = false виконує в ОДНІЙ транзакції (під advisory-lock майстра): профіль вимкнено; прив'язаного
    /// користувача (крім owner) вимкнено; його refresh-токени відкликано; pending-запрошення з цим specialist_id відкликано.
    /// Повторна активація (true) користувача НЕ вмикає.
    /// </summary>
    Task UpdateSpecialistAsync(Guid id, string name, string? phone, string? position, bool? isActive, DateTimeOffset now, CancellationToken ct);
    /// <summary>Повна заміна переліку призначених послуг.</summary>
    Task ReplaceServicesAsync(Guid specialistId, IReadOnlyCollection<Guid> serviceIds, CancellationToken ct);
    /// <summary>Створює або оновлює графік майстра в закладі.</summary>
    Task SetScheduleAsync(Guid specialistId, Guid locationId, string workingHoursJson, CancellationToken ct);

    /// <summary>
    /// Додає заклад майстру (новий зв'язок або повторна активація неактивного; hoursJson null = графік не змінюється).
    /// AlreadyAssigned — зв'язок уже активний (в т.ч. паралельне створення).
    /// </summary>
    Task<LocationAssignOutcome> AssignLocationAsync(Guid specialistId, Guid locationId, string? workingHoursJson, CancellationToken ct);
    /// <summary>
    /// Прибирає заклад (is_active = false, графік зберігається) атомарним умовним UPDATE: лише якщо немає майбутніх
    /// активних (pending/confirmed) записів майстра в цьому закладі.
    /// </summary>
    Task<LocationRemoveOutcome> RemoveLocationAsync(Guid specialistId, Guid locationId, DateTimeOffset nowUtc, CancellationToken ct);

    Task<AbsenceRecord?> GetAbsenceAsync(Guid id, CancellationToken ct);
    /// <summary>Відсутності, що перетинають [from, to] (включно).</summary>
    Task<IReadOnlyList<AbsenceRecord>> ListAbsencesAsync(DateOnly from, DateOnly to, Guid? specialistId, CancellationToken ct);
    /// <summary>Перетин з відсутністю у статусі requested/approved.</summary>
    Task<bool> HasActiveAbsenceOverlapAsync(Guid specialistId, DateOnly from, DateOnly to, CancellationToken ct);
    /// <summary>
    /// Створює відсутність під advisory-lock майстра в одній транзакції: ліміт активних requested (maxRequested, лише для
    /// статусу requested) -> TooManyRequests; перетин (exclusion constraint) -> Overlap; з window — у Record.Candidates
    /// повертаються майбутні активні записи майстра з початком у вікні (для conflicts[]).
    /// </summary>
    Task<AbsenceAddResult> AddAbsenceAsync(NewAbsence absence, int? maxRequested, ConflictWindow? window, CancellationToken ct);
    /// <summary>
    /// Атомарно (під advisory-lock майстра) змінює статус, лише якщо поточний належить fromStatuses; null — умова не виконана.
    /// by/at: для approved/rejected — decided_by/decided_at; для cancelled — cancelled_by_user_id/cancelled_at.
    /// window (як у AddAbsenceAsync) — кандидати в conflicts[] у тій самій транзакції.
    /// </summary>
    Task<AbsenceRecord?> TransitionAbsenceAsync(
        Guid id, IReadOnlyCollection<string> fromStatuses, string toStatus, Guid? by, DateTimeOffset? at, ConflictWindow? window,
        CancellationToken ct);
}
