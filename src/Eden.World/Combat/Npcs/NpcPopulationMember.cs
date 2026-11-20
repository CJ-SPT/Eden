using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal readonly record struct NpcPopulationMember(
    long Id,
    long EncounterId,
    int TypeId,
    int BeltId,
    int Slot,
    NpcAnchorKind AnchorKind,
    NpcRole Role,
    long? DestroyedAt,
    long? RetiredAt,
    NpcNavigationState Navigation
)
{
    internal bool Active => DestroyedAt is null && RetiredAt is null;

    internal static IEnumerable<NpcPopulationMember> InSystem(WorldAction world, int system) =>
        world.Set<WorldNpc>() is EconomySet<WorldNpc, EconomyWorldNpc> npcs
            ? Read(npcs.IndexedSnapshots(nameof(WorldNpc.SystemId), system).Where(n => n.SystemId == system))
            : Read(world.Filter<WorldNpc>(n => n.SystemId == system));

    internal static IEnumerable<NpcPopulationMember> AtAnchor(WorldAction world, long anchor) =>
        world.Set<WorldNpc>() is EconomySet<WorldNpc, EconomyWorldNpc> npcs
            ? Read(npcs.IndexedSnapshots(nameof(WorldNpc.EncounterId), anchor).Where(n => n.EncounterId == anchor))
            : Read(world.Filter<WorldNpc>(n => n.EncounterId == anchor));

    private static IEnumerable<NpcPopulationMember> Read(IEnumerable<EconomyWorldNpc> source) =>
        source.Select(n => new NpcPopulationMember(
            n.Id,
            n.EncounterId,
            n.TypeId,
            n.BeltId,
            n.SpawnSlot,
            n.AnchorKind,
            n.Role,
            n.DestroyedAt,
            n.RetiredAt,
            n.Navigation
        ));

    private static IQueryable<NpcPopulationMember> Read(IQueryable<WorldNpc> source) =>
        source.Select(n => new NpcPopulationMember(
            n.Id,
            n.EncounterId,
            n.TypeId,
            n.BeltId,
            n.SpawnSlot,
            n.AnchorKind,
            n.Role,
            n.DestroyedAt,
            n.RetiredAt,
            n.Navigation
        ));
}
