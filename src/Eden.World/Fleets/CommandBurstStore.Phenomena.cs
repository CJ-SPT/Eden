using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Flight;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Fleets;

internal sealed partial class CommandBurstStore
{
    public PhenomenaPulse PulsePhenomena(long pilot, long ship, long module, long now)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Phenomena require the resident world.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        int system = live.Economy.Inspect(world =>
        {
            var source = world.Pilots.Find(pilot) ?? throw new InvalidDataException("Unknown pilot.");
            return CrimewatchPersistence.System(catalog, source.ActiveShip);
        });
        return LiveWorld.Execute(
            database.Path,
            system,
            () => PulsePhenomenaOwned(live, catalog, system, pilot, ship, module, now),
            nameof(PulsePhenomena)
        );
    }

    private PhenomenaPulse PulsePhenomenaOwned(
        LiveWorld live,
        CatalogDbContext catalog,
        int system,
        long pilotId,
        long shipId,
        long moduleId,
        long now
    )
    {
        var candidates = live.Economy.Inspect(world =>
            world
                .Pilots.Where(candidate => candidate.ActiveShip.Position != null)
                .AsEnumerable()
                .Where(candidate => CrimewatchPersistence.System(catalog, candidate.ActiveShip) == system)
                .Select(candidate => (Pilot: candidate.Id, Ship: candidate.ActiveShipId))
                .ToArray()
        );
        using var world = live.Economy.Begin(
            new(
                systems: [system],
                pilots: candidates.Select(candidate => candidate.Pilot).Append(pilotId),
                ships: candidates.Select(candidate => candidate.Ship).Append(shipId)
            )
        );
        var source = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            source?.ActiveShipId != shipId
            || source.ActiveShip.Position is not { } position
            || CrimewatchPersistence.System(catalog, source.ActiveShip) != system
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !CommandBurstCatalog.Module(catalog, item.TypeId, CommandBurstRules.PhenomenaGroup)
        )
        {
            throw new InvalidDataException("Use an online Phenomena Generator on its compatible Titan in space.");
        }
        world.RequireUncloaked(shipId);
        if (world.Set<Account>().Find(source.AccountId)?.CloneGrade != AccountCloneGrade.Omega)
        {
            throw new InvalidDataException("Phenomena Generators require an Omega clone.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        PilotFittingStore.CheckHull(catalog, item.TypeId, source.ActiveShip.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        RequirePhenomenaInactive(world, shipId, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The Phenomena Generator is still cycling.");
        }

        var calculation = FittingCalculation.Read(world, catalog, source);
        var effective = calculation.Graph.Attributes(moduleId);
        var module = CommandBurstRules.Define(moduleId, item.TypeId, DogmaEffect.TitanPhenomena, effective);
        int fuel = checked((int)effective.GetValueOrDefault(DogmaAttributeId.ConsumptionType));
        int quantity = checked((int)effective.GetValueOrDefault(DogmaAttributeId.ConsumptionQuantity));
        var stacks = PilotCapitalTravelStore
            .FuelStacks(world, pilotId, shipId, InventoryFlag.FuelBay, fuel)
            .Concat(PilotCapitalTravelStore.FuelStacks(world, pilotId, shipId, InventoryFlag.Cargo, fuel))
            .ToArray();
        if (quantity <= 0 || stacks.Sum(stack => (long)stack.Quantity) < quantity)
        {
            throw new InvalidDataException("Load the generator's required isotopes into the Titan.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, source, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed Titan cannot activate a Phenomena Generator.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, shipId, attributes, module.Capacitor, now);
        var changes = PilotCapitalTravelStore.SpendFuel(world, catalog, stacks, quantity);
        long readyAt = checked(now + module.CycleTicks);
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = readyAt;
        var active = world.Set<ActiveFittedModule>().Find(moduleId) ?? world.Add(new ActiveFittedModule { ModuleId = moduleId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = readyAt;

        var timers = CrimewatchPersistence.State(world, pilotId);
        timers.SystemId = system;
        var state = timers.Timers();
        timers.Apply(state with { Weapons = CrimewatchRules.Extend(state.Weapons, now, CrimewatchRules.WeaponsDuration) });
        SpacePosition origin = new(position.X, position.Y, position.Z);
        double radius = catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0;
        List<long> recipients = [shipId];
        foreach (var (candidate, otherShip) in candidates)
        {
            if (
                otherShip == shipId
                || world.Pilots.Find(candidate) is not { } target
                || target.ActiveShipId != otherShip
                || target.ActiveShip.Position is not { } saved
                || CrimewatchPersistence.System(catalog, target.ActiveShip) != system
                || world.Set<ShipLoss>().Find(otherShip) is not null
                || presence.IsTravelProtected(system, otherShip)
            )
            {
                continue;
            }
            var type = catalog.RequireItem(target.ActiveShip.TypeId);
            var point = presence.FindArea(system, otherShip)?.Position ?? new SpacePosition(saved.X, saved.Y, saved.Z);
            if (
                type.GroupId != FleetDefinitions.CapsuleGroup
                && CommandBurstRules.InRange(origin, radius, point, type.Radius ?? 0, module.Radius)
            )
            {
                recipients.Add(otherShip);
            }
        }
        SaveBoosts(
            world,
            recipients,
            system,
            checked(now + module.BoostTicks),
            CommandBurstCatalog.Values(calculation.Graph, moduleId),
            phenomena: true
        );
        world.Commit();
        return new(module, readyAt, capacitor, changes, recipients.ToArray());
    }

    internal static void RequirePhenomenaInactive(WorldAction world, long ship, long now)
    {
        if (
            world
                .Set<ActiveFittedModule>()
                .Where(row => row.ShipId == ship && row.ExpiresAt > now)
                .AsEnumerable()
                .Any(row => world.Set<OwnedItem>().Find(row.ModuleId) is { } item && CommandBurstRules.IsPhenomena(item.TypeId))
        )
        {
            throw new InvalidDataException("Wait for the active Phenomena Generator to finish.");
        }
    }
}
