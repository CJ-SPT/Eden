using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
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
        if (LiveWorld.Find(database.Path) is { SqlConnections: 0 } live && live.Economy.IsLoaded)
        {
            using CatalogDbContext source = new(database.CatalogPath);
            return live.Economy.Inspect(world =>
            {
                var state = world.Set<SpatialNavigationState>().Find(shipId);
                var module = world
                    .Set<ActiveFittedModule>()
                    .Where(row => row.ShipId == shipId)
                    .Select(row => row.ModuleId)
                    .AsEnumerable()
                    .Select(id => world.Set<OwnedItem>().Find(id))
                    .FirstOrDefault(item =>
                        item is { Online: true, Singleton: true }
                        && item.LocationId == shipId
                        && InventoryDefinitions.IsHighSlot(item.FlagId)
                        && source.FittingType(item.TypeId)?.GroupId == SpatialModuleRules.CloakGroup
                    );
                return new SpatialStatus(
                    module?.Id ?? 0,
                    module is not null && FittingCatalog.DefaultEffect(source, module.TypeId) == DogmaEffect.CovertCloaking,
                    state is { CloakModuleId: not 0 } && module is null ? long.MaxValue : state?.TargetingReadyAt ?? 0
                );
            });
        }
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var state = world.Set<SpatialNavigationState>().Find(shipId);
        var types = catalog.Definitions.TypesInGroup(SpatialModuleRules.CloakGroup).ToArray();
        var module = (
            from active in world.Set<ActiveFittedModule>()
            join item in world.Set<OwnedItem>() on active.ModuleId equals item.Id
            where
                active.ShipId == shipId
                && item.LocationId == shipId
                && item.Online
                && item.Singleton
                && item.FlagId >= (int)InventoryFlag.HighSlot0
                && item.FlagId <= (int)InventoryFlag.HighSlot7
                && types.Contains(item.TypeId)
            select item
        ).FirstOrDefault();
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

    private static bool Scrambled(WorldDbContext world, long ship, long now) =>
        world
            .Set<WorldTackle>()
            .Any(t => t.TargetId == ship && t.TargetKind == TackleTargetKind.Ship && t.MwdBlock > 0 && t.ExpiresAt > now);

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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
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
        var status = ReadStatus(pilot.ActiveShipId);
        if (cloak)
        {
            CheckCloakGeometry(world, catalog, pilot.ActiveShip, now);
        }
        var cloakTypes = catalog.Definitions.TypesInGroup(SpatialModuleRules.CloakGroup).ToArray();
        if (
            cloak
            && world
                .Set<OwnedItem>()
                .Count(i =>
                    i.LocationId == pilot.ActiveShipId
                    && i.FlagId >= (int)InventoryFlag.HighSlot0
                    && i.FlagId <= (int)InventoryFlag.HighSlot7
                    && cloakTypes.Contains(i.TypeId)
                ) != 1
        )
        {
            throw new InvalidDataException("Fit only one cloaking device before activating it.");
        }
        if (
            status.CloakModuleId != 0
            || world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now
            || world.Set<ActiveFittedModule>().Any(a => a.ModuleId == moduleId)
        )
        {
            throw new InvalidDataException("The module is active, cloaked or waiting for reactivation.");
        }
        if (
            cloak
            && (
                locks?.IsTargeted(pilot.ActiveShipId) == true
                || world.Set<NpcAggression>().Any(a => a.TargetShipId == pilot.ActiveShipId && a.ReadyAt <= now)
            )
        )
        {
            throw new InvalidDataException("An established target lock prevents cloaking.");
        }
        if (
            !cloak
            && (
                Scrambled(world, pilot.ActiveShipId, now)
                || world.Set<SpatialNavigationState>().Any(s => s.ShipId == pilot.ActiveShipId && s.JumpModuleId != 0)
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
            world.Set<NpcAggression>().Where(a => a.TargetShipId == pilot.ActiveShipId).ExecuteDelete();
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

    private static SpatialNavigationState State(WorldDbContext world, long ship) =>
        world.Set<SpatialNavigationState>().Find(ship) ?? world.Add(new SpatialNavigationState { ShipId = ship }).Entity;

    private static void CheckCloakGeometry(WorldDbContext world, CatalogDbContext catalog, Ship ship, long now)
    {
        var saved = ship.Position!;
        var origin = new SpacePosition(saved.X, saved.Y, saved.Z);
        int system = saved.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
        double radius = catalog.RequireItem(ship.TypeId).Radius ?? 0;
        bool Near(SpacePosition position, double otherRadius) => SpatialModuleRules.Proximity(origin, radius, position, otherRadius);
        bool blocked = SystemObjects.Geometry(catalog, system).Any(o => o.Solid && Near(o.Position, o.Radius));
        if (!blocked)
        {
            var cloakTypes = catalog.Definitions.TypesInGroup(SpatialModuleRules.CloakGroup).ToArray();
            var cloaked = (
                from active in world.Set<ActiveFittedModule>()
                join item in world.Set<OwnedItem>() on active.ModuleId equals item.Id
                where item.Online && cloakTypes.Contains(item.TypeId)
                select active.ShipId
            ).ToHashSet();
            blocked = world
                .Ships.Include(s => s.Position)
                .Where(s => !world.Set<SavedCapsuleerSuspension>().Any(pause => pause.PilotId == s.OwnerId))
                .Where(s => s.Id != ship.Id && s.Position != null)
                .AsEnumerable()
                .Any(s =>
                    !cloaked.Contains(s.Id)
                    && (s.Position!.SystemId ?? catalog.RequireStation(s.StationId).SolarSystemId) == system
                    && Near(new(s.Position.X, s.Position.Y, s.Position.Z), catalog.RequireItem(s.TypeId).Radius ?? 0)
                );
        }
        blocked |= world
            .Set<WorldNpc>()
            .Where(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
            .AsEnumerable()
            .Any(n => Near(new(n.X, n.Y, n.Z), catalog.RequireItem(n.TypeId).Radius ?? 0));
        blocked |= world
            .Set<WorldWreck>()
            .Where(w => w.SystemId == system && w.ExpiresAt > now)
            .AsEnumerable()
            .Any(w => Near(new(w.X, w.Y, w.Z), catalog.RequireItem(w.TypeId).Radius ?? 0));
        var drones =
            from drone in world.Set<WorldDrone>()
            join item in world.Set<OwnedItem>() on drone.ItemId equals item.Id
            where drone.SystemId == system
            select new
            {
                drone.X,
                drone.Y,
                drone.Z,
                item.TypeId,
            };
        blocked |= drones.AsEnumerable().Any(d => Near(new(d.X, d.Y, d.Z), catalog.RequireItem(d.TypeId).Radius ?? 0));
        blocked |= world
            .Set<WorldAsteroid>()
            .Where(a => a.SystemId == system && a.Quantity > 0)
            .AsEnumerable()
            .Any(a => Near(new(a.X, a.Y, a.Z), a.Radius));
        if (blocked)
        {
            throw new InvalidDataException("Move at least 2 km clear of nearby objects before cloaking.");
        }
    }

    private static void Cooldown(WorldDbContext world, long module, long ready)
    {
        var cooldown = world.Set<ModuleCooldown>().Find(module) ?? world.Add(new ModuleCooldown { ModuleId = module }).Entity;
        cooldown.ReadyAt = ready;
        cooldown.Kind = ModuleCooldownKind.Cycle;
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (!world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            return new Dictionary<DogmaAttributeId, double>();
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = world.Set<OwnedItem>().Find(moduleId);
        var active = world.Set<ActiveFittedModule>().Find(moduleId);
        var pending = world.Set<SpatialNavigationState>().Find(shipId);
        if (pending?.CloakModuleId == moduleId && world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            pending.TargetingReadyAt = SpatialModuleRules.Deadline(now, pending.CloakTargetingMilliseconds);
            if (item is not null)
            {
                Cooldown(world, moduleId, SpatialModuleRules.Deadline(now, pending.CloakReactivationMilliseconds));
            }
            pending.CloakModuleId = 0;
        }
        if (active?.ShipId == shipId && world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            world.Remove(active);
        }
        if (pending?.JumpModuleId == moduleId)
        {
            pending.JumpModuleId = 0;
        }
        world.SaveChanges();
        var values = world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == shipId)
            ? PilotFittingStore.ShipAttributes(world, catalog, pilotId)
            : new Dictionary<DogmaAttributeId, double>();
        values[DogmaAttributeId.NextActivationTime] = world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt ?? 0;
        transaction.Commit();
        return values;
    }

    public MicroJumpResult? CompleteJump(long pilotId, long shipId, long moduleId, long startedAt, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (!world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == shipId && p.ActiveShip.OwnerId == pilotId))
        {
            throw new InvalidDataException("Complete a micro jump only on your own active ship.");
        }
        var state = world.Set<SpatialNavigationState>().Find(shipId);
        if (state is null || state.JumpModuleId != moduleId || state.JumpStartedAt != startedAt || state.JumpEndsAt > now)
        {
            return null;
        }
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
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
            && ReadStatus(shipId).CloakModuleId == 0
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
        world.Set<ActiveFittedModule>().Where(a => a.ModuleId == moduleId && a.ShipId == shipId).ExecuteDelete();
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
                    Cooldown(world, state.CloakModuleId, SpatialModuleRules.Deadline(now, state.CloakReactivationMilliseconds));
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
