using BeautyCrm.Application.Features.BeautyAuth;

namespace BeautyCrm.Application.Features.BeautyInventory;

/// <summary>Scope закладів за правом складу (§32.2): all = усі; own = заклади активного профілю майстра; інакше порожньо.</summary>
internal sealed record InventoryScope(bool All, IReadOnlySet<Guid> Ids)
{
    public bool Contains(Guid id) => All || Ids.Contains(id);
    /// <summary>null = без обмеження (all); інакше список закладів scope.</summary>
    public IReadOnlyList<Guid>? LocationFilter => All ? null : [.. Ids];

    public static async Task<InventoryScope> ResolveAsync(Actor actor, string key,
        Func<Guid, CancellationToken, Task<IReadOnlyList<Guid>>> specialistLocations, CancellationToken ct)
    {
        var level = actor.Access.Level(key);
        if (level == PermissionLevels.All) return new InventoryScope(true, new HashSet<Guid>());
        if (level != PermissionLevels.Own || actor.SpecialistId is not { } sid || actor.Access.SpecialistInactive)
            return new InventoryScope(false, new HashSet<Guid>());
        return new InventoryScope(false, (await specialistLocations(sid, ct)).ToHashSet());
    }
}
