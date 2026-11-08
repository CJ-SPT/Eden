using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

[Injectable]
internal sealed class PilotTackleStore(WorldDatabase database, TimeProvider clock) : IPilotTackle
{
    public TackleActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, target, lockedTargets, now, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal TackleActivation ActivateOverloaded(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, target, lockedTargets, now, true);

    private TackleActivation ActivateCycle(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> lockedTargets,
        long now,
        bool overloaded
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.RequireItem(item.TypeId).GroupId;
        bool core = effect == DogmaEffect.WarpCoreStabilizer;
        if (
            pilot.ActiveShip.Position is not { } position
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !item.Singleton
            || !item.Online
            || !(core ? InventoryDefinitions.IsLowSlot(item.FlagId) : InventoryDefinitions.IsMediumSlot(item.FlagId))
            || !TackleCatalog.Supported(catalog, item.TypeId, group)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted tackle module on your active ship in space.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        var calculation = FittingCalculation.Read(
            world,
            catalog,
            pilot,
            overloadedModules: overloaded ? new HashSet<long> { moduleId } : null
        );
        var module = TackleDefinitions.Define(item.Id, item.TypeId, effect, calculation.Graph.Attributes(item.Id));
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate tackle equipment.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown is not null && cooldown.ReadyAt > now)
        {
            throw new InvalidDataException("The module is still cycling or waiting for reactivation.");
        }
        int system = System(catalog, pilot.ActiveShip);
        WorldNpc? npc = null;
        AggressionDecision? aggression = null;
        long victimId = 0;
        if (core)
        {
            if (target is not null && (target.Kind != TackleTargetKind.Ship || target.Id != pilot.ActiveShipId))
            {
                throw new InvalidDataException("Activate the warp core stabilizer on your own ship.");
            }
        }
        else
        {
            if (target is null || target.Id == pilot.ActiveShipId || !lockedTargets.Contains(target.Id))
            {
                throw new InvalidDataException("Lock a nearby ship or NPC before applying tackle.");
            }
            SpacePosition targetPosition;
            int targetType;
            if (target.Kind == TackleTargetKind.Npc)
            {
                npc = world.Set<WorldNpc>().Find(target.Id);
                if (
                    npc is null
                    || npc.DestroyedAt.HasValue
                    || npc.RetiredAt.HasValue
                    || npc.SystemId != system
                    || npc.Navigation == NpcNavigationState.Warping
                )
                {
                    throw new InvalidDataException("The NPC is unavailable.");
                }
                targetPosition = NpcMotion.Position(npc);
                targetType = npc.TypeId;
            }
            else
            {
                var victim = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .SingleOrDefault(p => p.ActiveShipId == target.Id);
                if (
                    victim?.ActiveShip.Position is not { } saved
                    || System(catalog, victim.ActiveShip) != system
                    || world.Set<ShipLoss>().Any(l => l.ShipId == victim.ActiveShipId)
                    || world.Set<SavedLogoff>().Any(l => l.PilotId == victim.Id && l.ExpiresAt <= now)
                )
                {
                    throw new InvalidDataException("The target ship is unavailable.");
                }
                targetPosition = new(saved.X, saved.Y, saved.Z);
                targetType = victim.ActiveShip.TypeId;
                aggression = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, victim, system, now);
                victimId = victim.Id;
            }
            double sourceRadius = catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0;
            double targetRadius = catalog.FittingType(targetType)!.Radius ?? 0;
            double distance = Math.Max(
                0,
                new SpacePosition(position.X, position.Y, position.Z).DistanceTo(targetPosition) - sourceRadius - targetRadius
            );
            if (
                distance > module.Range
                || distance > TackleDefinitions.MaximumDistance
                || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
            )
            {
                throw new InvalidDataException("The target is outside tackle range.");
            }
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        long expires = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        if (aggression is not null)
        {
            CrimewatchPersistence.Player(world, pilotId, victimId, system, aggression, now, expires);
        }
        else if (npc is not null)
        {
            CrimewatchPersistence.Npc(world, pilotId, system, expires);
        }
        if (core)
        {
            if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
            {
                world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
            }
        }
        else
        {
            var row = world.Set<WorldTackle>().Find(moduleId) ?? world.Set<WorldTackle>().Add(new() { ModuleId = moduleId }).Entity;
            Eden.World.Drones.DroneCombatPersistence.Module(world, pilot.ActiveShipId, target!.Id, aggression is not null, now);
            row.PilotId = pilotId;
            row.SourceShipId = pilot.ActiveShipId;
            row.TypeId = item.TypeId;
            row.SystemId = system;
            row.TargetId = target!.Id;
            row.TargetKind = target.Kind;
            row.Effect = module.Effect;
            row.DurationMilliseconds = module.DurationMilliseconds;
            row.CapacitorNeed = module.CapacitorNeed;
            row.Range = module.Range;
            row.Strength = module.Strength;
            row.WebPercent = module.WebPercent;
            row.MwdBlock = module.MwdBlock;
            row.StartedAt = now;
            row.ExpiresAt = expires;
            if (npc is not null)
            {
                NpcHostility.Record(world, catalog, npc, pilot.ActiveShipId, now);
            }
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(expires + (long)Math.Ceiling(module.ReactivationMilliseconds * TimeSpan.TicksPerMillisecond));
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, expires) : null;
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(module, core ? null : target, capacitor, cooldown.ReadyAt);
    }

    private static int System(CatalogDbContext catalog, Ship ship) =>
        ship.Position!.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;

    public void Sweep(long now)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.tackleSweep");
        if (LiveReadModels.Find(database.Path) is { } resident && !resident.HasTackleWork())
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        if (!world.Set<WorldTackle>().Any() && !world.Set<ActiveFittedModule>().Any())
        {
            return;
        }
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        foreach (var active in world.Set<ActiveFittedModule>().ToArray())
        {
            var item = world.Set<OwnedItem>().Find(active.ModuleId)!;
            if (catalog.FittingType(item.TypeId)!.GroupId != TackleDefinitions.WarpCoreStabilizerGroup)
            {
                continue;
            }
            var cooldown = world.Set<ModuleCooldown>().Find(item.Id);
            double delay = PilotFittingStore
                .ReadAttributes(catalog, item.TypeId)
                .GetValueOrDefault(DogmaAttributeId.ModuleReactivationDelay);
            // Core strength ends with its paid cycle, independently of when the
            // owning connection next advances. Its reactivation deadline remains.
            if (cooldown is null || cooldown.ReadyAt - delay * TimeSpan.TicksPerMillisecond <= now)
            {
                world.Remove(active);
            }
        }
        foreach (var row in world.Set<WorldTackle>().ToArray())
        {
            var source = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == row.PilotId);
            var item = world.Set<OwnedItem>().Find(row.ModuleId);
            // A connection owns each remote cycle. Its controller atomically
            // renews the paid row or stops it. Another connection must not
            // expire that row before the owner has made its repeat decision.
            bool valid =
                source.ActiveShipId == row.SourceShipId
                && source.ActiveShip.Position is not null
                && System(catalog, source.ActiveShip) == row.SystemId
                && item is { Online: true }
                && item.OwnerId == row.PilotId
                && item.LocationId == row.SourceShipId
                && InventoryDefinitions.IsMediumSlot(item.FlagId)
                && !world.Set<ShipLoss>().Any(l => l.ShipId == row.SourceShipId);
            if (valid && row.TargetKind == TackleTargetKind.Npc)
            {
                valid = world
                    .Set<WorldNpc>()
                    .Any(n => n.Id == row.TargetId && n.DestroyedAt == null && n.RetiredAt == null && n.SystemId == row.SystemId);
            }
            else if (valid)
            {
                var target = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .SingleOrDefault(p => p.ActiveShipId == row.TargetId);
                valid =
                    target?.ActiveShip.Position is not null
                    && System(catalog, target.ActiveShip) == row.SystemId
                    && !world.Set<ShipLoss>().Any(l => l.ShipId == row.TargetId);
                valid = valid && !world.Set<SavedLogoff>().Any(l => l.ShipId == row.TargetId && l.ExpiresAt <= now);
            }
            if (!valid)
            {
                Remove(world, row, now);
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public IReadOnlyList<TackleEffect> Incoming(long shipId)
    {
        return PlayerIncoming(shipId)
            .Concat(
                (LiveReadModels.Find(database.Path)?.NpcEwarIncoming(shipId) ?? ReadNpcEwar(shipId))
                    .Where(e => e.Ability.Kind is NpcEwarKind.Webifying or NpcEwarKind.WarpDisruption)
                    .Select(e => new TackleEffect(
                        0,
                        e.SourceId,
                        new(
                            e.SourceId,
                            e.SourceTypeId,
                            e.Ability.Kind == NpcEwarKind.Webifying ? DogmaEffect.StasisWebifier : DogmaEffect.WarpDisruptor,
                            e.Ability.DurationMilliseconds,
                            0,
                            e.Ability.Optimal,
                            e.Applied.WarpStrength,
                            e.Applied.WebPercent,
                            0,
                            0
                        ),
                        new(shipId, TackleTargetKind.Ship),
                        e.StartedAt,
                        e.ReadyAt
                    ))
            )
            .ToArray();
    }

    private IReadOnlyList<NpcEwarContact> ReadNpcEwar(long shipId)
    {
        using WorldDbContext world = new(database.Path);
        return NpcEwarStore.Active(world, shipId);
    }

    private IReadOnlyList<TackleEffect> PlayerIncoming(long shipId)
    {
        if (LiveReadModels.Find(database.Path) is { } resident)
        {
            return resident.Incoming(shipId);
        }
        using WorldDbContext world = new(database.Path);
        return world
            .Set<WorldTackle>()
            .Where(t => t.TargetId == shipId && t.TargetKind == TackleTargetKind.Ship)
            .OrderBy(t => t.ModuleId)
            .AsEnumerable()
            .Select(t => t.Contact())
            .ToArray();
    }

    public IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return PilotFittingStore.ShipAttributes(world, catalog, pilotId);
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (
            world.Set<WorldTackle>().SingleOrDefault(t => t.ModuleId == moduleId && t.PilotId == pilotId && t.SourceShipId == shipId) is
            { } row
        )
        {
            Remove(world, row, Now);
        }
        if (world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            world.Set<ActiveFittedModule>().Where(m => m.ModuleId == moduleId && m.ShipId == shipId).ExecuteDelete();
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void Cancel(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var rows = world
            .Set<WorldTackle>()
            .Where(t => t.PilotId == pilotId && t.SourceShipId == shipId || t.TargetKind == TackleTargetKind.Ship && t.TargetId == shipId)
            .ToArray();
        long now = Now;
        foreach (var row in rows)
        {
            Remove(world, row, now);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void Disconnect(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        long now = Now;
        foreach (var row in world.Set<WorldTackle>().Where(t => t.PilotId == pilotId && t.SourceShipId == shipId).ToArray())
        {
            Remove(world, row, now);
        }
        world.SaveChanges();
        transaction.Commit();
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void Remove(WorldDbContext world, WorldTackle row, long now)
    {
        ShipHeatPersistence.CancelSource(
            world,
            row.PilotId,
            row.SourceShipId,
            new HeatCycleKey(row.ModuleId, row.StartedAt, row.ExpiresAt),
            now
        );
        world.Remove(row);
    }

    internal static ILookup<long, WorldTackle> NpcTackles(WorldDbContext world, long[] ids) =>
        world
            .Set<WorldTackle>()
            .AsNoTracking()
            .Where(t => t.TargetKind == TackleTargetKind.Npc && ids.Contains(t.TargetId))
            .ToArray()
            .ToLookup(t => t.TargetId);

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldDbContext world,
        long npcId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0
    ) =>
        PlayerEwarStore.NpcAttributes(
            world,
            npcId,
            TackleDefinitions.NpcAttributes(
                attributes,
                world
                    .Set<WorldTackle>()
                    .Where(t => t.TargetKind == TackleTargetKind.Npc && t.TargetId == npcId)
                    .AsEnumerable()
                    .Select(t => t.Module())
                    .ToArray()
            ),
            missileRange
        );
}
