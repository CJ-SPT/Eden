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
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Propulsion;

internal sealed partial class PilotInterdictionStore
{
    // HIC fields are owned by the generator's paid cycle, not destructible probe hulls.
    private const double ActiveFieldMarker = 1;

    public InterdictionActivation ActivateGenerator(
        long pilotId,
        long shipId,
        long moduleId,
        DogmaEffect effect,
        TackleTarget? target,
        IReadOnlySet<long> locked,
        long now
    )
    {
        using var transaction = Live.Economy.Begin(
            new(
                pilots: [pilotId],
                ships: target?.Kind == TackleTargetKind.Ship ? [shipId, target.Id] : [shipId],
                resources: target?.Kind == TackleTargetKind.Npc ? [new(SharedSimulationResource.Npc, target.Id)] : []
            )
        );
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
            || !InterdictionCatalog.Generator(catalog, item.TypeId)
        )
        {
            throw new InvalidDataException("Use an online warp disruption field generator on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The field generator is still cycling or reloading.");
        }
        var charge = PilotAmmunitionStore.Contents(world, moduleId);
        DogmaEffect appliedEffect =
            charge is null ? DogmaEffect.WarpDisruptSphere
            : InterdictionCatalog.Script(catalog, charge.TypeId, catalog.RequireItem(charge.TypeId).GroupId)
            && charge.Quantity == 1
            && charge.OwnerId == pilotId
                ? FittingCatalog.DefaultEffect(catalog, charge.TypeId)!.Value
            : throw new InvalidDataException("Invalid focused script.");
        // Build 3662168 picks the target using the script, then activates the generator's default effect.
        if (
            (effect != DogmaEffect.WarpDisruptSphere && effect != appliedEffect)
            || InterdictionRules.Focused(appliedEffect) != (target is not null)
        )
        {
            throw new InvalidDataException("Use an unscripted field without a target or the loaded focused script with a locked target.");
        }
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        if (appliedEffect == DogmaEffect.WarpDisruptSphere)
        {
            InterdictionRules.CheckSystem(system, CrimewatchPersistence.Security(catalog, system));
        }
        bool overloaded = PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        var calculation = FittingCalculation.Read(
            world,
            catalog,
            pilot,
            overloadedModules: overloaded ? new HashSet<long> { moduleId } : null
        );
        var module = InterdictionGenerator.Define(calculation.Graph.Attributes(moduleId));
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate a field generator.");
        }
        long expires = InterdictionRules.Deadline(now, module.Duration);
        AggressionDecision? aggression = null;
        long victim = 0;
        WorldNpc? npc = null;
        if (target is not null)
        {
            if (target.Id == shipId || !locked.Contains(target.Id))
            {
                throw new InvalidDataException("Lock another ship before using a focused script.");
            }
            SpacePosition destination;
            int targetType;
            if (target.Kind == TackleTargetKind.Npc)
            {
                npc = world.Set<WorldNpc>().Find(target.Id);
                if (
                    npc is null
                    || npc.SystemId != system
                    || npc.DestroyedAt is not null
                    || npc.RetiredAt is not null
                    || npc.Navigation == NpcNavigationState.Warping
                )
                {
                    throw new InvalidDataException("The focused target is unavailable.");
                }
                destination = NpcMotion.Position(npc);
                targetType = npc.TypeId;
            }
            else
            {
                var recipient = world.Pilots.SingleOrDefault(row => row.ActiveShipId == target.Id);
                if (
                    recipient?.ActiveShip.Position is not { } saved
                    || CrimewatchPersistence.System(catalog, recipient.ActiveShip) != system
                    || world.Set<ShipLoss>().Find(target.Id) is not null
                    || world.Filter<SavedLogoff>(row => row.ShipId == target.Id && row.ExpiresAt <= now).Any()
                )
                {
                    throw new InvalidDataException("The focused target is unavailable.");
                }
                destination = new(saved.X, saved.Y, saved.Z);
                targetType = recipient.ActiveShip.TypeId;
                aggression = CrimewatchPersistence.CheckPlayer(world, catalog, pilot, recipient, system, now);
                victim = recipient.Id;
            }
            double distance = Math.Max(
                0,
                new SpacePosition(position.X, position.Y, position.Z).DistanceTo(destination)
                    - (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0)
                    - (catalog.RequireItem(targetType).Radius ?? 0)
            );
            if (
                distance > module.Range
                || distance > TackleDefinitions.MaximumDistance
                || distance > ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange)
            )
            {
                throw new InvalidDataException("The focused target is outside the generator's range.");
            }
        }
        double capacitor = ShipResources.SpendCapacitor(world, shipId, ship, module.Capacitor, now);
        var active = world.Set<ActiveFittedModule>().Find(moduleId) ?? world.Add(new ActiveFittedModule { ModuleId = moduleId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = expires;
        if (target is null)
        {
            foreach (
                var previous in world
                    .Filter<SavedInterdictionField>(row => row.ModuleId == moduleId && row.Kind == InterdictionKind.HeavyField)
                    .ToArray()
            )
            {
                world.Remove(previous);
            }
            world.Add(
                new SavedInterdictionField
                {
                    Id = world.Next("SavedInterdictionField", InterdictionRules.FirstProbeId),
                    PilotId = pilotId,
                    SourceShipId = shipId,
                    ModuleId = moduleId,
                    TypeId = item.TypeId,
                    SystemId = system,
                    Kind = InterdictionKind.HeavyField,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    Radius = module.Range,
                    HitPoints = ActiveFieldMarker,
                    StartedAt = now,
                    ExpiresAt = expires,
                }
            );
            var timers = CrimewatchPersistence.State(world, pilotId);
            timers.SystemId = system;
            var state = timers.Timers();
            timers.Apply(state with { Weapons = CrimewatchRules.Extend(state.Weapons, now, CrimewatchRules.WeaponsDuration) });
        }
        else
        {
            var row = world.Set<WorldTackle>().Find(moduleId) ?? world.Add(new WorldTackle { ModuleId = moduleId }).Entity;
            row.PilotId = pilotId;
            row.SourceShipId = shipId;
            row.TypeId = item.TypeId;
            row.SystemId = system;
            row.TargetId = target.Id;
            row.TargetKind = target.Kind;
            row.Effect = appliedEffect;
            row.DurationMilliseconds = module.Duration;
            row.CapacitorNeed = module.Capacitor;
            row.Range = module.Range;
            row.Strength = module.Strength;
            row.MwdBlock =
                appliedEffect == DogmaEffect.FocusedWarpScrambling
                    ? calculation.Graph.Read(moduleId, DogmaAttributeId.ActivationBlockStrength)
                    : 0;
            row.StartedAt = now;
            row.ExpiresAt = expires;
            Eden.World.Drones.DroneCombatPersistence.Module(world, shipId, target.Id, aggression is not null, now);
            if (aggression is not null)
            {
                CrimewatchPersistence.Player(world, catalog, pilotId, victim, system, aggression, now, expires);
            }
            else if (npc is not null)
            {
                CrimewatchPersistence.Npc(world, pilotId, system, expires);
                NpcHostility.Record(world, catalog, npc, shipId, now);
            }
        }
        cooldown ??= world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = expires;
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, expires) : null;
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, shipId, heat);
        }
        return new(
            moduleId,
            item.TypeId,
            appliedEffect,
            module.Duration,
            expires,
            expires,
            capacitor,
            TargetId: target?.Id,
            Range: module.Range
        );
    }

    public bool Active(long shipId, long moduleId, long now) =>
        Live.Economy.Inspect(world =>
            world.Set<ActiveFittedModule>().Find(moduleId) is { } active
            && active.ShipId == shipId
            && active.ExpiresAt > now
            && world.Set<OwnedItem>().Find(moduleId) is { Online: true } item
            && item.LocationId == shipId
            && world.Ships.Find(shipId) is { Position: not null }
            && world.Set<ShipLoss>().Find(shipId) is null
        );

    public void Stop(long pilotId, long shipId, long moduleId, long now)
    {
        using var ownership = Live.EnterShared("interdiction stop");
        var edge = Live.Economy.Peek<WorldTackle>(moduleId);
        using var transaction = Live.Economy.Begin(
            new(pilots: [pilotId], ships: edge?.TargetKind == TackleTargetKind.Ship ? [shipId, edge.TargetId] : [shipId])
        );
        var item = transaction.Set<OwnedItem>().Find(moduleId);
        if (item?.OwnerId != pilotId || item.LocationId != shipId)
        {
            return;
        }
        if (transaction.Set<ActiveFittedModule>().Find(moduleId) is { } active)
        {
            transaction.Remove(active);
        }
        if (transaction.Set<WorldTackle>().Find(moduleId) is { } tackle && InterdictionRules.Focused(tackle.Effect))
        {
            transaction.Remove(tackle);
        }
        transaction.RemoveRange(
            transaction.Filter<SavedInterdictionField>(row => row.ModuleId == moduleId && row.Kind == InterdictionKind.HeavyField)
        );
        ShipHeatPersistence.CancelSource(transaction, pilotId, shipId, moduleId, now);
        transaction.Commit();
    }
}
