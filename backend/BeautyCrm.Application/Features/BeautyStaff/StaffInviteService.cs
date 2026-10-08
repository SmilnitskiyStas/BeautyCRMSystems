using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyStaff;

/// <summary>Запрошення працівника на вхід поверх існуючого потоку invites (UserAdminService). Окремий сервіс, бо залежить від auth-портів.</summary>
public sealed class StaffInviteService(IStaffStore store, UserAdminService users)
{
    /// <summary>Запрошення працівника на вхід: існуючий потік invites (role=specialist, specialist_id профілю).</summary>
    public async Task<AuthResult<InviteCreatedDto>> InviteAsync(Actor actor, Guid id, InviteSpecialistRequest req, CancellationToken ct)
    {
        if (!StaffRoles.IsManager(actor)) return AuthError.Forbidden("forbidden", "Only owner or admin can invite staff.");
        var link = await store.GetSpecialistLinkAsync(id, ct);
        if (link is null) return AuthError.NotFound("specialist_not_found", "Specialist not found.");
        if (!StaffRoles.CanModify(actor, link.LinkedUserRole)) return AuthError.Forbidden("forbidden_role", "You cannot manage this specialist.");
        if (!link.IsActive) return AuthError.Conflict("specialist_inactive", "The specialist profile is deactivated; reactivate it first.");
        return await users.CreateInviteAsync(actor, new CreateInviteRequest(req.Email, Roles.Specialist, id), ct);
    }

}
