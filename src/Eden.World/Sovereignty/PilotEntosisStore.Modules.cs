using Eden.Data.World.Alliances;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Thermal;

namespace Eden.World.Sovereignty;

internal sealed partial class PilotEntosisStore
{
    public EntosisActivation Activate(long pilotId, long shipId, long moduleId, long hubId, long repeats, long now)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Start(action, catalog, pilotId, shipId, moduleId, hubId, repeats, now, warm: false);
        action.AfterCommit(() => _scoringLeases.Add(moduleId));
        action.Commit();
        return result;
    }

    public EntosisActivation? Renew(long pilotId, long moduleId, long now)
    {
        using var action = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var cycle = action.Set<SavedEntosisCycle>().Find(moduleId);
        if (cycle is null || cycle.PilotId != pilotId)
        {
            return null;
        }
        if (now < cycle.EndsAt)
        {
            throw new InvalidDataException("The paid Entosis cycle has not ended.");
        }
        Advance(action, catalog, cycle.SystemId, now);
        if (action.Set<SavedEntosisCycle>().Find(moduleId) is null)
        {
            action.Commit();
            return null;
        }
        bool repeat =
            !cycle.Stopping
            && cycle.Repeats > 0
            && Target(action, cycle.SystemId, cycle.HubId) is { } target
            && Admits(action, target, now);
        action.Remove(cycle);
        action.AfterCommit(() => _scoringLeases.Remove(moduleId));
        EntosisActivation? result = null;
        if (repeat)
        {
            result = Start(action, catalog, pilotId, cycle.ShipId, moduleId, cycle.HubId, cycle.Repeats - 1, now, cycle.Contributing);
            action.AfterCommit(() => _scoringLeases.Add(moduleId));
        }
        action.Commit();
        return result;
    }

    private EntosisActivation Start(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long moduleId,
        long hubId,
        long repeats,
        long now,
        bool warm
    )
    {
        if (repeats is < 0 or > int.MaxValue || now < 0)
        {
            throw new InvalidDataException("Choose a valid Entosis repeat count.");
        }
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot?.ActiveShipId != shipId
            || pilot.ActiveShip.Position is not { } position
            || item is not { Online: true, Singleton: true }
            || item.OwnerId != pilotId
            || item.LocationId != shipId
            || !InventoryDefinitions.IsHighSlot(item.FlagId)
            || !EntosisCatalog.Supported(catalog, item.TypeId)
            || world.Set<ShipLoss>().Find(shipId) is not null
        )
        {
            throw new InvalidDataException("Use an online fitted Entosis Link on your active ship in space.");
        }
        world.RequireUncloaked(shipId);
        if (catalog.FittingType(pilot.ActiveShip.TypeId)?.GroupId == EntosisRules.InterceptorGroup)
        {
            throw new InvalidDataException("Interceptors cannot activate an Entosis Link.");
        }
        int system = position.SystemId ?? throw new InvalidDataException("The ship has no current solar system.");
        Advance(world, catalog, system, now);
        var target = Target(world, system, hubId);
        if (target is null || !Admits(world, target, now))
        {
            throw new InvalidDataException("Choose an available Entosis target in your current system's vulnerability window.");
        }
        if (world.Set<SavedSovereigntyConflict>().Find(system) is { Phase: SovereigntyPhase.Secure, Value: 1 } secure)
        {
            // A newly admitted warm-up uses today's window, even after days without observers.
            secure.UpdatedAt = now;
        }
        var member = world.Set<SavedAllianceMember>().Find(pilot.CorporationId);
        if (
            member is not null && world.Set<SavedAlliance>().Find(member.AllianceId) is not { ClosedAt: null }
            || member is null && target.TypeId == EntosisRules.HubTypeId && world.Set<SavedSovereigntyClaim>().Find(system) is null
        )
        {
            throw new InvalidDataException("Only a pilot in an active alliance can claim a sovereignty hub.");
        }
        if (!locks.Contains(shipId, hubId))
        {
            throw new InvalidDataException("Lock the target before activating Entosis.");
        }
        if (
            world.Filter<SavedEntosisCycle>(row => row.ShipId == shipId && row.EndsAt > now).Any()
            || world.Set<ModuleCooldown>().Find(moduleId)?.ReadyAt > now
        )
        {
            throw new InvalidDataException("The Entosis Link is still cycling.");
        }
        PilotFittingStore.CheckHull(catalog, item.TypeId, pilot.ActiveShip.TypeId);
        PilotSkillReader.CheckRequirements(world, catalog, pilotId, item.TypeId);
        NaniteRepairPersistence.RequireAvailable(world, catalog, moduleId, now);
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot, calculation: calculation);
        var profile = EntosisCatalog.Module(
            calculation.Graph.Attributes(moduleId),
            attributes.GetValueOrDefault(EntosisCatalog.DurationMultiplier)
        );
        var cycle = new SavedEntosisCycle
        {
            ModuleId = moduleId,
            TypeId = item.TypeId,
            PilotId = pilotId,
            ShipId = shipId,
            HubId = hubId,
            SystemId = system,
            CorporationId = pilot.CorporationId,
            AllianceId = member?.AllianceId ?? 0,
            StartedAt = now,
            EndsAt = checked(now + (long)Math.Ceiling(profile.Duration * TimeSpan.TicksPerMillisecond)),
            Range = profile.Range,
            SpeedLimit = profile.SpeedLimit,
            Capital = attributes.GetValueOrDefault(EntosisCatalog.CapitalSize) > 0,
            Repeats = repeats,
            Contributing = true,
        };
        cycle.WarmupUntil = warm ? now : cycle.EndsAt;
        if (!Valid(world, catalog, cycle, target))
        {
            throw new InvalidDataException("The target is outside Entosis range.");
        }
        if (world.Set<SavedEntosisCycle>().Find(moduleId) is { } expired)
        {
            world.Remove(expired);
        }
        double capacitor = ShipResources.SpendCapacitor(world, shipId, attributes, profile.Capacitor, now);
        var fuel = world
            .Filter<OwnedItem>(row =>
                row.OwnerId == pilotId
                && row.LocationId == shipId
                && row.FlagId == (int)InventoryFlag.Cargo
                && row.TypeId == profile.FuelType
                && !row.Singleton
                && row.Quantity > 0
            )
            .OrderBy(row => row.Id)
            .ToArray();
        if (fuel.Sum(row => (long)row.Quantity) < profile.FuelQuantity)
        {
            throw new InvalidDataException("Each Entosis cycle requires Strontium Clathrates in your ship's cargo.");
        }
        List<InventoryChange> changes = [];
        int needed = profile.FuelQuantity;
        foreach (var stack in fuel)
        {
            int paid = Math.Min(needed, stack.Quantity);
            if (paid == 0)
            {
                break;
            }
            var before = PilotInventoryStore.Read(catalog, stack);
            stack.Quantity -= paid;
            needed -= paid;
            if (stack.Quantity == 0)
            {
                world.Remove(stack);
            }
            changes.Add(new(before with { Quantity = stack.Quantity }, before));
        }
        world.Add(cycle);
        if (cycle.Capital)
        {
            world.RemoveRange(world.Filter<Eden.Data.World.Repair.WorldRemoteAssistance>(row => row.TargetId == shipId));
        }
        var active = world.Set<ActiveFittedModule>().Find(moduleId) ?? world.Add(new ActiveFittedModule { ModuleId = moduleId }).Entity;
        active.ShipId = shipId;
        active.ExpiresAt = cycle.EndsAt;
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId) ?? world.Add(new ModuleCooldown { ModuleId = moduleId }).Entity;
        cooldown.Kind = ModuleCooldownKind.Cycle;
        cooldown.ReadyAt = cycle.EndsAt;
        var timers = CrimewatchPersistence.State(world, pilotId);
        timers.SystemId = system;
        var state = timers.Timers();
        timers.Apply(state with { Weapons = CrimewatchRules.Extend(state.Weapons, now, CrimewatchRules.WeaponsDuration) });
        return new(Cycle(cycle), capacitor, changes);
    }
}
