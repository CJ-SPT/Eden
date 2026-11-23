using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Structures;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.FactionWarfare;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Structures;

[Injectable]
internal sealed class PilotTetherStore(WorldDatabase database, TimeProvider clock) : IPilotTethering
{
    // Build 3662168 dogma attributes, verified against the pinned catalog.
    private const int TetheringRange = 2268;
    private const int DisallowTethering = 2343;
    public bool Available => LiveWorld.Find(database.Path) is not null;

    private sealed record TetherPreview(PilotTether? Tether, bool Change);

    public PilotTether? Advance(long pilotId, TetherFlightState flight)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Structure tethering requires the resident world.");
        if (live.Economy.TetherUnavailable(pilotId, flight.InSpace))
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var preview = live.Economy.Inspect(world => Preview(world, catalog, pilotId, flight, now));
        if (!preview.Change)
        {
            return preview.Tether;
        }
        using var scope = new WorldActionScope(
            database,
            select: world => new(pilots: [pilotId], systems: System(world, catalog, pilotId) is int system ? [system] : [])
        );
        var world = scope.World;
        var pilot = world.Pilots.Find(pilotId)!;
        long ship = pilot.ActiveShipId;
        var previous = world.Set<SavedPilotTether>().Find(ship);
        SavedStructure? structure = null;
        if (flight.InSpace && System(world, catalog, pilotId) is int system)
        {
            structure = world
                .Filter<SavedStructure>(row => row.SystemId == system)
                .OrderBy(row => row.Id)
                .FirstOrDefault(row => Eligible(world, catalog, pilotId, ship, row, flight, now));
        }
        if (structure is null)
        {
            if (previous is not null)
            {
                world.Remove(previous);
            }
            scope.Commit();
            return null;
        }
        if (previous?.StructureId != structure.Id)
        {
            if (previous is not null)
            {
                world.Remove(previous);
            }
            previous = world
                .Add(
                    new SavedPilotTether
                    {
                        ShipId = ship,
                        PilotId = pilotId,
                        StructureId = structure.Id,
                        EngagedAt = now,
                        RepairAt = checked(now + StructureTetherRules.RepairDelay),
                        RepairCompleted = !NeedsRepair(world, catalog, pilotId, ship, now),
                    }
                )
                .Entity;
            world.RemoveRange(world.Filter<NpcAggression>(row => row.TargetShipId == ship).ToArray());
            world.RemoveRange(world.Filter<WorldNpcEwar>(row => row.TargetShipId == ship).ToArray());
            world.RemoveRange(world.Filter<WorldTackle>(row => row.TargetId == ship).ToArray());
            world.RemoveRange(world.Filter<WorldPlayerEwar>(row => row.TargetId == ship).ToArray());
            world.AfterCommit(() => world.TargetLocks?.RemoveShip(ship));
        }
        if (!previous.RepairCompleted && previous.RepairAt <= now)
        {
            Repair(world, catalog, pilotId, ship, now);
            previous.RepairCompleted = true;
        }
        var result = Snapshot(catalog, structure, previous);
        scope.Commit();
        return result;
    }

    private static TetherPreview Preview(WorldAction world, CatalogDbContext catalog, long pilotId, TetherFlightState flight, long now)
    {
        if (world.Pilots.Find(pilotId) is not { } pilot)
        {
            throw new InvalidDataException("The tethering pilot is unavailable.");
        }
        var previous = world.Set<SavedPilotTether>().Find(pilot.ActiveShipId);
        if (previous is not null)
        {
            var structure = world.Set<SavedStructure>().Find(previous.StructureId);
            if (
                structure is not null
                && Eligible(world, catalog, pilotId, pilot.ActiveShipId, structure, flight, now)
                && (previous.RepairCompleted || previous.RepairAt > now)
            )
            {
                return new(Snapshot(catalog, structure, previous), false);
            }
            return new(null, true);
        }
        bool candidate =
            flight.InSpace
            && System(world, catalog, pilotId) is int system
            && world
                .Filter<SavedStructure>(row => row.SystemId == system)
                .AsEnumerable()
                .Any(row => Eligible(world, catalog, pilotId, pilot.ActiveShipId, row, flight, now));
        return new(null, candidate);
    }

    private static int? System(WorldAction world, CatalogDbContext catalog, long pilotId) =>
        world.Pilots.Find(pilotId)?.ActiveShip is { Position: not null } ship ? CrimewatchPersistence.System(catalog, ship) : null;

    private static PilotTether Snapshot(CatalogDbContext catalog, SavedStructure structure, SavedPilotTether saved) =>
        new(saved.ShipId, structure.Id, saved.EngagedAt, saved.RepairAt, saved.RepairCompleted)
        {
            StructureRadius = StructureCatalog.Require(catalog, structure.TypeId).Radius,
            Range = catalog.FittingAttributes(structure.TypeId).GetValueOrDefault((DogmaAttributeId)TetheringRange),
        };

    public bool Protected(long shipId)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        return LiveWorld.Find(database.Path)?.Economy.TetherProtected(shipId, now) == true;
    }

    public void Release(long pilotId)
    {
        // Connection cleanup can follow host disposal. A closed resident owner has nothing left to release here.
        if (
            LiveWorld.Find(database.Path) is not { } live
            || !live.Economy.Inspect(world => world.Filter<SavedPilotTether>(row => row.PilotId == pilotId).Any())
        )
        {
            return;
        }
        using var scope = new WorldActionScope(database, select: world => new(pilots: [pilotId]));
        scope.World.RemoveRange(scope.World.Filter<SavedPilotTether>(row => row.PilotId == pilotId).ToArray());
        scope.Commit();
    }

    internal static bool Protected(WorldAction world, CatalogDbContext catalog, long ship, long now) =>
        world is EconomyTransaction
        && world.Set<SavedPilotTether>().Find(ship) is { } tether
        && world.Set<SavedStructure>().Find(tether.StructureId) is { } structure
        && Eligible(world, catalog, tether.PilotId, ship, structure, new(true, false, false, false, false), now);

    private static bool Eligible(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long shipId,
        SavedStructure structure,
        TetherFlightState flight,
        long now
    )
    {
        if (
            !flight.InSpace
            || flight.Warping
            || flight.Cloaked
            || flight.Disrupted
            || flight.DisallowedModule
            || !structure.HasCore
            || world.Pilots.Find(pilot) is not { } character
            || character.ActiveShipId != shipId
            || world.Ships.Find(shipId) is not { } ship
            || world.Positions.Find(shipId) is not { } position
            || CrimewatchPersistence.System(catalog, ship) != structure.SystemId
            || structure.UnanchorAt <= now
            || world.Set<SpatialNavigationState>().Find(shipId) is { CloakModuleId: not 0 }
        )
        {
            return false;
        }
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        int group = catalog.RequireItem(ship.TypeId).GroupId;
        if (
            LanceDisruption.Active(world, shipId, now)
            || !StructureRules.CanDock(profile, structure.State, group)
            || !MilitiaEnlistmentStore.CanUseDocking(world, character, structure.SystemId, group)
            || group is (int)Eden.Gameplay.Ships.HullClass.Titan or (int)Eden.Gameplay.Ships.HullClass.LancerDreadnought
                && AreaWeaponStore.DoomsdayLocked(world, shipId, now, movement: false)
        )
        {
            return false;
        }
        var timers = world.Set<SavedCrimewatch>().Find(pilot);
        bool online = world.Filter<SavedStructureService>(row => row.StructureId == structure.Id && row.Online).Any();
        bool disallowed = world
            .Filter<ActiveFittedModule>(active => active.ShipId == shipId && (active.ExpiresAt == 0 || active.ExpiresAt > now))
            .AsEnumerable()
            .Any(active =>
                world.Set<OwnedItem>().Find(active.ModuleId) is { } item
                && catalog.FittingAttributes(item.TypeId).GetValueOrDefault((DogmaAttributeId)DisallowTethering) > 0
            );
        var attributes = world.Attributes(pilot);
        disallowed |= attributes.GetValueOrDefault((DogmaAttributeId)DisallowTethering) > 0;
        // Fitting already combines paid incoming tackle and defensive warp-core strength.
        // Repeating tackle remains authoritative until its owner renews or stops the paid effect.
        bool disrupted = attributes.GetValueOrDefault(DogmaAttributeId.WarpScrambleStatus) > 0;
        if (
            world.Set<ShipHealthState>().Find(shipId) is { } health
            && health.StructureDamage >= attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints)
        )
        {
            return false;
        }
        var hull = catalog.RequireItem(ship.TypeId);
        double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(structure.SystemId);
        return StructureTetherRules.Eligible(
            new(
                profile,
                structure.State,
                StructureRules.Upkeep(structure.LowPowerAt, online, now, structure.TypeId, structure.AbandonAt),
                structure.HasCore,
                PilotStructureStore.Access(world, structure, pilot),
                hull.GroupId,
                hull.Radius ?? 0,
                new SpacePosition(position.X, position.Y, position.Z),
                new SpacePosition(structure.X, structure.Y, structure.Z),
                catalog.FittingAttributes(structure.TypeId).GetValueOrDefault((DogmaAttributeId)TetheringRange),
                world.TargetLocks?.HasOutgoing(shipId) == true,
                timers?.WeaponsUntil > now,
                security >= StructureRules.HighSecurityBoundary && timers?.CriminalUntil > now,
                !StructureTetherRules.SecurityPermits(security, world.Set<SavedSecurityStatus>().Find(pilot)?.Value ?? 0),
                0,
                flight with
                {
                    DisallowedModule = flight.DisallowedModule || disallowed,
                    Disrupted = flight.Disrupted || disrupted,
                }
            ),
            now
        );
    }

    private static bool NeedsRepair(WorldAction world, CatalogDbContext catalog, long pilot, long ship, long now)
    {
        var attributes = world.Attributes(pilot);
        var defense = ShipHealth.DefenseFrom(attributes);
        var health = PilotHealthStore.Read(world, ship, defense, now);
        return health.Remaining != defense.Undamaged
            || ShipResources.Capacitor(world, ship, attributes, now) < attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            || world
                .Filter<OwnedItem>(item => item.LocationId == ship && ShipHeatPersistence.Rack(item.FlagId) != null)
                .AsEnumerable()
                .Any(item => world.Set<SavedModuleCondition>().Find(item.Id) is { Damage: > 0 })
            || world
                .Filter<OwnedItem>(item => item.LocationId == ship && item.FlagId == (int)InventoryFlag.DroneBay)
                .AsEnumerable()
                .Any(item =>
                    world.Set<WorldDrone>().Find(item.Id) is { SystemId: null } drone
                    && (
                        drone.ArmorDamage > 0
                        || drone.StructureDamage > 0
                        || drone.Shield < catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.ShieldCapacity)
                    )
                );
    }

    private static void Repair(WorldAction world, CatalogDbContext catalog, long pilot, long ship, long now)
    {
        var attributes = world.Attributes(pilot);
        var health = world.Set<ShipHealthState>().Find(ship) ?? world.Add(new ShipHealthState { ShipId = ship }).Entity;
        health.ShieldCharge = attributes.GetValueOrDefault(DogmaAttributeId.ShieldCapacity);
        health.ArmorDamage = 0;
        health.StructureDamage = 0;
        health.UpdatedAt = now;
        var energy = world.Set<ShipEnergy>().Find(ship) ?? world.Add(new ShipEnergy { ShipId = ship }).Entity;
        energy.Charge = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        energy.UpdatedAt = now;
        foreach (var item in world.Filter<OwnedItem>(row => row.LocationId == ship).ToArray())
        {
            if (ShipHeatPersistence.Rack(item.FlagId) is not null)
            {
                ModuleConditionPersistence.Repair(world, catalog, item, double.MaxValue);
                world.RemoveRange(world.Filter<SavedNaniteRepair>(repair => repair.ModuleId == item.Id).ToArray());
            }
            else if (item.FlagId == (int)InventoryFlag.DroneBay && world.Set<WorldDrone>().Find(item.Id) is { SystemId: null } drone)
            {
                drone.ArmorDamage = 0;
                drone.StructureDamage = 0;
                drone.Shield = catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.ShieldCapacity);
                drone.UpdatedAt = now;
            }
        }
    }
}
