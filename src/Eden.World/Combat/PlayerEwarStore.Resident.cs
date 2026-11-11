using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal sealed partial class PlayerEwarStore
{
    private sealed record EffectEndpoint(long Pilot, long Source, long Target, int System, TackleTargetKind Kind);

    private static TransactionParticipants EwarParticipants(
        WorldAction world,
        long? actor = null,
        long? ship = null,
        long? module = null,
        TackleTarget? target = null,
        bool incoming = false,
        bool all = false
    )
    {
        IQueryable<WorldPlayerEwar> rows = world.Set<WorldPlayerEwar>();
        if (module is long item)
        {
            rows = rows.Where(row => row.ModuleId == item);
        }
        else if (!all)
        {
            rows = rows.Where(row =>
                row.PilotId == actor && row.SourceShipId == ship
                || incoming && row.TargetKind == TackleTargetKind.Ship && row.TargetId == ship
            );
        }
        var endpoints = rows.Select(row => new EffectEndpoint(row.PilotId, row.SourceShipId, row.TargetId, row.SystemId, row.TargetKind))
            .ToArray();
        HashSet<long> pilots = [];
        HashSet<long> ships = [];
        HashSet<int> systems = [];
        HashSet<SimulationResource> resources = [];
        if (actor is long pilot)
        {
            pilots.Add(pilot);
            long active = world.Pilots.Where(row => row.Id == pilot).Select(row => row.ActiveShipId).SingleOrDefault();
            if (active != 0)
            {
                ships.Add(active);
            }
        }
        if (ship is long hull)
        {
            ships.Add(hull);
        }
        foreach (var endpoint in endpoints)
        {
            pilots.Add(endpoint.Pilot);
            ships.Add(endpoint.Source);
            systems.Add(endpoint.System);
            Target(endpoint.Target, endpoint.Kind);
        }
        if (target is not null)
        {
            Target(target.Id, target.Kind);
        }
        return new(systems, pilots, ships, resources);

        void Target(long id, TackleTargetKind kind)
        {
            if (kind == TackleTargetKind.Npc)
            {
                resources.Add(new(SharedSimulationResource.Npc, id));
            }
            else
            {
                ships.Add(id);
            }
        }
    }

    internal static IReadOnlyList<ElectronicWarfareContact> Active(WorldAction world, long targetId) =>
        world
            .Set<WorldPlayerEwar>()
            .Where(e => e.TargetId == targetId)
            .OrderBy(e => e.ModuleId)
            .AsNoTracking()
            .AsEnumerable()
            .Select(e => e.Contact())
            .ToArray();

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldAction world,
        long npcId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0
    ) => PlayerEwarRules.NpcAttributes(attributes, Active(world, npcId), missileRange);
}
