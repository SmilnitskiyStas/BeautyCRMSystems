using System.Text.Json;
using System.Text.RegularExpressions;
using BeautyCrm.Application.Features.BeautyAuth;
using BeautyCrm.Application.Features.BeautyCommon;

namespace BeautyCrm.Application.Features.BeautyStaff;

/// <summary>
/// Профілі працівників: список, створення, зміна, призначені послуги, графік, запрошення на вхід.
/// Змінювати можуть лише owner/admin (перевіряється і в контролері політикою, і тут); admin не змінює профіль owner.
/// Логування свідомо немає.
/// </summary>
public sealed partial class StaffService(IStaffStore store, TimeProvider clock)
{
    private static readonly Error Forbidden = Error.Forbidden("forbidden", "Only owner or admin can manage staff.");
    private static readonly Error NotFound = Error.NotFound("specialist_not_found", "Specialist not found.");

    [GeneratedRegex(@"^\+?[0-9 ()\-]{5,32}$")]
    private static partial Regex PhonePattern();

    // ---------- читання ----------

    /// <summary>Довідник для всіх ролей персоналу; phone і hasAccount лише для керівників.</summary>
    public async Task<IReadOnlyList<SpecialistDto>> ListAsync(Actor actor, CancellationToken ct) =>
        (await store.ListSpecialistsAsync(ct)).Select(r => ToDto(r, StaffRoles.CanEditStaff(actor))).ToList();

    public async Task<Result<SpecialistDto>> GetAsync(Actor actor, Guid id, CancellationToken ct) =>
        await store.GetSpecialistAsync(id, ct) is { } r ? ToDto(r, StaffRoles.CanEditStaff(actor)) : NotFound;

    // ---------- зміни ----------

    public async Task<Result<SpecialistDto>> CreateAsync(Actor actor, CreateSpecialistRequest req, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (ValidateProfile(req.Name, req.Phone, req.Position) is { } invalid) return invalid;
        if (ValidatePublicProfile(req.PracticeStartYear, req.Bio, req.PhotoUrl, allowClear: false) is { } invalidPublic) return invalidPublic;

        var locationIds = (req.LocationIds ?? []).Distinct().ToList();
        var serviceIds = (req.ServiceIds ?? []).Distinct().ToList();
        if (locationIds.Count > 100 || serviceIds.Count > 500) return Error.Validation("too_many_items", "Too many locations or services.");
        if ((await store.ExistingLocationIdsAsync(locationIds, ct)).Count != locationIds.Count)
            return Error.Validation("location_not_found", "One or more locations do not exist.");
        if ((await store.ExistingServiceIdsAsync(serviceIds, ct)).Count != serviceIds.Count)
            return Error.Validation("service_not_found", "One or more services do not exist.");

        string? hours = null;
        if (req.WorkingHours is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            if (locationIds.Count == 0) return Error.Validation("location_required", "workingHours requires at least one location.");
            hours = WorkingHoursValidator.TryNormalize(req.WorkingHours);
            if (hours is null) return InvalidHours;
        }

        var id = await store.CreateSpecialistAsync(
            new NewSpecialist(req.Name.Trim(), Clean(req.Phone), Clean(req.Position), locationIds, serviceIds, hours,
                req.PracticeStartYear, Clean(req.Bio), Clean(req.PhotoUrl)), ct);
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    public async Task<Result<SpecialistDto>> UpdateAsync(Actor actor, Guid id, UpdateSpecialistRequest req, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (ValidateProfile(req.Name, req.Phone, req.Position) is { } invalid) return invalid;
        if (ValidatePublicProfile(req.PracticeStartYear, req.Bio, req.PhotoUrl, allowClear: true) is { } invalidPublic) return invalidPublic;
        if (await Guard(actor, id, ct) is { } denied) return denied;
        // M1 (§30.6.4): деактивація профілю з прив'язаним користувачем - це вимкнення користувача: team.manage + ранг (CanManage)
        if (req.IsActive == false && await store.GetSpecialistLinkAsync(id, ct) is { IsActive: true, LinkedUserRole: { } linkedRole })
        {
            if (!actor.Access.AtLeast(PermissionKeys.TeamManage, PermissionLevels.Manage))
                return Error.ForbiddenPermission(PermissionKeys.TeamManage, PermissionLevels.Manage);
            if (!RolePolicy.CanManage(actor.Role, linkedRole)) return Error.Forbidden("forbidden_role", "You cannot manage this specialist.");
        }

        // isActive = false каскадно (в одній транзакції) вимикає прив'язаного користувача, відкликає його refresh-токени й
        // pending-запрошення. Повторна активація доступ НЕ повертає: користувача вмикають явно (PATCH /api/users/{id}/status).
        await store.UpdateSpecialistAsync(id, req.Name.Trim(), Clean(req.Phone), Clean(req.Position), req.IsActive, clock.GetUtcNow(), actor.UserId, ct);
        if (req.PracticeStartYear is not null || req.Bio is not null || req.PhotoUrl is not null)
            await store.UpdateSpecialistProfileAsync(id, new SpecialistProfilePatch(
                req.PracticeStartYear is not null, req.PracticeStartYear is null or 0 ? null : req.PracticeStartYear,
                req.Bio is not null, Clean(req.Bio),
                req.PhotoUrl is not null, Clean(req.PhotoUrl)), ct);
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    public async Task<Result<SpecialistDto>> SetServicesAsync(Actor actor, Guid id, SetSpecialistServicesRequest req, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (await Guard(actor, id, ct) is { } denied) return denied;
        var serviceIds = req.ServiceIds.Distinct().ToList();
        if (serviceIds.Count > 500) return Error.Validation("too_many_items", "Too many services.");
        if ((await store.ExistingServiceIdsAsync(serviceIds, ct)).Count != serviceIds.Count)
            return Error.Validation("service_not_found", "One or more services do not exist.");

        await store.ReplaceServicesAsync(id, serviceIds, ct);
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    public async Task<Result<SpecialistDto>> SetScheduleAsync(Actor actor, Guid id, SetScheduleRequest req, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (await Guard(actor, id, ct) is { } denied) return denied;
        var hours = WorkingHoursValidator.TryNormalize(req.WorkingHours);
        if (hours is null) return InvalidHours;
        if ((await store.ExistingLocationIdsAsync([req.LocationId], ct)).Count != 1)
            return Error.NotFound("location_not_found", "Location not found.");

        await store.SetScheduleAsync(id, req.LocationId, hours, ct);
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    /// <summary>Додати заклад (з графіком, якщо передано). 409 location_already_assigned; 404 location_not_found.</summary>
    public async Task<Result<SpecialistDto>> AssignLocationAsync(Actor actor, Guid id, AssignLocationRequest req, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (await Guard(actor, id, ct) is { } denied) return denied;
        string? hours = null;
        if (req.WorkingHours is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) })
        {
            hours = WorkingHoursValidator.TryNormalize(req.WorkingHours);
            if (hours is null) return InvalidHours;
        }
        if ((await store.ExistingLocationIdsAsync([req.LocationId], ct)).Count != 1)
            return Error.NotFound("location_not_found", "Location not found.");

        if (await store.AssignLocationAsync(id, req.LocationId, hours, ct) == LocationAssignOutcome.AlreadyAssigned)
            return Error.Conflict("location_already_assigned", "The specialist already works at this location.");
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    /// <summary>Прибрати заклад: лише без майбутніх активних записів (409 has_future_appointments); 404 location_not_assigned.</summary>
    public async Task<Result<SpecialistDto>> RemoveLocationAsync(Actor actor, Guid id, Guid locationId, CancellationToken ct)
    {
        if (!StaffRoles.CanEditStaff(actor)) return Forbidden;
        if (await Guard(actor, id, ct) is { } denied) return denied;
        switch (await store.RemoveLocationAsync(id, locationId, clock.GetUtcNow(), ct))
        {
            case LocationRemoveOutcome.NotAssigned:
                return Error.NotFound("location_not_assigned", "The specialist does not work at this location.");
            case LocationRemoveOutcome.HasFutureAppointments:
                return Error.Conflict("has_future_appointments",
                    "The specialist has upcoming appointments at this location; move or cancel them first.");
        }
        return ToDto((await store.GetSpecialistAsync(id, ct))!, true);
    }

    // ---------- допоміжне ----------

    private static readonly Error InvalidHours = Error.Validation("invalid_working_hours",
        "workingHours must look like {\"mon\":[{\"from\":\"09:00\",\"to\":\"18:00\"}]} (keys mon..sun, HH:mm, from < to, no overlaps).");

    /// <summary>404, якщо профілю немає; 403, якщо admin намагається змінити профіль owner/admin.</summary>
    private async Task<Error?> Guard(Actor actor, Guid id, CancellationToken ct)
    {
        var link = await store.GetSpecialistLinkAsync(id, ct);
        if (link is null) return NotFound;
        return StaffRoles.CanModify(actor, link.LinkedUserRole, link.LinkedUserId)
            ? null
            : Error.Forbidden("forbidden_role", "You cannot manage this specialist.");
    }

    private static Error? ValidateProfile(string? name, string? phone, string? position)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n) || n.Length > 200 || n.Any(char.IsControl))
            return Error.Validation("invalid_name", "Name must be 1-200 characters.");
        var p = Clean(phone);
        if (p is not null && !PhonePattern().IsMatch(p))
            return Error.Validation("invalid_phone", "Phone may contain digits, +, spaces, parentheses and hyphens (5-32 chars).");
        var pos = Clean(position);
        if (pos is not null && (pos.Length > 200 || pos.Any(char.IsControl)))
            return Error.Validation("invalid_position", "Position must be at most 200 characters.");
        return null;
    }

    /// <summary>
    /// Публічний профіль: рік 1950..поточний (UTC), bio до 500 символів, photoUrl лише абсолютний https до 500 символів.
    /// allowClear (PUT): рік 0 / порожні рядки означають «очистити».
    /// </summary>
    private Error? ValidatePublicProfile(int? year, string? bio, string? photoUrl, bool allowClear)
    {
        if (year is { } y && !(allowClear && y == 0) && (y < 1950 || y > clock.GetUtcNow().Year))
            return Error.Validation("invalid_practice_start_year", $"practiceStartYear must be between 1950 and {clock.GetUtcNow().Year}.");
        var b = Clean(bio);
        if (b is not null && (b.Length > 500 || b.Any(c => char.IsControl(c) && c is not ('\n' or '\r'))))
            return Error.Validation("invalid_bio", "Bio must be at most 500 characters.");
        var p = Clean(photoUrl);
        if (p is not null && !IsHttpsUrl(p))
            return Error.Validation("invalid_photo_url", "photoUrl must be an absolute https URL of at most 500 characters.");
        return null;
    }

    private static bool IsHttpsUrl(string value) =>
        value.Length <= 500 && !value.Any(char.IsControl)
        && Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host)
        && string.IsNullOrEmpty(u.UserInfo);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static SpecialistDto ToDto(StaffSpecialistRecord r, bool manager) => new(
        r.Id, r.FullName, manager ? r.Phone : null, r.Position, r.PhotoUrl, r.IsActive, manager ? r.LinkedUserRole is not null : null,
        r.Services,
        r.Locations.Select(l => new SpecialistLocationDto(l.LocationId, l.LocationName, l.IsActive, ParseHours(l.WorkingHoursJson))).ToList(),
        r.PracticeStartYear, r.Bio);

    private static JsonElement? ParseHours(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
