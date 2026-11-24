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

    // Pirates affect population through membership and availability. Their combat
    // motion and damage still refresh the owning combat and contact views.
    internal static bool RetainsBeltPirateDeadline(WorldNpc before, WorldNpc after) =>
        before is { Role: NpcRole.Pirate, DestroyedAt: null, RetiredAt: null }
        && (
            before is { AnchorKind: NpcAnchorKind.Belt, BeltId: > 0, EncounterId: > 0 }
            || NpcPopulationRules.IsIncursionPatrol(before.AnchorKind)
        )
        && after is { Role: NpcRole.Pirate, DestroyedAt: null, RetiredAt: null }
        && before.AnchorKind == after.AnchorKind
        && before.ExpiresAt == after.ExpiresAt
        && before.TypeId != NpcEncounterDefinitions.GuristasRookieType
        && !NpcSpecialTypes.Excluded.Contains(before.TypeId)
        && before.SystemId == after.SystemId
        && before.BeltId == after.BeltId
        && before.EncounterId == after.EncounterId
        && before.SpawnSlot == after.SpawnSlot
        && before.TypeId == after.TypeId
        && before.CorporationId == after.CorporationId
        && before.FactionId == after.FactionId
        && before.Name == after.Name
        && before.AcquisitionRange == after.AcquisitionRange
        && before.HomeX == after.HomeX
        && before.HomeY == after.HomeY
        && before.HomeZ == after.HomeZ;

    internal static IEnumerable<NpcPopulationMember> InSystem(WorldAction world, int system) =>
        world.Set<WorldNpc>() is EconomySet<WorldNpc, EconomyWorldNpc> npcs
            ? FromSnapshots(npcs.IndexedSnapshots(nameof(WorldNpc.SystemId), system).Where(n => n.SystemId == system))
            : Read(world.Filter<WorldNpc>(n => n.SystemId == system));

    internal static IEnumerable<NpcPopulationMember> AtAnchor(WorldAction world, long anchor) =>
        world.Set<WorldNpc>() is EconomySet<WorldNpc, EconomyWorldNpc> npcs
            ? FromSnapshots(npcs.IndexedSnapshots(nameof(WorldNpc.EncounterId), anchor).Where(n => n.EncounterId == anchor))
            : Read(world.Filter<WorldNpc>(n => n.EncounterId == anchor));

    internal static IEnumerable<NpcPopulationMember> FromSnapshots(IEnumerable<EconomyWorldNpc> source) =>
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
