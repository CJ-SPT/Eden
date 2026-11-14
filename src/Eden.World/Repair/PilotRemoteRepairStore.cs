using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotRemoteRepairStore(WorldDatabase database, IRemoteRepairEvents events, TimeProvider clock) : IPilotRemoteRepairing
{
    public bool IsOnline(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.RemoteRepairModuleOnline(pilotId, shipId, moduleId, ReadOnline)
        ?? ReadOnline(pilotId, shipId, moduleId);

    private bool ReadOnline(long pilotId, long shipId, long moduleId)
    {
        return WorldActionScope.Inspect(
            database,
            world =>
                world.Set<OwnedItem>().Find(moduleId) is { Singleton: true, Online: true } item
                && item.OwnerId == pilotId
                && item.LocationId == shipId
                && InventoryDefinitions.IsHighSlot(item.FlagId)
        );
    }

    public RemoteRepairActivation Start(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool continuation = false
    ) => StartCycle(pilotId, moduleId, effect, targetId, locks, now, continuation, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal RemoteRepairActivation StartOverloaded(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool continuation = false
    ) => StartCycle(pilotId, moduleId, effect, targetId, locks, now, continuation, true);

    private RemoteRepairActivation StartCycle(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool continuation,
        bool overloaded
    )
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [targetId]));
        var world = transaction.World;
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)?.GroupId ?? 0;
        if (
            pilot.ActiveShip.Position is null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !item.Singleton
            || !item.Online
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !RemoteRepairCatalog.Supported(catalog, item.TypeId, group)
            || RemoteRepairRules.Effect(group) != effect
            || !InventoryDefinitions.CanFitShip(
                catalog.FittingAttributes(item.TypeId),
                pilot.ActiveShip.TypeId,
                catalog.FittingType(pilot.ActiveShip.TypeId)!.GroupId
            )
        )
        {
            throw new InvalidDataException("Use an online fitted remote repairer on your active ship in space.");
        }
        if (targetId == pilot.ActiveShipId || !locks.Contains(targetId))
        {
            throw new InvalidDataException("Lock another pilot's nearby ship before applying remote repairs.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        var calculation = overloaded
            ? FittingCalculation.Read(world, catalog, pilot, overloadedModules: new HashSet<long> { moduleId })
            : null;
        var attributes = PilotFittingStore.ReadModuleAttributes(
            world,
            catalog,
            pilotId,
            PilotInventoryStore.Read(catalog, item),
            calculation
        );
        var module = RemoteRepairRules.Define(item.Id, item.TypeId, effect, attributes);
        var ship = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        _ = Health(world, pilot, ship, now);
        var target = Target(world, catalog, pilot, targetId, now);
        double distance = Distance(catalog, pilot, target.Pilot);
        if (
            distance > Math.Min(TackleDefinitions.MaximumDistance, ship.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
            || distance > module.Optimal && module.Falloff == 0
        )
        {
            throw new InvalidDataException("The remote repair target is outside range.");
        }
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        var row = world.Set<WorldRemoteRepair>().Find(moduleId);
        var previous = row is null ? null : JsonSerializer.Deserialize<RemoteRepairCycle>(row.CycleJson);
        if (cooldown?.ReadyAt > now || previous is { Settled: false })
        {
            throw new InvalidDataException("The remote repairer is still cycling.");
        }
        int rampCycles = 0;
        if (effect == DogmaEffect.MutadaptiveRemoteArmorRepair)
        {
            if (continuation)
            {
                if (
                    previous is not { Settled: true }
                    || row!.PilotId != pilotId
                    || row.SourceShipId != pilot.ActiveShipId
                    || row.TargetId != targetId
                    || row.SystemId != CrimewatchPersistence.System(catalog, pilot.ActiveShip)
                    || previous.Module.TypeId != item.TypeId
                    || previous.Module.Effect != effect
                )
                {
                    throw new InvalidDataException("The mutadaptive repair chain has ended.");
                }
                rampCycles = Math.Min(previous.RampCycles, int.MaxValue - 1) + 1;
            }
            module = RemoteRepairRules.Ramp(module, rampCycles);
        }
        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        AssistancePersistence.Apply(world, catalog, pilot, target.Pilot, now, end);
        var fuel = RemoteRepairRules.AncillaryKind(group) is { } ancillary
            ? AncillaryFuelPersistence.Consume(
                world,
                catalog,
                pilotId,
                item,
                AncillaryRepairRules.Define(ancillary, attributes),
                DogmaAttributeId.ChargeGroup1
            )
            : null;
        if (fuel is not null)
        {
            module = module with { Amount = fuel.Payment.Amount, CapacitorNeed = fuel.Payment.CapacitorNeed };
        }
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        RemoteRepairCycle cycle = new(module, pilot.ActiveShipId, targetId, now, end, rampCycles);
        row ??= world.Set<WorldRemoteRepair>().Add(new() { ModuleId = moduleId }).Entity;
        row.PilotId = pilotId;
        row.SourceShipId = pilot.ActiveShipId;
        row.TargetId = targetId;
        row.SystemId = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        row.CycleJson = JsonSerializer.Serialize(cycle);
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        RemoteRepairApplied? applied = module.Kind == RemoteRepairKind.Shield ? Apply(world, target, module, distance, now) : null;
        var heat = calculation is null ? null : ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, end);
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        if (applied is not null)
        {
            events.Publish(applied);
        }
        return new(cycle, capacitor, fuel?.Magazine);
    }

    public void Complete(long pilotId, long moduleId, long now)
    {
        using WorldActionScope transaction = new(database, select: world => RepairParticipants(world, pilotId, moduleId));
        var world = transaction.World;
        if (world.Set<WorldRemoteRepair>().Find(moduleId) is not { } row || row.PilotId != pilotId)
        {
            return;
        }
        var cycle = JsonSerializer.Deserialize<RemoteRepairCycle>(row.CycleJson)!;
        if (cycle.CompletesAt > now || cycle.Settled)
        {
            return;
        }
        RemoteRepairApplied? applied = null;
        if (cycle.Module.Kind is RemoteRepairKind.Armor or RemoteRepairKind.Hull)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            var pilot = Pilot(world, pilotId);
            var item = world.Set<OwnedItem>().Find(moduleId);
            if (
                pilot.ActiveShipId != row.SourceShipId
                || pilot.ActiveShip.Position is null
                || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != row.SystemId
                || item is not { Singleton: true, Online: true }
                || item.TypeId != cycle.Module.TypeId
                || item.OwnerId != pilotId
                || item.LocationId != row.SourceShipId
                || !InventoryDefinitions.IsHighSlot(item.FlagId)
            )
            {
                throw new InvalidDataException("The remote repair source is unavailable.");
            }
            _ = Health(world, pilot, PilotFittingStore.ShipAttributes(world, catalog, pilot), now);
            var target = Target(world, catalog, pilot, row.TargetId, now);
            double distance = Distance(catalog, pilot, target.Pilot);
            if (cycle.Module.Effect == DogmaEffect.MutadaptiveRemoteArmorRepair && distance > cycle.Module.Optimal)
            {
                throw new InvalidDataException("The mutadaptive repair target has left optimal range.");
            }
            AssistancePersistence.Apply(world, catalog, pilot, target.Pilot, now, now);
            applied = Apply(world, target, cycle.Module, distance, now);
        }
        if (cycle.Module.Effect == DogmaEffect.MutadaptiveRemoteArmorRepair)
        {
            // Retain only the completed chain, so a paid repeat can earn its
            // next step. Stop/disconnect/restart delete this connection-owned row.
            row.CycleJson = JsonSerializer.Serialize(cycle with { Settled = true });
        }
        else
        {
            world.Remove(row);
        }
        world.SaveChanges();
        transaction.Commit();
        if (applied is not null)
        {
            events.Publish(applied);
        }
    }

    public void Stop(long pilotId, long moduleId)
    {
        using WorldActionScope transaction = new(database, select: world => RepairParticipants(world, pilotId, moduleId));
        var world = transaction.World;
        if (world.Set<WorldRemoteRepair>().Find(moduleId) is { } row && row.PilotId == pilotId)
        {
            var cycle = JsonSerializer.Deserialize<RemoteRepairCycle>(row.CycleJson)!;
            HeatCycleKey key = new(moduleId, cycle.StartedAt, cycle.CompletesAt);
            ShipHeatPersistence.CancelSource(world, pilotId, row.SourceShipId, key, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            world.Remove(row);
            world.SaveChanges();
        }
        transaction.Commit();
    }

    private sealed record RepairTarget(Pilot Pilot, IReadOnlyDictionary<DogmaAttributeId, double> Attributes, ShipHealth Health);

    private static TransactionParticipants RepairParticipants(WorldAction world, long pilotId, long moduleId) =>
        world.Set<WorldRemoteRepair>().Find(moduleId) is { } row && row.PilotId == pilotId
            ? new(pilots: [pilotId], ships: [row.SourceShipId, row.TargetId])
            : TransactionParticipants.None;

    private static Pilot Pilot(WorldAction world, long pilotId) => world.Filter<Pilot>(p => p.Id == pilotId).Single();

    private static ShipHealth Health(WorldAction world, Pilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> values, long now)
    {
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(values), now);
        if (health.Remaining.Destroyed || world.Set<ShipLoss>().Find(pilot.ActiveShipId) is not null)
        {
            throw new InvalidDataException("A destroyed ship cannot send or receive remote repairs.");
        }
        return health;
    }

    private static RepairTarget Target(WorldAction world, CatalogDbContext catalog, Pilot source, long targetId, long now)
    {
        var pilot = world.Filter<Pilot>(p => p.ActiveShipId == targetId).SingleOrDefault();
        if (
            pilot?.ActiveShip.Position is null
            || source.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != CrimewatchPersistence.System(catalog, source.ActiveShip)
            || world.Filter<SavedLogoff>(l => l.ShipId == targetId && l.ExpiresAt <= now).Any()
        )
        {
            throw new InvalidDataException("The remote repair target must be another pilot's live ship in the same system.");
        }
        var values = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        if (values.GetValueOrDefault(DogmaAttributeId.DisallowAssistance) != 0)
        {
            throw new InvalidDataException("That ship cannot receive remote assistance.");
        }
        return new(pilot, values, Health(world, pilot, values, now));
    }

    private static double Distance(CatalogDbContext catalog, Pilot source, Pilot target)
    {
        var a = source.ActiveShip.Position!;
        var b = target.ActiveShip.Position!;
        return Math.Max(
            0,
            new SpacePosition(a.X, a.Y, a.Z).DistanceTo(new(b.X, b.Y, b.Z))
                - (catalog.FittingType(source.ActiveShip.TypeId)!.Radius ?? 0)
                - (catalog.FittingType(target.ActiveShip.TypeId)!.Radius ?? 0)
        );
    }

    private static RemoteRepairApplied Apply(WorldAction world, RepairTarget target, RemoteRepairModule module, double distance, long now)
    {
        double amount = RemoteRepairRules.Amount(
            module,
            distance,
            target.Attributes.GetValueOrDefault(DogmaAttributeId.RemoteRepairImpedance, 1)
        );
        var health = RemoteRepairRules.Apply(target.Health, module.Kind, amount);
        var saved = world.Set<ShipHealthState>().Find(health.ShipId) ?? world.Add(new ShipHealthState { ShipId = health.ShipId }).Entity;
        saved.ShieldCharge = health.Remaining.Shield;
        if (module.Kind == RemoteRepairKind.Armor)
        {
            saved.ArmorDamage = health.Defense.Armor - health.Remaining.Armor;
        }
        else if (module.Kind == RemoteRepairKind.Hull)
        {
            saved.StructureDamage = health.Defense.Structure - health.Remaining.Structure;
        }
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        return new(target.Pilot.Id, health);
    }
}
