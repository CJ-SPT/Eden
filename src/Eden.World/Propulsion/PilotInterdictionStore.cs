using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Propulsion;

[Injectable]
internal sealed partial class PilotInterdictionStore(WorldDatabase database) : IPilotInterdiction
{
    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Interdiction requires the resident world.");

    public bool IsLauncher(long pilotId, long moduleId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var item = world.Set<OwnedItem>().Find(moduleId);
                using CatalogDbContext catalog = new(database.CatalogPath);
                return item?.OwnerId == pilotId && catalog.FittingType(item.TypeId)?.GroupId == InterdictionRules.LauncherGroup;
            })
        ?? false;

    public InterdictionActivation Launch(long pilotId, long shipId, long moduleId, long now)
    {
        using var transaction = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var world = (WorldAction)transaction;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { } position
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !InterdictionCatalog.Module(catalog, item.TypeId)
        )
        {
            throw new InvalidDataException("Use an online interdiction sphere launcher on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        InterdictionRules.CheckSystem(system, CrimewatchPersistence.Security(catalog, system));
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The interdiction launcher is still cycling or reloading.");
        }
        var charge = PilotAmmunitionStore.Contents(world, moduleId);
        if (
            charge is not { Quantity: > 0 }
            || charge.OwnerId != pilotId
            || !InterdictionCatalog.Probe(catalog, charge.TypeId, catalog.RequireItem(charge.TypeId).GroupId)
        )
        {
            throw new InvalidDataException("Load a warp disrupt probe before launching.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, charge.TypeId);
        bool overloaded = PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        var calculation = FittingCalculation.Read(
            world,
            catalog,
            pilot,
            overloadedModules: overloaded ? new HashSet<long> { moduleId } : null
        );
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot launch probes.");
        }
        var attributes = calculation.Graph.Attributes(moduleId);
        var probe = catalog.FittingAttributes(charge.TypeId);
        double duration = attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
        long expires = InterdictionRules.Deadline(now, probe.GetValueOrDefault(DogmaAttributeId.MissileFlightTime));
        long ready = InterdictionRules.Deadline(now, duration);
        double capacitor = ShipResources.SpendCapacitor(
            world,
            shipId,
            ship,
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed),
            now
        );
        Magazine before = PilotAmmunitionStore.Read(world, item, charge);
        var after = before with { Quantity = before.Quantity - 1 };
        charge.Quantity--;
        if (charge.Quantity == 0)
        {
            world.Remove(charge);
        }
        foreach (
            var expired in world
                .Filter<SavedInterdictionField>(row => row.SystemId == system && (row.ExpiresAt <= now || row.HitPoints <= 0))
                .ToArray()
        )
        {
            world.Remove(expired);
        }
        long id = world.Next("SavedInterdictionField", InterdictionRules.FirstProbeId);
        world.Add(
            new SavedInterdictionField
            {
                Id = id,
                PilotId = pilotId,
                SourceShipId = shipId,
                ModuleId = moduleId,
                TypeId = before.TypeId,
                SystemId = system,
                Kind = InterdictionKind.Probe,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Radius = probe[DogmaAttributeId.WarpScrambleRange],
                HitPoints = probe.GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
                StartedAt = now,
                ExpiresAt = expires,
            }
        );
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = ready;
        var timers = CrimewatchPersistence.State(world, pilotId);
        timers.SystemId = system;
        var state = timers.Timers();
        timers.Apply(state with { Weapons = CrimewatchRules.Extend(state.Weapons, now, CrimewatchRules.WeaponsDuration) });
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, ready) : null;
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, shipId, heat);
        }
        return new(moduleId, item.TypeId, DogmaEffect.UseMissiles, duration, ready, ready, capacitor, new(before, after));
    }

    public InterdictionWarp Resolve(long shipId, int systemId, SpacePosition origin, SpacePosition destination, long now) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var fields = Fields(world, systemId, now);
                if (fields.Length == 0)
                {
                    return new InterdictionWarp(false, destination);
                }
                using CatalogDbContext catalog = new(database.CatalogPath);
                bool immune = Immune(world, catalog, shipId, now);
                return InterdictionRules.Resolve(
                    origin,
                    destination,
                    fields.Select(row => Sphere(world, row)).OfType<InterdictionSphere>().ToArray(),
                    immune
                );
            })
        ?? new(false, destination);

    public IReadOnlyList<StaticSpaceObject> Contacts(int systemId, long now) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
                Fields(world, systemId, now)
                    .Where(row => row.Kind == InterdictionKind.Probe)
                    .Select(row => new StaticSpaceObject(
                        row.Id,
                        row.TypeId,
                        InterdictionRules.ProbeGroup,
                        (int)InventoryCategory.Charge,
                        0,
                        new(row.X, row.Y, row.Z),
                        90
                    )
                    {
                        Solid = false,
                        IsGlobal = false,
                        InterdictionRadius = row.Radius,
                    })
                    .ToArray()
            )
        ?? [];

    private static InterdictionSphere? Sphere(WorldAction world, SavedInterdictionField row)
    {
        if (row.Kind is InterdictionKind.Probe or InterdictionKind.Projector)
        {
            return new(row.Id, new(row.X, row.Y, row.Z), row.Radius);
        }
        var source = world.Pilots.Find(row.PilotId);
        var item = world.Set<OwnedItem>().Find(row.ModuleId);
        var active = world.Set<ActiveFittedModule>().Find(row.ModuleId);
        return
            source?.ActiveShipId == row.SourceShipId
            && source.ActiveShip.Position is { } position
            && position.SystemId == row.SystemId
            && item is { Online: true }
            && item.LocationId == row.SourceShipId
            && active?.ExpiresAt == row.ExpiresAt
            && world.Set<ShipLoss>().Find(row.SourceShipId) is null
            ? new(row.Id, new(position.X, position.Y, position.Z), row.Radius)
            : null;
    }

    private static SavedInterdictionField[] Fields(WorldAction world, int system, long now) =>
        world.Filter<SavedInterdictionField>(row => row.SystemId == system && row.ExpiresAt > now && row.HitPoints > 0).ToArray();
}
