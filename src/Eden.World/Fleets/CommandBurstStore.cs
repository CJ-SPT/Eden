using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Fleets;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
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
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fleets;

[Injectable]
internal sealed class CommandBurstStore(WorldDatabase database, ICooperativeFleets fleets) : IPilotCommandBursts
{
    public CommandBurstPulse Pulse(
        long pilotId,
        long shipId,
        long moduleId,
        DogmaEffect effect,
        IReadOnlyList<CommandBurstCandidate> candidates,
        long now
    )
    {
        var members = fleets.MembersInFleet(pilotId, candidates.Select(candidate => candidate.PilotId).ToArray());
        var fleetmates = candidates.Where(candidate => members.Contains(candidate.PilotId)).ToArray();
        using WorldActionScope transaction = new(
            database,
            new(
                pilots: fleetmates.Select(candidate => candidate.PilotId).Append(pilotId),
                ships: fleetmates.Select(candidate => candidate.ShipId).Append(shipId)
            )
        );
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var source = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            source?.ActiveShipId != shipId
            || source.ActiveShip.Position is not { } position
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !CommandBurstCatalog.Module(catalog, item.TypeId, catalog.RequireItem(item.TypeId).GroupId)
            || FittingCatalog.DefaultEffect(catalog, item.TypeId) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted command burst on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        if (world.Set<Account>().Find(source.AccountId)?.CloneGrade != AccountCloneGrade.Omega)
        {
            throw new InvalidDataException("Command bursts require an Omega clone.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        PilotFittingStore.CheckHull(catalog, item.TypeId, source.ActiveShip.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The command burst is still cycling or reloading.");
        }
        var charge = PilotAmmunitionStore.Contents(world, moduleId);
        if (
            charge is not { Quantity: > 0 }
            || charge.OwnerId != pilotId
            || !CommandBurstCatalog.Charge(catalog, charge.TypeId, catalog.RequireItem(charge.TypeId).GroupId)
            || !CommandBurstRules.Compatible(
                CommandBurstRules.Group,
                catalog.RequireItem(charge.TypeId).GroupId,
                catalog.FittingAttributes(item.TypeId)
            )
        )
        {
            throw new InvalidDataException("Load a compatible command burst charge.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, charge.TypeId);
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        if (world is SqlWorldAction)
        {
            Expire(world, system, now);
            world.SaveChanges();
        }
        var calculation = FittingCalculation.Read(world, catalog, source);
        var module = CommandBurstRules.Define(moduleId, item.TypeId, effect, calculation.Graph.Attributes(moduleId));
        var group = calculation.Modules.Where(m => m.GroupId == CommandBurstRules.Group && m.Item.Online).Select(m => m.Item.Id).ToArray();
        if (
            world
                .Set<ActiveFittedModule>()
                .Count(row => row.ShipId == shipId && row.ExpiresAt > now && Enumerable.Contains(group, row.ModuleId))
            >= module.MaximumActive
        )
        {
            throw new InvalidDataException("The maximum number of active command bursts has been reached.");
        }
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, source, calculation: calculation);
        if (PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(attributes), now).Remaining.Destroyed)
        {
            throw new InvalidDataException("A destroyed ship cannot activate command bursts.");
        }
        var values = CommandBurstCatalog.Values(calculation.Graph, moduleId);
        if (values.Length == 0)
        {
            throw new InvalidDataException("The loaded charge has no command burst effects.");
        }
        long readyAt = checked(now + module.CycleTicks);
        long expiresAt = checked(now + module.BoostTicks);
        double capacitor = ShipResources.SpendCapacitor(world, shipId, attributes, module.Capacitor, now);
        var magazine = PilotAmmunitionStore.Read(world, item, charge) with { Quantity = charge.Quantity - 1, ReadyAt = 0 };
        charge.Quantity--;
        if (charge.Quantity == 0)
        {
            world.Remove(charge);
        }
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

        List<long> recipients = [shipId];
        if (fleetmates.Length > 0)
        {
            SpacePosition origin = new(position.X, position.Y, position.Z);
            double radius = catalog.RequireItem(source.ActiveShip.TypeId).Radius ?? 0;
            bool highsec = CrimewatchPersistence.Security(catalog, system) >= CrimewatchRules.HighSecurity;
            foreach (var candidate in fleetmates.Where(c => c.ShipId != shipId).DistinctBy(c => c.ShipId))
            {
                if (
                    world.Pilots.Find(candidate.PilotId) is not { } target
                    || target.ActiveShipId != candidate.ShipId
                    || target.ActiveShip.Position is null
                    || CrimewatchPersistence.System(catalog, target.ActiveShip) != system
                    || catalog.RequireItem(target.ActiveShip.TypeId).GroupId == FleetDefinitions.CapsuleGroup
                    || world.Set<ShipLoss>().Find(candidate.ShipId) is not null
                    || !CommandBurstRules.InRange(
                        origin,
                        radius,
                        candidate.Position,
                        catalog.RequireItem(target.ActiveShip.TypeId).Radius ?? 0,
                        module.Radius
                    )
                    || highsec && HasNeutralEngagement(world, pilotId, target.Id, now)
                )
                {
                    continue;
                }
                recipients.Add(candidate.ShipId);
            }
        }
        string boosts = JsonSerializer.Serialize(values);
        long lastId = 0;
        foreach (long ship in recipients)
        {
            lastId = world.Next("WorldCommandBurst", lastId);
            world.Add(
                new WorldCommandBurst
                {
                    Id = lastId,
                    ShipId = ship,
                    SystemId = system,
                    ExpiresAt = expiresAt,
                    BoostsJson = boosts,
                }
            );
        }
        transaction.Commit();
        return new(module, readyAt, capacitor, magazine, recipients.ToArray());
    }

    private static bool HasNeutralEngagement(WorldAction world, long source, long target, long now) =>
        world
            .Set<SavedEngagement>()
            .Any(e =>
                e.ExpiresAt > now
                && (e.LowerPilotId == target || e.HigherPilotId == target)
                && e.LowerPilotId != source
                && e.HigherPilotId != source
            );

    public IReadOnlyList<CommandBurstBoost> Incoming(long ship, long now) =>
        LiveReadModels.Find(database.Path)?.CommandBursts(ship, now, Load) ?? Load(ship, now).Boosts;

    private (IReadOnlyList<CommandBurstBoost> Boosts, long NextExpiry) Load(long ship, long now) =>
        WorldActionScope.Inspect<(IReadOnlyList<CommandBurstBoost>, long)>(
            database,
            world =>
            {
                var rows = world.Set<WorldCommandBurst>().Where(b => b.ShipId == ship && b.ExpiresAt > now).ToArray();
                if (rows.Length == 0)
                {
                    return (Array.Empty<CommandBurstBoost>(), long.MaxValue);
                }
                using CatalogDbContext catalog = new(database.CatalogPath);
                return (Array.AsReadOnly(Effective(catalog, rows)), rows.Min(b => b.ExpiresAt));
            }
        );

    internal static void Clear(WorldAction world, long ship) =>
        world.RemoveRange(world.Set<WorldCommandBurst>().Where(b => b.ShipId == ship));

    internal static void Expire(WorldAction world, int? system, long now) =>
        world.RemoveRange(world.Set<WorldCommandBurst>().Where(b => b.ExpiresAt <= now && (system == null || b.SystemId == system)));

    private static CommandBurstBoost[] Effective(CatalogDbContext catalog, IEnumerable<WorldCommandBurst> rows) =>
        CommandBurstRules.Strongest(
            rows.SelectMany(row =>
                JsonSerializer
                    .Deserialize<CommandBurstValue[]>(row.BoostsJson)!
                    .Select(value => new CommandBurstBoost(value.CollectionId, value.Value, row.ExpiresAt))
            ),
            id => CommandBurstCatalog.Collection(catalog, id)
        );

    internal static IEnumerable<DogmaModifier> Modifiers(
        WorldAction world,
        CatalogDbContext catalog,
        long ship,
        long pilot,
        IReadOnlyList<FittingLocation> modules
    ) => Modifiers(catalog, ship, pilot, modules, world.Set<WorldCommandBurst>().Where(b => b.ShipId == ship));

    internal static IEnumerable<DogmaModifier> Modifiers(
        WorldDbContext world,
        CatalogDbContext catalog,
        long ship,
        long pilot,
        IReadOnlyList<FittingLocation> modules
    ) => Modifiers(catalog, ship, pilot, modules, world.Set<WorldCommandBurst>().Where(b => b.ShipId == ship));

    private static IEnumerable<DogmaModifier> Modifiers(
        CatalogDbContext catalog,
        long ship,
        long pilot,
        IReadOnlyList<FittingLocation> modules,
        IEnumerable<WorldCommandBurst> rows
    )
    {
        foreach (var boost in Effective(catalog, rows))
        {
            var collection = CommandBurstCatalog.Collection(catalog, boost.CollectionId);
            foreach (var modifier in collection.Modifiers)
            {
                var value = new DogmaValue.Constant(boost.Value);
                if (modifier.Attribute is DogmaAttributeId.BurstProbeStrength or DogmaAttributeId.BurstProbeDeviation)
                {
                    // These client collections express a percentage on a synthetic probe attribute.
                    // Probe launch consumes the corresponding pilot multiplier.
                    yield return new(pilot, modifier.Attribute, DogmaOperation.Add, value);
                    continue;
                }
                IEnumerable<long> targets = modifier.Selector switch
                {
                    ModifierSelector.Item => [ship],
                    ModifierSelector.Group => modules.Where(m => m.GroupId == modifier.SelectionId).Select(m => m.Item.Id),
                    ModifierSelector.RequiredSkill => modules
                        .Where(m => m.RequiredSkills.Contains(modifier.SelectionId))
                        .Select(m => m.Item.Id),
                    _ => modules.Select(m => m.Item.Id),
                };
                foreach (long target in targets)
                {
                    if (modifier.Attribute == DogmaAttributeId.CrystalVolatilityChance)
                    {
                        // The mining cycle consumes this multiplier when it rolls wear on the loaded crystal.
                        yield return new(
                            target,
                            modifier.Attribute,
                            DogmaOperation.PostAssignment,
                            new DogmaValue.Constant(1 + boost.Value / 100)
                        );
                    }
                    else
                    {
                        yield return new(target, modifier.Attribute, collection.Operation, value);
                    }
                }
            }
        }
    }
}
