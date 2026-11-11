using Eden.Data.World.Propulsion;
using Eden.Gameplay.Propulsion;
using Eden.World.Runtime.Economy;

namespace Eden.World.Propulsion;

internal sealed partial class PilotTackleStore
{
    internal static ILookup<long, WorldTackle> NpcTackles(WorldAction world, long[] ids) =>
        world
            .Set<WorldTackle>()
            .Where(row => row.TargetKind == TackleTargetKind.Npc && Enumerable.Contains(ids, row.TargetId))
            .ToArray()
            .ToLookup(row => row.TargetId);
}
