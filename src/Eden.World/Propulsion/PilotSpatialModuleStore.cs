using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Mining;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

[Injectable]
internal sealed class PilotSpatialModuleStore(WorldDatabase database, IPilotTargetLocks? locks = null) : IPilotSpatialModules
{
    public SpatialStatus Status(long shipId) =>
        LiveReadModels.Find(database.Path)?.SpatialNavigation(shipId, () => ReadStatus(shipId)) ?? ReadStatus(shipId);

    private SpatialStatus ReadStatus(long shipId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => ReadStatus(world, catalog, shipId));
    }

    private static SpatialStatus ReadStatus(WorldAction world, CatalogDbContext catalog, long shipId)
    {
        var state = world.Set<SpatialNavigationState>().Find(shipId);
        var module = world
            .Filter<ActiveFittedModule>(row => row.ShipId == shipId)
            .Select(row => row.ModuleId)
            .AsEnumerable()
            .Select(id => world.Set<OwnedItem>().Find(id))
            .FirstOrDefault(item =>
                item is { Online: true, Singleton: true }
                && item.LocationId == shipId
                && InventoryDefinitions.IsHighSlot(item.FlagId)
                && catalog.FittingType(item.TypeId)?.GroupId == SpatialModuleRules.CloakGroup
            );
        return new(
            module?.Id ?? 0,
            module is not null && FittingCatalog.DefaultEffect(catalog, module.TypeId) == DogmaEffect.CovertCloaking,
            state is { CloakModuleId: not 0 } && module is null ? long.MaxValue : state?.TargetingReadyAt ?? 0
        );
    }

    public bool Available(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.SpatialMembership(pilotId, shipId, moduleId, () => ReadAvailable(pilotId, shipId, moduleId))
        ?? ReadAvailable(pilotId, shipId, moduleId);

    private bool ReadAvailable(long pilotId, long shipId, long moduleId)
    {
        return WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Set<OwnedItem>()
                    .Any(i =>
                        i.Id == moduleId
                        && i.OwnerId == pilotId
                        && i.LocationId == shipId
                        && i.Online
                        && i.Singleton
                        && i.FlagId >= (int)InventoryFlag.MediumSlot0
                        && i.FlagId <= (int)InventoryFlag.HighSlot7
                    )
        );
    }

    private static bool Scrambled(WorldAction world, long ship, long now) =>
        world
            .Filter<WorldTackle>(t => t.TargetId == ship && t.TargetKind == TackleTargetKind.Ship && t.MwdBlock > 0 && t.ExpiresAt > now)
            .Any();

    public SpatialActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        SpacePosition heading,
        SpacePosition velocity,
        long now
    )
    {
        _ = SpatialModuleRules.Jump(SpacePosition.Zero, heading);
        if (!SpatialModuleRules.Finite(velocity))
        {
            throw new InvalidDataException("Invalid micro-jump velocity.");
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).Single();
        var item = world.Set<OwnedItem>().Find(moduleId);
        bool cloak = SpatialModuleRules.IsCloak(effect);
        if (
            pilot.ActiveShip.Position is null
            || pilot.ActiveShip.OwnerId != pilotId
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !(cloak ? InventoryDefinitions.IsHighSlot(item.FlagId) : InventoryDefinitions.IsMediumSlot(item.FlagId))
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != effect
            || !SpatialModuleCatalog.Supported(catalog, item.TypeId, catalog.RequireItem(item.TypeId).GroupId)
            || !InventoryDefinitions.CanFitShip(
                catalog.FittingAttributes(item.TypeId),
                pilot.ActiveShip.TypeId,
                catalog.RequireItem(pilot.ActiveShip.TypeId).GroupId
            )
        )
        {
            throw new InvalidDataException("Use an online spatial module fitted to your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var status = ReadStatus(world, catalog, pilot.ActiveShipId);
        if (cloak)
        {
            CheckCloakGeometry(world, catalog, pilot.ActiveShip, now);
        }
        var cloakTypes = catalog.Definitions.TypesInGroup(SpatialModuleRules.CloakGroup).ToHashSet();
        if (
            cloak
            && world
                .Filter<OwnedItem>(i =>
                    i.LocationId == pilot.ActiveShipId
                    && i.FlagId >= (int)InventoryFlag.HighSlot0
                    && i.FlagId <= (int)InventoryFlag.HighSlot7
                    && cloakTypes.Contains(i.TypeId)
                )
                .Count() != 1
        )
        {
            throw new InvalidDataException("Fit only one cloaking device before activating it.");
        }
        if (
            status.CloakModuleId != 0
            || world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now
            || world.Set<ActiveFittedModule>().Find(moduleId) is not null
        )
        {
            throw new InvalidDataException("The module is active, cloaked or waiting for reactivation.");
        }
        if (
            cloak
            && (
                locks?.IsTargeted(pilot.ActiveShipId) == true
                || world.Filter<NpcAggression>(a => a.TargetShipId == pilot.ActiveShipId && a.ReadyAt <= now).Any()
            )
        )
        {
            throw new InvalidDataException("An established target lock prevents cloaking.");
        }
        if (
            !cloak
            && (
                Scrambled(world, pilot.ActiveShipId, now)
                || world.Set<SpatialNavigationState>().Find(pilot.ActiveShipId) is { JumpModuleId: not 0 }
            )
        )
        {
            throw new InvalidDataException("A warp scrambler or another spool prevents micro jumping.");
        }
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var values = calculation.Graph.Attributes(moduleId);
        if (cloak)
        {
            // The pinned skill's legacy effect 848 has no declarative modifierInfo.
            double bonus = catalog
                .FittingAttributes(SpatialModuleRules.CloakingSkill)
                .GetValueOrDefault(DogmaAttributeId.CloakingTargetingDelayBonus);
            values[DogmaAttributeId.CloakingTargetingDelay] =
                values.GetValueOrDefault(DogmaAttributeId.CloakingTargetingDelay)
                * (1 + bonus * PilotSkillReader.Level(world, catalog, pilotId, SpatialModuleRules.CloakingSkill) / 100);
        }
        var module = SpatialModuleRules.Define(moduleId, item.TypeId, effect, values);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a spatial module.");
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        long ends = cloak ? 0 : SpatialModuleRules.Deadline(now, module.DurationMilliseconds);
        long ready = cloak ? 0 : SpatialModuleRules.Deadline(ends, module.ReactivationMilliseconds);
        world.Add(
            new ActiveFittedModule
            {
                ModuleId = moduleId,
                ShipId = pilot.ActiveShipId,
                ExpiresAt = ends,
            }
        );
        if (cloak)
        {
            var state = State(world, pilot.ActiveShipId);
            state.CloakModuleId = moduleId;
            state.CloakTargetingMilliseconds = module.TargetingDelayMilliseconds;
            state.CloakReactivationMilliseconds = module.ReactivationMilliseconds;
            // A pending NPC acquisition is not an established lock and cannot finish through a cloak.
            world.RemoveRange(world.Filter<NpcAggression>(a => a.TargetShipId == pilot.ActiveShipId));
        }
        if (!cloak)
        {
            var state = State(world, pilot.ActiveShipId);
            state.JumpModuleId = moduleId;
            state.JumpStartedAt = now;
            state.JumpEndsAt = ends;
            state.HeadingX = heading.Unit.X;
            state.HeadingY = heading.Unit.Y;
            state.HeadingZ = heading.Unit.Z;
            state.VelocityX = velocity.X;
            state.VelocityY = velocity.Y;
            state.VelocityZ = velocity.Z;
            Cooldown(world, moduleId, ready);
        }
        world.SaveChanges();
        var updated = PilotFittingStore.ShipAttributes(world, catalog, pilotId);
        transaction.Commit();
        return new(module, now, ends, ready, capacitor, updated);
    }

    private static SpatialNavigationState State(WorldAction world, long ship) =>
        world.Set<SpatialNavigationState>().Find(ship) ?? world.Add(new SpatialNavigationState { ShipId = ship }).Entity;

    private static void CheckCloakGeometry(WorldAction world, CatalogDbContext catalog, Ship ship, long now)
    {
        var saved = ship.Position!;
        var origin = new SpacePosition(saved.X, saved.Y, saved.Z);
        int system = saved.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
        double radius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
        var live = LiveWorld.Find(world.WorldPath);
        bool Near(SpacePosition position, double otherRadius) => SpatialModuleRules.Proximity(origin, radius, position, otherRadius);
        bool NearBody(long id, int type, SpacePosition position) =>
            Near(live?.PublishedPosition(system, id) ?? position, catalog.RequireItem(type).Radius ?? 0);

        bool blocked =
            SystemObjects.Geometry(catalog, system).Any(o => o.Solid && Near(o.Position, o.Radius))
            || world
                .Filter<FlightPosition>(p => p.SystemId == system)
                .AsEnumerable()
                .Concat(world.Filter<FlightPosition>(p => p.SystemId == null))
                .Any(p =>
                    p.ShipId != ship.Id
                    && world.Ships.Find(p.ShipId) is { } other
                    && (p.SystemId ?? catalog.RequireStation(other.StationId).SolarSystemId) == system
                    && world.Set<SavedCapsuleerSuspension>().Find(other.OwnerId) is null
                    && NearBody(other.Id, other.TypeId, new(p.X, p.Y, p.Z))
                    && ReadStatus(world, catalog, other.Id).CloakModuleId == 0
                )
            || world
                .Filter<WorldNpc>(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
                .AsEnumerable()
                .Any(n => NearBody(n.Id, n.TypeId, new(n.X, n.Y, n.Z)))
            || world
                .Filter<WorldWreck>(w => w.SystemId == system && w.ExpiresAt > now)
                .AsEnumerable()
                .Any(w => NearBody(w.Id, w.TypeId, new(w.X, w.Y, w.Z)))
            || world
                .Filter<WorldDrone>(d => d.SystemId == system)
                .AsEnumerable()
                .Any(d => world.Set<OwnedItem>().Find(d.ItemId) is { } item && NearBody(d.ItemId, item.TypeId, new(d.X, d.Y, d.Z)))
            || world
                .Filter<WorldAsteroid>(a => a.SystemId == system && a.Quantity > 0)
                .AsEnumerable()
                .Any(a => Near(new(a.X, a.Y, a.Z), a.Radius));
        if (blocked)
        {
            throw new InvalidDataException("Move at least 2 km clear of nearby objects before cloaking.");
        }
    }

    private static void Cooldown(WorldAction world, long module, long ready)
    {
        var cooldown = world.Set<ModuleCooldown>().Find(module) ?? world.Add(new ModuleCooldown { ModuleId = module }).Entity;
        cooldown.ReadyAt = ready;
        cooldown.Kind = ModuleCooldownKind.Cycle;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        if (world.Ships.Find(shipId)?.OwnerId != pilotId)
        {
            return new Dictionary<DogmaAttributeId, double>();
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = world.Set<OwnedItem>().Find(moduleId);
        var active = world.Set<ActiveFittedModule>().Find(moduleId);
        var pending = world.Set<SpatialNavigationState>().Find(shipId);
        if (pending?.CloakModuleId == moduleId)
        {
            pending.TargetingReadyAt = SpatialModuleRules.Deadline(now, pending.CloakTargetingMilliseconds);
            if (item is not null)
            {
                Cooldown(world, moduleId, SpatialModuleRules.Deadline(now, pending.CloakReactivationMilliseconds));
            }
            pending.CloakModuleId = 0;
        }
        if (active?.ShipId == shipId)
        {
            world.Remove(active);
        }
        if (pending?.JumpModuleId == moduleId)
        {
            pending.JumpModuleId = 0;
        }
        world.SaveChanges();
        var values =
            world.Pilots.Find(pilotId)?.ActiveShipId == shipId
                ? PilotFittingStore.ShipAttributes(world, catalog, pilotId)
                : new Dictionary<DogmaAttributeId, double>();
        values[DogmaAttributeId.NextActivationTime] = world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt ?? 0;
        transaction.Commit();
        return values;
    }

    public MicroJumpResult? CompleteJump(long pilotId, long shipId, long moduleId, long startedAt, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        var pilot = world.Pilots.Find(pilotId);
        if (pilot?.ActiveShipId != shipId || pilot.ActiveShip.OwnerId != pilotId)
        {
            throw new InvalidDataException("Complete a micro jump only on your own active ship.");
        }
        var state = world.Set<SpatialNavigationState>().Find(shipId);
        if (state is null || state.JumpModuleId != moduleId || state.JumpStartedAt != startedAt || state.JumpEndsAt > now)
        {
            return null;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = world.Set<OwnedItem>().Find(moduleId);
        MicroJumpResult? result = null;
        if (
            pilot.ActiveShipId == shipId
            && pilot.ActiveShip.Position is { } position
            && item is { Online: true }
            && item.OwnerId == pilotId
            && item.LocationId == shipId
            && InventoryDefinitions.IsMediumSlot(item.FlagId)
            && !Scrambled(world, shipId, now)
            && ReadStatus(world, catalog, shipId).CloakModuleId == 0
        )
        {
            var destination = SpatialModuleRules.Jump(
                new(position.X, position.Y, position.Z),
                new(state.HeadingX, state.HeadingY, state.HeadingZ)
            );
            position.X = destination.X;
            position.Y = destination.Y;
            position.Z = destination.Z;
            result = new(destination, new(state.VelocityX, state.VelocityY, state.VelocityZ));
        }
        state.JumpModuleId = 0;
        if (world.Set<ActiveFittedModule>().Find(moduleId) is { } active && active.ShipId == shipId)
        {
            world.Remove(active);
        }
        world.SaveChanges();
        transaction.Commit();
        return result;
    }

    internal static void Reconcile(WorldDbContext world, long now)
    {
        foreach (var state in world.Set<SpatialNavigationState>())
        {
            if (state.CloakModuleId != 0)
            {
                state.TargetingReadyAt = SpatialModuleRules.Deadline(now, state.CloakTargetingMilliseconds);
                if (world.Set<OwnedItem>().Any(i => i.Id == state.CloakModuleId))
                {
                    Cooldown(
                        new SqlWorldAction(world),
                        state.CloakModuleId,
                        SpatialModuleRules.Deadline(now, state.CloakReactivationMilliseconds)
                    );
                }
                state.CloakModuleId = 0;
            }
            state.JumpModuleId = 0;
        }
        world.SaveChanges();
    }

    internal static void RequireUncloaked(WorldDbContext world, long ship)
    {
        if (world.Set<SpatialNavigationState>().Any(s => s.ShipId == ship && s.CloakModuleId != 0))
        {
            throw new InvalidDataException("Decloak before moving inventory, reloading or changing overload settings.");
        }
    }
}
