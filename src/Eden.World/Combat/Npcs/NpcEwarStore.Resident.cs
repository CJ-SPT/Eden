using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal sealed partial class NpcEwarStore
{
    private static TransactionParticipants EffectParticipants(
        WorldAction world,
        long ship,
        long? pilot = null,
        IReadOnlyList<NpcEwarSource>? sources = null
    )
    {
        IEnumerable<long> candidates =
            sources is not null ? sources.Select(source => source.Id)
            : pilot is not null ? world.Set<NpcAggression>().Where(row => row.TargetShipId == ship).Select(row => row.NpcId)
            : [];
        var npcs = world
            .Set<WorldNpcEwar>()
            .Where(row => row.TargetShipId == ship)
            .Select(row => row.NpcId)
            .AsEnumerable()
            .Concat(candidates)
            .Distinct()
            .ToArray();
        var ships = world
            .Set<WorldNpcEwar>()
            .Where(row => Enumerable.Contains(npcs, row.NpcId))
            .Select(row => row.TargetShipId)
            .AsEnumerable()
            .Append(ship)
            .Distinct()
            .ToArray();
        return new(
            pilots: pilot is long id ? [id] : [],
            ships: ships,
            resources: npcs.Select(id => new SimulationResource(SharedSimulationResource.Npc, id))
        );
    }

    internal static IReadOnlyList<NpcEwarContact> Active(WorldAction world, long shipId) =>
        world
            .Set<WorldNpcEwar>()
            .Where(e => e.TargetShipId == shipId && e.AppliedJson != null)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact()!)
            .ToArray();
}
