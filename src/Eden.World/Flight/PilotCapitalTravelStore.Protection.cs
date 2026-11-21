using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fleets;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Thermal;

namespace Eden.World.Flight;

internal sealed partial class PilotCapitalTravelStore
{
    public IndustrialProtectionActivation ProtectIndustry(long pilotId, long shipId, long moduleId, long asteroid)
    {
        using var selection = Live.Enter();
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        long[] fleet = Live.Economy.Inspect(world =>
        {
            var source = ActivePilot(world, pilotId, shipId);
            int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
            var candidates = world
                .Filter<Eden.Data.World.Docking.FlightPosition>(row => row.SystemId == system)
                .AsEnumerable()
                .Select(row => world.Ships.Find(row.ShipId)?.OwnerId)
                .OfType<long>()
                .Distinct()
                .ToArray();
            return fleets.MembersInFleet(pilotId, candidates).ToArray();
        });
        using var action = Live.Economy.Begin(new(pilots: fleet.Append(pilotId), ships: [shipId]));
        var pilot = ActivePilot(action, pilotId, shipId);
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        var item = action.Set<OwnedItem>().Find(moduleId);
        if (
            pilot.ActiveShip.TypeId != UtilityDroneRules.Rorqual
            || item is not { Online: true, Singleton: true, TypeId: IndustrialProtectionRules.Module }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != DogmaEffect.IndustrialInvulnerability
        )
        {
            throw new InvalidDataException("Use an online P.A.N.I.C. module fitted to your active Rorqual.");
        }
        if (
            action.Set<WorldAsteroid>().Find(asteroid) is not { Quantity: > 0 } body
            || body.SystemId != system
            || catalog.RequireItem(body.TypeId).CategoryId != AsteroidDefinitions.Category
            || catalog.RequireItem(body.TypeId).GroupId == Eden.Gameplay.Mining.MiningDefinitions.GasCloudGroup
        )
        {
            throw new InvalidDataException("Lock a nearby ore or ice asteroid before activating P.A.N.I.C.");
        }
        action.RequireUncloaked(shipId);
        if (
            action.TetherProtected(shipId, now)
            || ProtectedIndustry(action, shipId, now)
            || action.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now
        )
        {
            throw new InvalidDataException("P.A.N.I.C. cannot start during tether, protection or reactivation delay.");
        }
        PilotSkillReader.CheckRequirements(action, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(action, catalog, moduleId, now);
        var hull = PilotFittingStore.ShipAttributes(action, catalog, pilot);
        RequireLiving(action, pilot, hull, now);
        var attributes = catalog.FittingAttributes(item.TypeId);
        double duration =
            attributes[DogmaAttributeId.IndustrialProtectionDuration]
            * (
                1
                + PilotSkillReader.Level(action, catalog, pilotId, IndustrialProtectionRules.OperationSkill)
                    * catalog.FittingAttributes(IndustrialProtectionRules.OperationSkill)[DogmaAttributeId.DurationBonus]
                    / 100
            );
        long end = checked(now + (long)Math.Ceiling(duration * TimeSpan.TicksPerMillisecond));
        double radius = catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0;
        var position = pilot.ActiveShip.Position!;
        var center = new SpacePosition(position.X, position.Y, position.Z);
        if (center.DistanceTo(new(body.X, body.Y, body.Z)) - radius - body.Radius > hull.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            throw new InvalidDataException("The locked asteroid is outside targeting range.");
        }
        string buffs = JsonSerializer.Serialize(IndustrialProtectionRules.Buffs);
        Add(shipId);
        foreach (long member in fleet.Where(id => id != pilotId))
        {
            var target = action.Pilots.Find(member)?.ActiveShip;
            if (
                target?.Position is not { } inSpace
                || CrimewatchPersistence.System(catalog, target) != system
                || action.TetherProtected(target.Id, now)
            )
            {
                continue;
            }
            var type = catalog.RequireItem(target.TypeId);
            if (
                IndustrialProtectionRules.Eligible((HullClass)type.GroupId, type.Id)
                && center.DistanceTo(new(inSpace.X, inSpace.Y, inSpace.Z)) - radius - (type.Radius ?? 0)
                    <= attributes[DogmaAttributeId.MaxRange]
            )
            {
                Add(target.Id);
            }
        }
        var condition =
            action.Set<SavedModuleCondition>().Find(moduleId) ?? action.Add(new SavedModuleCondition { ModuleId = moduleId }).Entity;
        var before = PilotInventoryStore.Read(catalog, item);
        condition.Damage = attributes[DogmaAttributeId.StructureHitPoints];
        ModuleConditionPersistence.BurnOut(action, catalog, moduleId, now);
        var cooldown = action.Set<ModuleCooldown>().Find(moduleId) ?? action.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = checked(
            end + (long)Math.Ceiling(attributes[DogmaAttributeId.ModuleReactivationDelay] * TimeSpan.TicksPerMillisecond)
        );
        var state = CrimewatchPersistence.State(action, pilotId);
        state.SystemId = system;
        var timers = state.Timers();
        state.Apply(timers with { Weapons = CrimewatchRules.Extend(timers.Weapons, now, CrimewatchRules.WeaponsDuration) });
        action.Commit();
        return new(pilotId, shipId, moduleId, now, end, new(PilotInventoryStore.Read(catalog, item), before), condition.Damage);

        void Add(long forShip) =>
            action.Add(
                new WorldCommandBurst
                {
                    Id = action.Next("WorldCommandBurst"),
                    ShipId = forShip,
                    SystemId = system,
                    ExpiresAt = end,
                    BoostsJson = buffs,
                    IndustrialProtection = true,
                }
            );
    }

    public bool IndustrialProtectionActive(long shipId)
    {
        long now = Now;
        return LiveWorld.Find(database.Path) is { } live
            && live.Economy.MayHaveIndustrialProtection(now)
            && live.Economy.Inspect(world => ProtectedIndustry(world, shipId, now));
    }

    internal static bool ProtectedIndustry(WorldAction world, long ship, long now) =>
        world is EconomyTransaction action
        && action.MayHaveIndustrialProtection(now)
        && world.Filter<WorldCommandBurst>(row => row.ShipId == ship && row.IndustrialProtection && row.ExpiresAt > now).Any();

    internal static void RequireOffense(WorldAction world, long ship, long now)
    {
        if (ProtectedIndustry(world, ship, now))
        {
            throw new InvalidDataException("P.A.N.I.C. prevents use of offensive modules and drones.");
        }
    }
}
