using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyStaff;

/// <summary>Значення відсутностей (beauty-contracts.md §13).</summary>
public static class AbsenceValues
{
    public static readonly string[] Types = ["sick", "vacation", "day_off", "other"];
    public const string Requested = "requested";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const int NoteMaxLength = 500;
    public const int MaxSpanDays = 366;
}

/// <summary>Налаштування керування працівниками (секція конфігурації "Staff").</summary>
public sealed class StaffOptions
{
    /// <summary>Максимум відсутностей у статусі requested на одного майстра (інакше 422 too_many_requests).</summary>
    public int MaxRequestedAbsencesPerSpecialist { get; set; } = 10;
}

public static class StaffRoles
{
    /// <summary>ADR-023: <c>staff.access = edit</c> - керування профілями, графіками, відсутностями (було IsManager за роллю).</summary>
    public static bool CanEditStaff(Actor actor) => actor.Access.AtLeast(PermissionKeys.StaffAccess, PermissionLevels.Edit);

    /// <summary>ADR-023: <c>locations.access = edit</c> - керування закладами.</summary>
    public static bool CanEditLocations(Actor actor) => actor.Access.AtLeast(PermissionKeys.LocationsAccess, PermissionLevels.Edit);

    /// <summary>
    /// §30.1 (M2): профіль без користувача змінює будь-хто з потрібним правом; профіль із користувачем - лише актор ВИЩОГО рангу
    /// (owner > admin > deputy > specialist) і не сам цей користувач (власний профіль цим правилом не змінюється).
    /// </summary>
    public static bool CanModify(Actor actor, string? linkedUserRole, Guid? linkedUserId = null) =>
        linkedUserRole is null || (linkedUserId != actor.UserId && Roles.Rank(actor.Role) > Roles.Rank(linkedUserRole));
}

// ---------- запити ----------

public sealed record CreateSpecialistRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(32)] string? Phone,
    [StringLength(200)] string? Position,
    IReadOnlyList<Guid>? LocationIds,
    IReadOnlyList<Guid>? ServiceIds,
    JsonElement? WorkingHours,
    int? PracticeStartYear = null,
    [StringLength(500)] string? Bio = null,
    [StringLength(500)] string? PhotoUrl = null);

/// <summary>
/// Повна заміна name/phone/position; IsActive = null — без змін.
/// Публічний профіль (мобільний потік): PracticeStartYear/Bio/PhotoUrl — null = без змін (старі клієнти не стирають дані);
/// очищення: Bio/PhotoUrl = "" , PracticeStartYear = 0. PhotoUrl — лише https-URL до 500 символів, Bio — до 500,
/// рік — 1950..поточний.
/// </summary>
public sealed record UpdateSpecialistRequest(
    [Required, StringLength(200, MinimumLength = 1)] string Name,
    [StringLength(32)] string? Phone,
    [StringLength(200)] string? Position,
    bool? IsActive,
    int? PracticeStartYear = null,
    [StringLength(500)] string? Bio = null,
    [StringLength(500)] string? PhotoUrl = null);

/// <summary>Частковий патч публічного профілю для порту даних: Set* = true — записати значення (null очищає).</summary>
public sealed record SpecialistProfilePatch(
    bool SetYear, int? PracticeStartYear, bool SetBio, string? Bio, bool SetPhoto, string? PhotoUrl);

public sealed record SetSpecialistServicesRequest([Required] IReadOnlyList<Guid> ServiceIds);

public sealed record SetScheduleRequest(Guid LocationId, JsonElement? WorkingHours);

/// <summary>POST /specialists/{id}/locations: WorkingHours необов'язковий (без нього майстер не бронюється, доки не задано графік).</summary>
public sealed record AssignLocationRequest(Guid LocationId, JsonElement? WorkingHours);

public sealed record InviteSpecialistRequest(
    [Required, EmailAddress, StringLength(320)] string Email,
    IReadOnlyDictionary<string, string>? Permissions = null);

public sealed record CreateAbsenceRequest(
    [Required, StringLength(16)] string Type, DateOnly DateFrom, DateOnly DateTo, [StringLength(500)] string? Note);

// ---------- відповіді ----------

public sealed record SpecialistServiceRef(Guid Id, string Name);

public sealed record SpecialistLocationDto(Guid LocationId, string LocationName, bool IsActive, JsonElement? WorkingHours);

/// <summary>Phone/HasAccount не віддаються ролі specialist (поле відсутнє у JSON).</summary>
public sealed record SpecialistDto(
    Guid Id, string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Phone,
    string? Position, string? PhotoUrl, bool IsActive,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HasAccount,
    IReadOnlyList<SpecialistServiceRef> Services, IReadOnlyList<SpecialistLocationDto> Locations,
    int? PracticeStartYear = null, string? Bio = null);

public sealed record AbsenceConflictDto(Guid AppointmentId, DateTimeOffset StartsAt, string ServiceName);

/// <summary>
/// Note — чутливі дані: заповнюється лише для owner/admin і автора запиту, інакше поле відсутнє у JSON.
/// Conflicts — лише для керівників при створенні/підтвердженні.
/// </summary>
public sealed record AbsenceDto(
    Guid Id, Guid SpecialistId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type, // D7: чужий тип (sick = дані про здоров'я) лише за staff.access = edit
    DateOnly DateFrom, DateOnly DateTo, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Note,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AbsenceConflictDto>? Conflicts = null);

// ---------- дані порту ----------

public sealed record StaffLocationRecord(Guid LocationId, string LocationName, bool IsActive, string? WorkingHoursJson);

public sealed record StaffSpecialistRecord(
    Guid Id, string FullName, string? Phone, string? Position, string? PhotoUrl, bool IsActive, string? LinkedUserRole,
    IReadOnlyList<SpecialistServiceRef> Services, IReadOnlyList<StaffLocationRecord> Locations,
    int? PracticeStartYear = null, string? Bio = null);

/// <summary>Мінімум для перевірок прав: існування, активність, роль прив'язаного користувача (admin не змінює owner).</summary>
public sealed record SpecialistLink(Guid Id, bool IsActive, string? LinkedUserRole, Guid? LinkedUserId = null);

public sealed record NewSpecialist(
    string Name, string? Phone, string? Position, IReadOnlyList<Guid> LocationIds, IReadOnlyList<Guid> ServiceIds, string? WorkingHoursJson,
    int? PracticeStartYear = null, string? Bio = null, string? PhotoUrl = null);

/// <summary>
/// Candidates заповнюється лише записами, що створюють/переводять відсутність у статус зі зверненням до conflicts[]
/// (ConflictWindow): вибірка йде в ТІЙ САМІЙ транзакції під advisory-lock майстра (TASK-696).
/// </summary>
public sealed record AbsenceRecord(
    Guid Id, Guid SpecialistId, string Type, DateOnly DateFrom, DateOnly DateTo, string Status, string? Note,
    Guid? RequestedByUserId, Guid? DecidedByUserId, DateTimeOffset? DecidedAt,
    IReadOnlyList<AppointmentCandidate>? Candidates = null);

/// <summary>Діапазон початків записів [FromUtc, ToUtc), з якого store вибирає кандидатів у conflicts[].</summary>
public sealed record ConflictWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc);

public enum AbsenceAddOutcome { Ok, Overlap, TooManyRequests }

public sealed record AbsenceAddResult(AbsenceAddOutcome Outcome, AbsenceRecord? Record);

public sealed record NewAbsence(
    Guid SpecialistId, string Type, DateOnly DateFrom, DateOnly DateTo, string Status, string? Note,
    Guid RequestedByUserId, Guid? DecidedByUserId, DateTimeOffset? DecidedAt);

/// <summary>Активний запис майстра з часовою зоною закладу (для розрахунку конфліктів за локальними днями).</summary>
public sealed record AppointmentCandidate(Guid AppointmentId, DateTimeOffset StartsAt, string ServiceName, string Timezone);
