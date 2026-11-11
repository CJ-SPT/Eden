using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Repair;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

[Injectable]
internal sealed partial class PilotRemoteAssistanceStore(WorldDatabase database, TimeProvider clock) : IPilotRemoteAssistance
{
    public RemoteAssistanceActivation Activate(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, targetId, locks, now, false);

    // Native overload activation remains gated on the damage/burnout owner.
    internal RemoteAssistanceActivation ActivateOverloaded(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now
    ) => ActivateCycle(pilotId, moduleId, effect, targetId, locks, now, true);

    private RemoteAssistanceActivation ActivateCycle(
        long pilotId,
        long moduleId,
        DogmaEffect effect,
        long targetId,
        IReadOnlySet<long> locks,
        long now,
        bool overloaded
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        overloaded |= PilotOverloadStore.Selected(world, database.Path, pilotId, moduleId);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        int group = item is null ? 0 : catalog.FittingType(item.TypeId)?.GroupId ?? 0;
        if (
            pilot.ActiveShip.Position is null
            || item is not { Singleton: true, Online: true }
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || !InventoryDefinitions.IsMediumSlot(item.FlagId)
            || !RemoteAssistanceCatalog.Supported(catalog, item.TypeId, group)
            || RemoteAssistanceRules.Effect(group) != effect
        )
        {
            throw new InvalidDataException("Use an online fitted remote tracking computer or sensor booster in space.");
        }
        if (targetId == pilot.ActiveShipId || !locks.Contains(targetId))
        {
            throw new InvalidDataException("Lock another pilot's nearby ship before applying remote assistance.");
        }
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, item.Id, now);
        if (PilotAmmunitionStore.Contents(world, moduleId) is { } script)
        {
            int scriptGroup = catalog.FittingType(script.TypeId)!.GroupId;
            if (
                script.OwnerId != pilotId
                || script.Quantity != 1
                || !ScriptCatalog.Supported(catalog, script.TypeId, scriptGroup)
                || !SupportModuleDefinitions.CompatibleScript(
                    group,
                    scriptGroup,
                    catalog.FittingAttributes(item.TypeId),
                    catalog.FittingAttributes(script.TypeId)
                )
            )
            {
                throw new InvalidDataException("Load a compatible remote assistance script.");
            }
        }
        var calculation = FittingCalculation.Read(
            world,
            catalog,
            pilot,
            overloadedModules: overloaded ? new HashSet<long> { moduleId } : null
        );
        var module = RemoteAssistanceRules.Define(moduleId, item.TypeId, effect, calculation.Graph.Attributes(moduleId));
        var source = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        CheckAlive(pilot, source);
        int system = CrimewatchPersistence.System(catalog, pilot.ActiveShip);
        var target = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.ActiveShipId == targetId);
        if (
            target?.ActiveShip.Position is null
            || CrimewatchPersistence.System(catalog, target.ActiveShip) != system
            || world.Set<SavedLogoff>().Any(l => l.ShipId == targetId && l.ExpiresAt <= now)
        )
        {
            throw new InvalidDataException("The remote assistance target must be another pilot's live ship in the same system.");
        }
        var recipient = PilotFittingStore.ShipAttributes(world, catalog, target);
        CheckAlive(target, recipient);
        if (
            recipient.GetValueOrDefault(DogmaAttributeId.DisallowAssistance) != 0
            || recipient.GetValueOrDefault(DogmaAttributeId.DisallowOffensiveModifiers) != 0
        )
        {
            // Pinned assistance remains disallowed on EW-immune ships as well
            // as hulls with the general assistance restriction.
            throw new InvalidDataException("That ship cannot receive remote electronic assistance.");
        }
        var a = pilot.ActiveShip.Position;
        var b = target.ActiveShip.Position;
        double distance = Math.Max(
            0,
            new SpacePosition(a.X, a.Y, a.Z).DistanceTo(new(b.X, b.Y, b.Z))
                - (catalog.FittingType(pilot.ActiveShip.TypeId)!.Radius ?? 0)
                - (catalog.FittingType(target.ActiveShip.TypeId)!.Radius ?? 0)
        );
        if (
            distance > Math.Min(TackleDefinitions.MaximumDistance, source.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
            || distance > module.Optimal && module.Falloff == 0
        )
        {
            throw new InvalidDataException("The remote assistance target is outside range.");
        }
        _ = RemoteAssistanceRules.Strength(module, distance, recipient.GetValueOrDefault(DogmaAttributeId.RemoteAssistanceImpedance, 1));
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (cooldown?.ReadyAt > now)
        {
            throw new InvalidDataException("The remote assistance module is still cycling or loading a script.");
        }
        long end = checked(now + (long)Math.Ceiling(module.Cycle.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        AssistancePersistence.Apply(world, catalog, pilot, target, now, end);
        double capacitor = ShipResources.SpendCapacitor(world, pilot.ActiveShipId, source, module.Cycle.CapacitorNeed, now);
        RemoteAssistanceContact contact = new(pilotId, pilot.ActiveShipId, targetId, module, distance, now, end);
        var row = world.Set<WorldRemoteAssistance>().Find(moduleId) ?? world.Add(new WorldRemoteAssistance { ModuleId = moduleId }).Entity;
        row.PilotId = pilotId;
        row.SourceShipId = pilot.ActiveShipId;
        row.TargetId = targetId;
        row.SystemId = system;
        row.ContactJson = JsonSerializer.Serialize(contact);
        if (world.Set<ActiveFittedModule>().Find(moduleId) is null)
        {
            world.Add(new ActiveFittedModule { ModuleId = moduleId, ShipId = pilot.ActiveShipId });
        }
        cooldown ??= world.Set<ModuleCooldown>().Add(new() { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = end;
        var heat = overloaded ? ShipHeatPersistence.BeginPaidCycle(world, catalog, calculation, moduleId, now, end) : null;
        world.SaveChanges();
        transaction.Commit();
        if (heat is not null)
        {
            WorldConnections.PublishHeat(world, pilotId, pilot.ActiveShipId, heat);
        }
        return new(contact, capacitor);

        void CheckAlive(Pilot owner, IReadOnlyDictionary<DogmaAttributeId, double> values)
        {
            if (
                world.Set<ShipLoss>().Any(l => l.ShipId == owner.ActiveShipId)
                || PilotHealthStore.Read(world, owner.ActiveShipId, ShipHealth.DefenseFrom(values), now).Remaining.Destroyed
            )
            {
                throw new InvalidDataException("A destroyed ship cannot send or receive remote assistance.");
            }
        }
    }

    internal static IReadOnlyList<RemoteAssistanceContact> Active(WorldDbContext world, long shipId) =>
        Array.AsReadOnly(
            world
                .Set<WorldRemoteAssistance>()
                .Where(e => e.TargetId == shipId)
                .OrderBy(e => e.ModuleId)
                .AsNoTracking()
                .AsEnumerable()
                .Select(e => e.Contact())
                .ToArray()
        );

    public IReadOnlyList<RemoteAssistanceContact> Incoming(long targetId) =>
        LiveReadModels.Find(database.Path)?.RemoteAssistanceIncoming(targetId, ReadIncoming) ?? ReadIncoming(targetId);

    private IReadOnlyList<RemoteAssistanceContact> ReadIncoming(long targetId)
    {
        using WorldDbContext world = new(database.Path);
        return Active(world, targetId);
    }

    public bool IsActive(long pilotId, long shipId, long moduleId) =>
        LiveReadModels.Find(database.Path)?.RemoteAssistanceActive(pilotId, shipId, moduleId, ReadActive)
        ?? ReadActive(pilotId, shipId, moduleId);

    private bool ReadActive(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        return world.Set<WorldRemoteAssistance>().Any(e => e.ModuleId == moduleId && e.PilotId == pilotId && e.SourceShipId == shipId)
            && world
                .Set<OwnedItem>()
                .Any(i =>
                    i.Id == moduleId
                    && i.OwnerId == pilotId
                    && i.LocationId == shipId
                    && i.Singleton
                    && i.Online
                    && i.FlagId >= (int)InventoryFlag.MediumSlot0
                    && i.FlagId <= (int)InventoryFlag.MediumSlot7
                );
    }

    public void Stop(long pilotId, long shipId, long moduleId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (
            world
                .Set<WorldRemoteAssistance>()
                .SingleOrDefault(e => e.ModuleId == moduleId && e.PilotId == pilotId && e.SourceShipId == shipId) is
            { } row
        )
        {
            Remove(world, row, Now);
        }
        else if (world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            world.Set<ActiveFittedModule>().Where(e => e.ModuleId == moduleId && e.ShipId == shipId).ExecuteDelete();
        }
        world.SaveChanges();
        transaction.Commit();
    }

    public void Cancel(long pilotId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (world.Ships.Any(s => s.Id == shipId && s.OwnerId == pilotId))
        {
            var rows = world
                .Set<WorldRemoteAssistance>()
                .Where(e => e.SourceShipId == shipId && e.PilotId == pilotId || e.TargetId == shipId)
                .ToArray();
            long now = Now;
            foreach (var row in rows)
            {
                Remove(world, row, now);
            }
            world.SaveChanges();
        }
        transaction.Commit();
    }

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static void Remove(WorldDbContext world, WorldRemoteAssistance row, long now)
    {
        var contact = row.Contact();
        ShipHeatPersistence.CancelSource(
            world,
            row.PilotId,
            row.SourceShipId,
            new HeatCycleKey(row.ModuleId, contact.StartedAt, contact.ExpiresAt),
            now
        );
        // The existing effect-deletion trigger removes its active marker.
        // Tracking both deletes would race that trigger during SaveChanges.
        world.Remove(row);
    }
}
