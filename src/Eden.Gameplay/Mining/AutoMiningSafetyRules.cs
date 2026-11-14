using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Mining;

public static class AutoMiningSafetyRules
{
    public static bool Threat(NpcContact npc, long shipId, int systemId, SpacePosition position) =>
        npc.SystemId == systemId
        && !npc.Health.Remaining.Destroyed
        && npc.Warp is null
        && position.DistanceTo(npc.Position) <= NpcEncounterDefinitions.VisibilityRange
        && (npc.Role == NpcRole.Pirate || npc.Weapon?.TargetId == shipId || npc.Navigation?.TargetId == shipId);

    public static bool SameGrid(AutoMiningDestination left, AutoMiningDestination right) =>
        left.SystemId == right.SystemId && left.Position.DistanceTo(right.Position) <= NpcEncounterDefinitions.VisibilityRange;
}
