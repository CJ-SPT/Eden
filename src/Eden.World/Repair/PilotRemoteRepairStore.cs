using System.Text.Json;
using Eden.Data.Characters;
using Eden.Data.Combat;
using Eden.Data.Inventory;
using Eden.Data.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed class PilotRemoteRepairStore(WorldDatabase database, IRemoteRepairEvents events) : IPilotRemoteRepairing
{
    public bool IsOnline(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.RemoteRepairModuleOnline(pilotId, shipId, moduleId, ReadOnline)
        ?? ReadOnline(pilotId, shipId, moduleId);

    private bool ReadOnline(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<OwnedItem>()
            .Any(i =>
                i.Id == moduleId
                && i.OwnerId == pilotId
                && i.LocationId == shipId
                && i.Singleton
                && i.Online
                && i.FlagId >= (int)InventoryFlag.HighSlot0
                && i.FlagId <= (int)InventoryFlag.HighSlot7
            );
    }

    public RemoteRepairActivation Start(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        catalog.Database.OpenConnection();
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
        )
        {
            throw new InvalidDataException("Use an online fitted remote repairer on your active ship in space.");
        }
        if (targetId == pilot.ActiveShipId || !locks.Contains(targetId))
        {
            throw new InvalidDataException("Lock another pilot's nearby ship before applying remote repairs.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var module = RemoteRepairRules.Define(item.Id, item.TypeId, effect, calculation.Graph.Attributes(item.Id));
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
        if (cooldown?.ReadyAt > now || world.Set<WorldRemoteRepair>().Any(r => r.ModuleId == moduleId))
        {
            throw new InvalidDataException("The remote repairer is still cycling.");
        }
        long end = checked(now + (long)Math.Ceiling(module.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        Assist(world, catalog, pilot, target.Pilot, now, end);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, ship, module.CapacitorNeed, now);
        RemoteRepairCycle cycle = new(module, pilot.ActiveShipId, targetId, now, end);
        world.Add(
            new WorldRemoteRepair
            {
                ModuleId = moduleId,
                PilotId = pilotId,
                SourceShipId = pilot.ActiveShipId,
                TargetId = targetId,
                SystemId = CrimewatchPersistence.System(catalog, pilot.ActiveShip),
                CycleJson = JsonSerializer.Serialize(cycle),
            }
        );
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        RemoteRepairApplied? applied = module.Kind == RemoteRepairKind.Shield ? Apply(world, target, module, distance, now) : null;
        world.SaveChanges();
        transaction.Commit();
        if (applied is not null)
        {
            events.Publish(applied);
        }
        return new(cycle, capacitor);
    }

    public void Complete(long pilotId, long moduleId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var row = world.Set<WorldRemoteRepair>().SingleOrDefault(r => r.ModuleId == moduleId && r.PilotId == pilotId);
        if (row is null)
        {
            return;
        }
        var cycle = JsonSerializer.Deserialize<RemoteRepairCycle>(row.CycleJson)!;
        if (cycle.CompletesAt > now)
        {
            return;
        }
        RemoteRepairApplied? applied = null;
        if (cycle.Module.Kind == RemoteRepairKind.Armor)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            catalog.Database.OpenConnection();
            var pilot = Pilot(world, pilotId);
            var item = world.Set<OwnedItem>().Find(moduleId);
            if (
                pilot.ActiveShipId != row.SourceShipId
                || pilot.ActiveShip.Position is null
                || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != row.SystemId
                || item is not { Singleton: true, Online: true }
                || item.OwnerId != pilotId
                || item.LocationId != row.SourceShipId
                || !InventoryDefinitions.IsHighSlot(item.FlagId)
            )
            {
                throw new InvalidDataException("The remote repair source is unavailable.");
            }
            _ = Health(world, pilot, PilotFittingStore.ShipAttributes(world, catalog, pilot), now);
            var target = Target(world, catalog, pilot, row.TargetId, now);
            Assist(world, catalog, pilot, target.Pilot, now, now);
            applied = Apply(world, target, cycle.Module, Distance(catalog, pilot, target.Pilot), now);
        }
        world.Remove(row);
        world.SaveChanges();
        transaction.Commit();
        if (applied is not null)
        {
            events.Publish(applied);
        }
    }

    public void Stop(long pilotId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        world.Set<WorldRemoteRepair>().Where(r => r.PilotId == pilotId && r.ModuleId == moduleId).ExecuteDelete();
    }

    private sealed record RepairTarget(Pilot Pilot, IReadOnlyDictionary<DogmaAttributeId, double> Attributes, ShipHealth Health);

    private static Pilot Pilot(WorldDbContext world, long pilotId) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);

    private static ShipHealth Health(WorldDbContext world, Pilot pilot, IReadOnlyDictionary<DogmaAttributeId, double> values, long now)
    {
        var health = PilotHealthStore.Read(world, pilot.ActiveShipId, ShipHealth.DefenseFrom(values), now);
        if (health.Remaining.Destroyed || world.Set<ShipLoss>().Any(l => l.ShipId == pilot.ActiveShipId))
        {
            throw new InvalidDataException("A destroyed ship cannot send or receive remote repairs.");
        }
        return health;
    }

    private static RepairTarget Target(WorldDbContext world, CatalogDbContext catalog, Pilot source, long targetId, long now)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == targetId);
        if (
            pilot?.ActiveShip.Position is null
            || source.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, pilot.ActiveShip) != CrimewatchPersistence.System(catalog, source.ActiveShip)
            || world.Set<SavedLogoff>().Any(l => l.ShipId == targetId && l.ExpiresAt <= now)
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

    private static RemoteRepairApplied Apply(
        WorldDbContext world,
        RepairTarget target,
        RemoteRepairModule module,
        double distance,
        long now
    )
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
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        return new(target.Pilot.Id, health);
    }

    private static void Assist(WorldDbContext world, CatalogDbContext catalog, Pilot source, Pilot target, long now, long end)
    {
        var timers = world.Set<SavedCrimewatch>().Find(target.Id)?.Timers() ?? CrimewatchTimers.Empty;
        int system = CrimewatchPersistence.System(catalog, source.ActiveShip);
        var flag = AssistanceRules.Decide(
            new(
                CrimewatchPersistence.Security(catalog, system),
                source.SafetyLevel,
                world.Set<Account>().Find(source.AccountId)!.CloneGrade == AccountCloneGrade.Alpha,
                timers.Flag(now),
                (world.Set<SavedSecurityStatus>().Find(target.Id)?.Value ?? 0) <= CrimewatchRules.OutlawSecurity,
                world.Set<SavedEngagement>().Any(e => (e.LowerPilotId == target.Id || e.HigherPilotId == target.Id) && e.ExpiresAt > now),
                timers.Pvp.Running(now)
            )
        );
        var state = CrimewatchPersistence.State(world, source.Id);
        state.SystemId = system;
        state.Apply(AssistanceRules.Apply(state.Timers(), timers, flag, now, end));
        if (world.Set<SavedLogoff>().Find(source.Id) is { } logoff)
        {
            logoff.ExpiresAt = Math.Max(logoff.ExpiresAt, Math.Max(state.PvpUntil, state.NpcUntil));
        }
    }
}
