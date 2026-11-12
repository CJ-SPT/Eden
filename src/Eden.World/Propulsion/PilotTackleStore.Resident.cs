using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Propulsion;

internal sealed partial class PilotTackleStore
{
    private sealed record TackleEndpoint(long Pilot, long Source, long Target, int System, TackleTargetKind Kind);

    private sealed record ActiveEndpoint(long Module, long Ship);

    private TransactionParticipants TackleParticipants(
        WorldAction world,
        long? actor = null,
        long? ship = null,
        long? module = null,
        TackleTarget? target = null,
        bool incoming = false,
        bool all = false
    )
    {
        IQueryable<WorldTackle> rows = world.Set<WorldTackle>();
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
        var endpoints = rows.Select(row => new TackleEndpoint(row.PilotId, row.SourceShipId, row.TargetId, row.SystemId, row.TargetKind))
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
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (
            var active in world
                .Set<ActiveFittedModule>()
                .Where(row => all || row.ModuleId == module)
                .Select(row => new ActiveEndpoint(row.ModuleId, row.ShipId))
                .ToArray()
        )
        {
            if (
                !all
                || world.Set<OwnedItem>().Find(active.Module) is { } fitted
                    && catalog.FittingType(fitted.TypeId)?.GroupId == TackleDefinitions.WarpCoreStabilizerGroup
            )
            {
                ships.Add(active.Ship);
            }
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

    internal static ILookup<long, WorldTackle> NpcTackles(WorldAction world, long[] ids) =>
        world
            .Set<WorldTackle>()
            .Where(row => row.TargetKind == TackleTargetKind.Npc && Enumerable.Contains(ids, row.TargetId))
            .ToArray()
            .ToLookup(row => row.TargetId);

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldAction world,
        long npcId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0
    ) =>
        PlayerEwarStore.NpcAttributes(
            world,
            npcId,
            TackleDefinitions.NpcAttributes(attributes, NpcTackles(world, [npcId])[npcId].Select(row => row.Module()).ToArray()),
            missileRange
        );
}
