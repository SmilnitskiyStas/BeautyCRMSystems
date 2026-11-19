namespace BeautyCrm.Application.Features.BeautyAuth;

/// <summary>
/// Scope клієнтів за <c>clients.view</c> (§30.2, M3): <c>none</c> - жодного; <c>own</c> - клієнти, що мають >= 1 запис із
/// <c>specialist_id</c> власного (активного) профілю, КРІМ скасованих записів, створених самим актором (<c>created_by_user_id</c>);
/// <c>all</c> - усі. Реалізація - Infrastructure (запит під RLS).
/// </summary>
public interface IClientScope
{
    Task<bool> InScopeAsync(Actor actor, Guid clientId, CancellationToken ct);

    /// <summary>M3: id існуючого клієнта, чий телефон збігається з наданим (за цифрами); null - збігу немає.</summary>
    Task<Guid?> MatchByPhoneAsync(string? phone, CancellationToken ct);
}
