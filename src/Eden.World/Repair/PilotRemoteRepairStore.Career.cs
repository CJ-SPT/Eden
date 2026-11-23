using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Repair;

internal sealed partial class PilotRemoteRepairStore
{
    private static TransactionParticipants RemoteRepairParticipants(WorldAction world, long pilotId, long targetId) =>
        world.Set<WorldNpc>().Find(targetId) is null
            ? new(pilots: [pilotId], ships: [targetId])
            : new(pilots: [pilotId], resources: [new(SharedSimulationResource.Npc, targetId)]);

    private static RemoteRepairActivation StartCareerRepair(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        WorldNpc npc,
        RemoteRepairModule module,
        IReadOnlyDictionary<DogmaAttributeId, double> ship,
        long now
    )
    {
        world.RequireUncloaked(pilot.ActiveShipId);
        _ = CareerRepairDistance(catalog, pilot, npc, module, ship);
        var cooldown = world.Set<ModuleCooldown>().Find(module.ItemId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The remote repairer is still cycling.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        long completes = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = module.ItemId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = completes;
        return new(new RemoteRepairCycle(module, pilot.ActiveShipId, npc.Id, now, completes) { CareerTarget = true }, capacitor);
    }

    public void CompleteCareer(long pilotId, RemoteRepairCycle cycle, long now)
    {
        if (!cycle.CareerTarget || now < cycle.CompletesAt || LiveWorld.Find(database.Path) is not { } live)
        {
            return;
        }
        using var world = live.Economy.Begin(new(pilots: [pilotId], resources: [new(SharedSimulationResource.Npc, cycle.TargetId)]));
        var pilot = Pilot(world, pilotId);
        if (
            pilot.ActiveShipId != cycle.ShipId
            || pilot.ActiveShip.Position is null
            || world.Set<ModuleCooldown>().Find(cycle.Module.ItemId)?.ReadyAt != cycle.CompletesAt
            || world.Set<OwnedItem>().Find(cycle.Module.ItemId) is not { Online: true, Singleton: true } item
            || item.OwnerId != pilotId
            || item.LocationId != cycle.ShipId
            || item.TypeId != cycle.Module.TypeId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || world.Set<WorldNpc>().Find(cycle.TargetId) is not { } npc
            || PilotMissionStore.CareerEffectTarget(world, pilot, npc, cycle.Module.Effect) is null
        )
        {
            return;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        double distance = CareerRepairDistance(catalog, pilot, npc, cycle.Module, ship);
        double repaired = RemoteRepairRules.Amount(cycle.Module, distance, 1);
        if (cycle.Module.Kind == RemoteRepairKind.Shield)
        {
            var attributes = NpcProfileCatalog.ReadAttributes(catalog, npc.TypeId);
            npc.ShieldCharge = Math.Min(attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity), npc.ShieldCharge + repaired);
        }
        else
        {
            npc.ArmorDamage = Math.Max(0, npc.ArmorDamage - repaired);
        }
        _ = PilotMissionStore.CompleteCareerEffect(world, pilot, npc, cycle.Module.Effect, now);
        world.Commit();
    }

    private static double CareerRepairDistance(
        CatalogDbContext catalog,
        Pilot pilot,
        WorldNpc npc,
        RemoteRepairModule module,
        IReadOnlyDictionary<DogmaAttributeId, double> ship
    )
    {
        var position = pilot.ActiveShip.Position!;
        double distance = new SpacePosition(position.X, position.Y, position.Z).DistanceTo(NpcMotion.Position(npc));
        distance -= (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0) + (catalog.RequireItem(npc.TypeId).Radius ?? 0);
        if (
            (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != npc.SystemId
            || distance > Math.Min(TackleDefinitions.MaximumDistance, ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
            || distance > module.Optimal && module.Falloff == 0
        )
        {
            throw new InvalidDataException("The damaged training vessel is outside repair range.");
        }
        return Math.Max(0, distance);
    }
}
