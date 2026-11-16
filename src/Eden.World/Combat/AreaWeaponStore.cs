using System.Collections.Concurrent;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Combat;

[Injectable(InjectionType.Singleton)]
internal sealed partial class AreaWeaponStore(
    WorldDatabase database,
    TimeProvider clock,
    INpcEwarRoll rolls,
    IPilotLockBreaks locks,
    PilotLossSettlement losses,
    SqliteWorld lookup,
    IPilotDamageEvents damageEvents,
    SecurityStatusSignal securityStatus,
    ICooperativeFleets fleets,
    IPilotCombatPresence presence
) : IPilotAreaWeapons
{
    private readonly ConcurrentDictionary<long, long> _impacts = [];

    public bool Impacted(long projectileId) =>
        _impacts.TryGetValue(projectileId, out long until) && clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() < until;

    private LiveWorld Live =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Area weapons require the resident world.");

    public bool IsLauncher(long pilotId, long moduleId) =>
        LiveWorld
            .Find(database.Path)
            ?.Economy.Inspect(world =>
            {
                var item = world.Set<OwnedItem>().Find(moduleId);
                using CatalogDbContext catalog = new(database.CatalogPath);
                return item?.OwnerId == pilotId
                    && catalog.FittingType(item.TypeId)?.GroupId
                        is AreaWeaponRules.BombLauncherGroup
                            or AreaWeaponRules.DefenderLauncherGroup;
            })
        ?? false;

    public AreaWeaponActivation Activate(
        long pilotId,
        long shipId,
        long moduleId,
        DogmaEffect effect,
        SpacePosition direction,
        SpacePosition velocity,
        SpacePosition? point,
        long now
    )
    {
        int system = Live.Economy.Inspect(world =>
        {
            var source = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
            using CatalogDbContext catalog = new(database.CatalogPath);
            return CrimewatchPersistence.System(catalog, source.ActiveShip);
        });
        using var transaction = Live.Economy.Begin(Participants(system));
        var world = (WorldAction)transaction;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.RequireItem(item.TypeId).GroupId;
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { } saved
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !(
                group == AreaWeaponRules.BurstJammerGroup
                    ? InventoryDefinitions.IsMediumSlot(item.FlagId)
                    : InventoryDefinitions.IsHighSlot(item.FlagId)
            )
            || !AreaWeaponCatalog.Module(catalog, item.TypeId, group)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted area weapon on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        if (CrimewatchPersistence.System(catalog, pilot.ActiveShip) != system || presence.IsAreaProtected(system, shipId))
        {
            throw new InvalidDataException("The source ship is travelling or has changed systems.");
        }
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        if (world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now)
        {
            throw new InvalidDataException("The area weapon is still cycling, reloading or waiting for reactivation.");
        }
        bool overloaded = PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        var calculation = FittingCalculation.Read(
            world,
            catalog,
            pilot,
            overloadedModules: overloaded ? new HashSet<long> { moduleId } : null
        );
        var attributes = calculation.Graph.Attributes(moduleId);
        var module = AreaWeaponRules.Define(moduleId, item.TypeId, group, effect, attributes);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(ship), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate area weapons.");
        }
        double security = CrimewatchPersistence.Security(catalog, system);
        if (module.Kind == AreaWeaponKind.BurstJammer)
        {
            bool alpha = world.Set<Account>().Find(pilot.AccountId)!.CloneGrade == AccountCloneGrade.Alpha;
            SmartbombRules.CheckSafety(security, pilot.SafetyLevel, alpha);
        }
        else if (security > 0)
        {
            throw new InvalidDataException("This area weapon requires null-security or wormhole space.");
        }
        SpacePosition origin = new(saved.X, saved.Y, saved.Z);
        List<InventoryChange> inventoryChanges = [];
        if (module.Kind == AreaWeaponKind.Projector)
        {
            if (point is null || !Finite(point) || origin.DistanceTo(point) > module.Range)
            {
                throw new InvalidDataException("Choose a finite point within the projector's range.");
            }
            SpendFuel(world, catalog, pilotId, shipId, module.FuelType, module.FuelQuantity, inventoryChanges);
        }
        else if (point is not null)
        {
            throw new InvalidDataException("This weapon does not accept a target point.");
        }
        MagazineChange? magazine = null;
        BombProfile? bomb = null;
        SavedAreaWeapon? intercepted = null;
        var loaded = PilotAmmunitionStore.Contents(world, moduleId);
        if (module.Kind is AreaWeaponKind.Bomb or AreaWeaponKind.Defender)
        {
            if (
                loaded is not { Quantity: > 0 }
                || loaded.OwnerId != pilotId
                || !AreaWeaponCatalog.Charge(catalog, loaded.TypeId, catalog.RequireItem(loaded.TypeId).GroupId)
                || (module.Kind == AreaWeaponKind.Bomb) != AreaWeaponRules.IsBomb(catalog.RequireItem(loaded.TypeId).GroupId)
            )
            {
                throw new InvalidDataException("Load compatible ammunition before activating the launcher.");
            }
            PilotSkillReader.CheckRequirements(world, catalog, pilotId, loaded.TypeId);
            if (module.Kind == AreaWeaponKind.Bomb)
            {
                if (
                    !Finite(direction)
                    || direction.Length <= 0
                    || !Finite(velocity)
                    || velocity.Length < attributes.GetValueOrDefault(DogmaAttributeId.MinimumActivationVelocity)
                )
                {
                    throw new InvalidDataException("Move the ship in a valid launch direction before launching a bomb.");
                }
                bomb = AreaWeaponCatalog.Bomb(catalog, loaded.TypeId);
                // Fitted charge nodes include the hull's bomb damage bonus.
                var chargeAttributes = calculation.Graph.Attributes(loaded.Id);
                bomb = bomb with
                {
                    Damage = new(
                        chargeAttributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
                        chargeAttributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                        chargeAttributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                        chargeAttributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
                    ),
                };
            }
            else
            {
                intercepted = SelectBomb(world, catalog, pilot, origin, calculation.Graph.Attributes(loaded.Id), now);
            }
            if (module.Kind == AreaWeaponKind.Bomb || intercepted is not null)
            {
                Magazine before = PilotAmmunitionStore.Read(world, item, loaded);
                magazine = new(before, before with { Quantity = before.Quantity - 1 });
                loaded.Quantity--;
                if (loaded.Quantity == 0)
                {
                    world.Remove(loaded);
                }
            }
        }
        double capacitor = ShipResources.SpendCapacitor(world, shipId, ship, module.CapacitorNeed, now);
        long end = AreaWeaponRules.Deadline(now, module.Duration);
        long ready = AreaWeaponRules.Deadline(end, module.Reactivation);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId) ?? world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = ready;
        var timer = CrimewatchPersistence.State(world, pilotId);
        timer.SystemId = system;
        var timers = timer.Timers();
        timer.Apply(timers with { Weapons = CrimewatchRules.Extend(timers.Weapons, end, CrimewatchRules.WeaponsDuration) });
        long? flightId = null;
        List<long> broken = [];
        if (module.Kind == AreaWeaponKind.BurstJammer)
        {
            Burst(world, catalog, pilot, module, origin, system, now, broken);
        }
        else if (module.Kind == AreaWeaponKind.Projector || bomb is not null || intercepted is not null)
        {
            var travel = bomb is not null ? direction.Unit * bomb.Speed : SpacePosition.Zero;
            long resolves = AreaWeaponRules.Deadline(now, bomb?.Lifetime ?? module.Delay);
            if (intercepted is not null)
            {
                var chargeAttributes = calculation.Graph.Attributes(loaded!.Id);
                double speed = chargeAttributes.GetValueOrDefault(DogmaAttributeId.MaxVelocity);
                var interceptPoint = AreaWeaponRules.Intercept(origin, intercepted.Flight(), speed, now);
                if (interceptPoint is null)
                {
                    throw new InvalidDataException("The selected bomb cannot be intercepted.");
                }
                point = interceptPoint;
                travel = (point - origin).Unit * speed;
                resolves = AreaWeaponRules.Deadline(now, origin.DistanceTo(point) / speed * 1000);
            }
            SavedAreaWeapon row = new()
            {
                Id = world.Next("SavedAreaWeapon", AreaWeaponRules.FirstFlightId),
                PilotId = pilotId,
                SourceShipId = shipId,
                ModuleId = moduleId,
                SystemId = system,
            };
            row.Save(new(module, bomb, origin, travel, point ?? origin, now, resolves, intercepted?.Id ?? 0, bomb?.Defense.Undamaged));
            world.Add(row);
            flightId = row.Id;
        }
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, end) : null;
        transaction.Commit();
        foreach (long target in broken)
        {
            locks.BreakOutgoing(target);
        }
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, shipId, heat);
        }
        return new(module, ready, capacitor, magazine, flightId, inventoryChanges);
    }

    private static bool Finite(SpacePosition point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);

    private static void SpendFuel(
        WorldAction world,
        CatalogDbContext catalog,
        long pilot,
        long ship,
        int type,
        int quantity,
        List<InventoryChange> changes
    )
    {
        var stacks = world
            .Filter<OwnedItem>(item =>
                item.OwnerId == pilot
                && item.LocationId == ship
                && item.FlagId == (int)InventoryFlag.Cargo
                && item.TypeId == type
                && !item.Singleton
            )
            .OrderBy(item => item.Id)
            .ToArray();
        if (stacks.Sum(item => item.Quantity) < quantity)
        {
            throw new InvalidDataException("The projector requires more fuel in ordinary cargo.");
        }
        foreach (var item in stacks)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            int consumed = Math.Min(quantity, item.Quantity);
            item.Quantity -= consumed;
            quantity -= consumed;
            changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
            if (quantity == 0)
            {
                break;
            }
        }
    }
}
