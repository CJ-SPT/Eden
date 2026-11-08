using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal sealed record LoadedShipHeat(long PilotId, long ShipId, ShipHeatSnapshot State, bool HasSavedState, bool Changed);

internal static class ShipHeatPersistence
{
    private static readonly JsonSerializerOptions s_savedJson = new() { IgnoreReadOnlyProperties = true };

    internal static string Encode(ShipHeatSnapshot state) => JsonSerializer.Serialize(state, s_savedJson);

    internal static ShipHeatSnapshot Decode(string data)
    {
        try
        {
            return HeatCycleRules.Restore(
                JsonSerializer.Deserialize<ShipHeatSnapshot>(data) ?? throw new InvalidDataException("Missing saved ship heat state.")
            );
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or NullReferenceException)
        {
            throw new InvalidDataException("Invalid saved ship heat state.", error);
        }
    }

    internal static void ValidateSaved(WorldDbContext world)
    {
        foreach (string data in world.Set<SavedShipHeat>().Select(h => h.StateJson))
        {
            _ = Decode(data);
        }
    }

    internal static IReadOnlyList<HeatRackProfile> Profiles(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        var capacity = HullFittingCapacity.Read(attributes);
        return new[] { ModuleRack.Low, ModuleRack.Medium, ModuleRack.High }
            .Where(r => capacity.Slots(r) > 0)
            .Select(r => HeatRules.Define(r, attributes))
            .ToArray();
    }

    internal static LoadedShipHeat? Load(
        WorldDbContext world,
        CatalogDbContext catalog,
        long shipId,
        long now,
        bool liveChange = false,
        ShipHeatSnapshot? current = null,
        long? expectedPilot = null
    )
    {
        var ship = world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == shipId);
        if (expectedPilot is not null && ship?.OwnerId != expectedPilot)
        {
            return null;
        }
        var pilot = ship is null ? null : world.Pilots.Include(p => p.ActiveShip).SingleOrDefault(p => p.Id == ship.OwnerId);
        if (ship is null || pilot is null)
        {
            return null;
        }
        var calculation = FittingCalculation.Read(world, catalog, pilot, ship);
        var profiles = Profiles(calculation.Graph.Attributes(ship.Id));
        var capacity = HullFittingCapacity.Read(calculation.Graph.Attributes(ship.Id));
        var saved = world.Set<SavedShipHeat>().Find(shipId);
        var original = saved is null ? null : Decode(saved.StateJson);
        var state = current ?? original ?? HeatCycleRules.Create(profiles, now);
        var ids = state.Cycles.Select(c => c.ModuleId).ToArray();
        var modules = calculation.Modules.Where(m => ids.Contains(m.Item.Id)).Select(m => m.Item).ToArray();
        // Physical membership triggers remove projected assistance before the
        // source controller runs. Keep its heat source on that same boundary.
        var assistance =
            liveChange
            && calculation.Modules.Any(m =>
                ids.Contains(m.Item.Id) && m.GroupId is RemoteAssistanceRules.TrackingGroup or RemoteAssistanceRules.SensorGroup
            )
                ? world
                    .Set<WorldRemoteAssistance>()
                    .Where(r => r.SourceShipId == shipId && ids.Contains(r.ModuleId))
                    .AsNoTracking()
                    .AsEnumerable()
                    .Select(r =>
                    {
                        var contact = r.Contact();
                        return new HeatCycleKey(r.ModuleId, contact.StartedAt, contact.ExpiresAt);
                    })
                    .ToHashSet()
                : null;
        var localCycles = liveChange
            ? calculation
                .Modules.Where(m =>
                    ids.Contains(m.Item.Id)
                    && (HardenerDefinitions.Effect(m.GroupId) is not null || SupportModuleDefinitions.Effect(m.GroupId) is not null)
                )
                .Select(m => m.Item.Id)
                .ToHashSet()
            : [];
        var localDeadlines =
            localCycles.Count == 0
                ? null
                : world
                    .Set<ActiveFittedModule>()
                    .Where(a => a.ShipId == shipId && localCycles.Contains(a.ModuleId))
                    .ToDictionary(a => a.ModuleId, a => a.ExpiresAt);
        var available = modules
            .Where(i =>
                ship.Position is not null
                && i.Online
                && i.Singleton
                && i.Quantity == 1
                && state.Cycles.Any(c => c.ModuleId == i.Id && Rack(i.FlagId) == c.Rack && InSlot(i.FlagId, c.Rack, capacity))
                && (
                    !localCycles.Contains(i.Id)
                    // The timed owner can remove a naturally expired marker before this read.
                    // Settle its last funded interval through the original deadline.
                    || state.Cycles.Any(c =>
                        c.ModuleId == i.Id && (c.CompletesAt <= now || c.CompletesAt == localDeadlines!.GetValueOrDefault(i.Id))
                    )
                )
                && OverloadCatalog.Definition(catalog, i.TypeId) is not null
                && !ModuleConditionPersistence.Read(world, catalog, i).BurnedOut
                && (
                    assistance is null
                    || catalog.FittingType(i.TypeId)!.GroupId
                        is not (RemoteAssistanceRules.TrackingGroup or RemoteAssistanceRules.SensorGroup)
                    || state.Cycles.Any(c => c.ModuleId == i.Id && assistance.Contains(c.Key))
                )
            )
            .Select(i => i.Id)
            .ToHashSet();
        state = HeatCycleRules.RetainSources(state, available);
        // A live mutation has already advanced/projected the old profile at
        // its boundary. A cold reopen first settles the saved profile's debt.
        if (!liveChange)
        {
            state = HeatCycleRules.Advance(state, now).State;
        }
        state = state with
        {
            Cycles = Array.AsReadOnly(
                state
                    .Cycles.Select(c => c with { Absorption = calculation.Graph.Read(c.ModuleId, DogmaAttributeId.HeatAbsorption) })
                    .ToArray()
            ),
        };
        state = HeatCycleRules.Configure(state, profiles, state.UpdatedAt).State;
        state = HeatCycleRules.Configure(state, profiles, now).State;
        return new(pilot.Id, shipId, state, saved is not null, original is not null && !HeatCycleRules.SameState(original, state));
    }

    internal static ModuleRack? Rack(int flag) =>
        InventoryDefinitions.IsLowSlot(flag) ? ModuleRack.Low
        : InventoryDefinitions.IsMediumSlot(flag) ? ModuleRack.Medium
        : InventoryDefinitions.IsHighSlot(flag) ? ModuleRack.High
        : null;

    private static bool InSlot(int flag, ModuleRack rack, HullFittingCapacity capacity)
    {
        int first = rack switch
        {
            ModuleRack.Low => (int)InventoryFlag.LowSlot0,
            ModuleRack.Medium => (int)InventoryFlag.MediumSlot0,
            ModuleRack.High => (int)InventoryFlag.HighSlot0,
            _ => throw new InvalidDataException("Invalid heat rack."),
        };
        return flag >= first && flag < first + capacity.Slots(rack);
    }

    internal static ShipHeatSnapshot BeginPaidCycle(
        WorldDbContext world,
        CatalogDbContext catalog,
        FittingCalculation calculation,
        long moduleId,
        long startedAt,
        long completesAt
    )
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Paid heat contributions require the owning transaction.");
        }
        if (world.Set<SavedNaniteRepair>().Any(r => r.ShipId == calculation.ShipId && r.CompletesAt > startedAt))
        {
            throw new InvalidDataException("Stop nanite repairs before paying for an overload cycle.");
        }
        var item = calculation.Modules.SingleOrDefault(m => m.Item.Id == moduleId)?.Item;
        var cooldown = world.Set<ModuleCooldown>().Find(moduleId);
        if (
            !calculation.OverloadedModules.Contains(moduleId)
            || item is null
            || !item.Online
            || item.OwnerId != calculation.PilotId
            || item.LocationId != calculation.ShipId
            || cooldown?.Kind != ModuleCooldownKind.Cycle
            || cooldown.ReadyAt != completesAt
            || !world.Pilots.Any(p => p.Id == calculation.PilotId && p.ActiveShipId == calculation.ShipId && p.ActiveShip.Position != null)
        )
        {
            throw new InvalidDataException("Register only the selected module's already paid overload cycle.");
        }
        var rack = Rack(item.FlagId) ?? throw new InvalidDataException("This fitted item has no heat rack.");
        var source = new PaidHeatCycle(
            moduleId,
            rack,
            startedAt,
            completesAt,
            calculation.Graph.Read(moduleId, DogmaAttributeId.HeatAbsorption)
        );
        var saved = world.Set<SavedShipHeat>().Find(calculation.ShipId);
        var profiles = Profiles(calculation.Graph.Attributes(calculation.ShipId));
        var state = saved is null ? HeatCycleRules.Create(profiles, startedAt) : Decode(saved.StateJson);
        state = HeatCycleRules.Configure(state, profiles, startedAt).State;
        state = HeatCycleRules.Start(state, source).State;
        saved ??= world.Add(new SavedShipHeat { ShipId = calculation.ShipId }).Entity;
        saved.StateJson = Encode(state);
        return state;
    }

    internal static void CancelPaidCycle(WorldDbContext world, long pilot, long ship, HeatCycleKey expected, long now)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Paid heat contributions require the owning transaction.");
        }
        if (!world.Ships.Any(s => s.Id == ship && s.OwnerId == pilot))
        {
            throw new InvalidDataException("Cancel heat only on an owned ship.");
        }
        if (world.Set<SavedShipHeat>().Find(ship) is { } saved)
        {
            saved.StateJson = Encode(HeatCycleRules.Cancel(Decode(saved.StateJson), expected, now).State);
        }
    }

    // Effect removal owns the current module source. A delayed completion
    // instead supplies its exact paid key so it cannot remove a replacement.
    internal static void CancelSource(WorldDbContext world, long pilot, long ship, long module, long now) =>
        CancelSource(world, pilot, ship, module, null, now);

    internal static void CancelSource(WorldDbContext world, long pilot, long ship, HeatCycleKey expected, long now) =>
        CancelSource(world, pilot, ship, expected.ModuleId, expected, now);

    private static void CancelSource(WorldDbContext world, long pilot, long ship, long module, HeatCycleKey? expected, long now)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Paid heat contributions require the owning transaction.");
        }
        if (world.Set<SavedShipHeat>().Find(ship) is not { } saved)
        {
            return;
        }
        var state = Decode(saved.StateJson);
        if (
            state.Cycles.SingleOrDefault(c => c.ModuleId == module) is { } source
            && (expected is null || source.Key == expected)
            && world.Ships.Any(s => s.Id == ship && s.OwnerId == pilot)
        )
        {
            saved.StateJson = Encode(HeatCycleRules.Cancel(state, source.Key, now).State);
        }
    }
}
