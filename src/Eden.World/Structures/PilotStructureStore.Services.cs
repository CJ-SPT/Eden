using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore
{
    public StructureResult FitService(long pilotId, long structureId, long itemId, int slot) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                if (!StructureRules.Operational(structure.State) || !structure.HasCore)
                {
                    throw new InvalidDataException("Core and online the structure before fitting services.");
                }
                var item = AccessibleItem(world, pilot, structure, itemId);
                var profile = StructureCatalog.Require(catalog, structure.TypeId);
                var module = StructureCatalog.Service(catalog, item.TypeId);
                StructureRules.CheckServiceFit(profile, module, slot);
                var installed = world.Set<SavedStructureService>().Where(m => m.StructureId == structureId).ToArray();
                if (
                    installed.Any(m => m.Slot == slot)
                    || module.MaximumFitted > 0
                        && installed.Count(m => world.Set<OwnedItem>().Find(m.ItemId)!.TypeId == item.TypeId) >= module.MaximumFitted
                )
                {
                    throw new InvalidDataException("The service slot or module limit is already occupied.");
                }
                if (item.Quantity > 1)
                {
                    item.Quantity--;
                    item = new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilotId,
                        TypeId = item.TypeId,
                        Quantity = 1,
                    };
                    world.Add(item);
                }
                item.Singleton = true;
                item.OwnerId = structure.CorporationId;
                item.LocationId = structureId;
                item.FlagId = slot;
                world.Add(
                    new SavedStructureService
                    {
                        ItemId = item.Id,
                        StructureId = structureId,
                        Slot = slot,
                    }
                );
            }
        );

    public StructureResult SetServiceOnline(long pilotId, long structureId, long moduleId, bool online) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, _, now) =>
            {
                var module = world.Set<SavedStructureService>().Find(moduleId);
                if (module?.StructureId != structureId)
                {
                    throw new InvalidDataException("This service is not fitted to this structure.");
                }
                var item = world.Set<OwnedItem>().Find(moduleId)!;
                if (module.Online == online)
                {
                    return;
                }
                if (online)
                {
                    if (!StructureRules.Operational(structure.State) || !structure.HasCore)
                    {
                        throw new InvalidDataException("The structure must be operational and have its core installed.");
                    }
                    var profile = StructureCatalog.Require(catalog, structure.TypeId);
                    var service = StructureCatalog.Service(catalog, item.TypeId);
                    if (service.Services.Any(s => !StructureRules.ServiceImplemented(s)))
                    {
                        throw new InvalidDataException("This structure service requires gameplay systems that are not implemented yet.");
                    }
                    var active = world
                        .Set<SavedStructureService>()
                        .Where(m => m.StructureId == structureId && m.Online)
                        .ToArray()
                        .Select(m => StructureCatalog.Service(catalog, world.Set<OwnedItem>().Find(m.ItemId)!.TypeId))
                        .ToArray();
                    if (active.Sum(m => m.Cpu) + service.Cpu > profile.Cpu || active.Sum(m => m.Power) + service.Power > profile.Power)
                    {
                        throw new InvalidDataException("The structure does not have enough available CPU or powergrid.");
                    }
                    double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(structure.SystemId);
                    if (
                        service.Services.Any(s =>
                            s
                                is StructureService.CompositeReactions
                                    or StructureService.BiochemicalReactions
                                    or StructureService.HybridReactions
                        )
                        && security >= StructureRules.HighSecurityBoundary
                    )
                    {
                        throw new InvalidDataException("Reaction services require low or null security.");
                    }
                    if (service.Services.Contains(StructureService.MoonMining) && structure.MoonId is null)
                    {
                        throw new InvalidDataException("Moon mining services require a refinery bound to a moon mining point.");
                    }
                    Fuel(world, catalog, structure, service, AdjustedFuel(catalog, profile, service, service.OnlineFuel));
                    module.NextFuelAt = checked(now + StructureRules.FuelInterval);
                }
                else
                {
                    module.NextFuelAt = null;
                }
                item.Online = online;
                module.Online = online;
                structure.LowPowerAt = online ? null : structure.LowPowerAt ?? now;
                world.SaveChanges();
                StructureIndustry.Synchronize(world, catalog, structure, now);
            }
        );

    public StructureResult RemoveService(long pilotId, long structureId, long moduleId) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                var module = world.Set<SavedStructureService>().Find(moduleId);
                if (
                    module?.StructureId != structureId
                    || module.Online
                    || StructureCatalog.Require(catalog, structure.TypeId).BuiltinModuleTypeId
                        == world.Set<OwnedItem>().Find(moduleId)!.TypeId
                )
                {
                    throw new InvalidDataException("Offline a removable service before unfitting it.");
                }
                if (pilot.ActiveShip.StructureId != structureId || pilot.ActiveShip.Position is not null)
                {
                    throw new InvalidDataException("Dock at the structure before removing its service module.");
                }
                var item = world.Set<OwnedItem>().Find(moduleId)!;
                item.OwnerId = pilotId;
                item.FlagId = (int)InventoryFlag.Hangar;
                item.Singleton = false;
                world.Remove(module);
            }
        );

    public StructureResult DepositFuel(long pilotId, long structureId, long itemId, int quantity) =>
        Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                var item = AccessibleItem(world, pilot, structure, itemId);
                var type = catalog.RequireItem(item.TypeId);
                const int FuelBlockGroup = 1136;
                if (type.GroupId != FuelBlockGroup)
                {
                    throw new InvalidDataException("The structure fuel bay accepts fuel blocks.");
                }
                double used = world
                    .Set<OwnedItem>()
                    .Where(i => i.LocationId == structureId && i.FlagId == StructureRules.FuelBayFlag)
                    .AsEnumerable()
                    .Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);
                if (
                    used + PilotInventoryStore.Volume(catalog, item.TypeId) * quantity
                    > StructureCatalog.Require(catalog, structure.TypeId).FuelCapacity
                )
                {
                    throw new InvalidDataException("The structure fuel bay does not have enough space.");
                }
                int typeId = item.TypeId;
                Consume(world, item, quantity);
                InventoryPersistence.MergeOutput(
                    world,
                    catalog,
                    structure.CorporationId,
                    structureId,
                    StructureRules.FuelBayFlag,
                    typeId,
                    quantity
                );
            }
        );

    private static int AdjustedFuel(CatalogDbContext catalog, StructureProfile structure, StructureServiceProfile module, int amount)
    {
        const int ServiceRoleBonus = 2339;
        return StructureRules.FuelAmount(
            structure.Family,
            module.Services,
            catalog.FittingAttributes(structure.TypeId).GetValueOrDefault((DogmaAttributeId)ServiceRoleBonus),
            amount
        );
    }

    private static bool Fuel(
        WorldDbContext world,
        CatalogDbContext catalog,
        SavedStructure structure,
        StructureServiceProfile service,
        int quantity,
        bool required = true
    )
    {
        var fuel = world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == structure.Id && i.FlagId == StructureRules.FuelBayFlag)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Where(i => catalog.RequireItem(i.TypeId).GroupId == service.FuelGroupId)
            .ToArray();
        if (fuel.Sum(i => (long)i.Quantity) < quantity)
        {
            if (required)
            {
                throw new InvalidDataException("The structure fuel bay lacks the fuel required by this service.");
            }
            return false;
        }
        foreach (var item in fuel)
        {
            int take = Math.Min(item.Quantity, quantity);
            if (take > 0)
            {
                Consume(world, item, take);
                quantity -= take;
            }
        }
        return true;
    }

    internal static void Advance(WorldDbContext world, CatalogDbContext catalog, long now)
    {
        var due = world
            .Set<SavedStructure>()
            .Where(s => s.StateEndsAt != null && s.StateEndsAt <= now || s.UnanchorAt != null && s.UnanchorAt <= now)
            .ToArray();
        foreach (var structure in due)
        {
            var profile = StructureCatalog.Require(catalog, structure.TypeId);
            if (structure.State == StructureState.Anchoring)
            {
                structure.State = StructureState.Onlining;
                structure.StateStartedAt = structure.StateEndsAt!.Value;
                structure.HasCore = profile.CoreTypeId is null;
                structure.StateEndsAt = structure.HasCore
                    ? checked(
                        structure.StateEndsAt!.Value
                        + StructureRules.Duration(StructureRules.RepairDuration, world.Settings.Structures.OnliningSpeed)
                    )
                    : null;
            }
            if (structure.State == StructureState.Onlining && structure.HasCore && structure.StateEndsAt <= now)
            {
                structure.State = StructureState.ShieldVulnerable;
                structure.StateStartedAt = structure.StateEndsAt!.Value;
                structure.StateEndsAt = null;
            }
            if (
                structure.UnanchorAt <= now
                && !world.Ships.Any(s => s.StructureId == structure.Id)
                && !world.Set<OwnedItem>().Any(i => i.LocationId == structure.Id && i.OwnerId != structure.CorporationId)
            )
            {
                // Retain corporation-owned contents in the unanchored container for recovery.
                structure.State = StructureState.Unanchored;
                structure.UnanchorAt = null;
                structure.StateEndsAt = null;
                foreach (var module in world.Set<SavedStructureService>().Where(m => m.StructureId == structure.Id))
                {
                    module.Online = false;
                    module.NextFuelAt = null;
                    world.Set<OwnedItem>().Find(module.ItemId)!.Online = false;
                }
            }
        }
        var services = world
            .Set<SavedStructureService>()
            .Where(m => m.Online && m.NextFuelAt != null && m.NextFuelAt <= now)
            .OrderBy(m => m.StructureId)
            .ThenBy(m => m.Slot)
            .ToArray();
        foreach (var module in services)
        {
            var structure = Require(world, module.StructureId);
            var profile = StructureCatalog.Require(catalog, structure.TypeId);
            var item = world.Set<OwnedItem>().Find(module.ItemId)!;
            var service = StructureCatalog.Service(catalog, item.TypeId);
            int hourly = AdjustedFuel(catalog, profile, service, service.HourlyFuel);
            long cycles = (now - module.NextFuelAt!.Value) / StructureRules.FuelInterval + 1;
            long available = world
                .Set<OwnedItem>()
                .Where(i => i.LocationId == structure.Id && i.FlagId == StructureRules.FuelBayFlag)
                .AsEnumerable()
                .Where(i => catalog.RequireItem(i.TypeId).GroupId == service.FuelGroupId)
                .Sum(i => (long)i.Quantity);
            long paid = hourly == 0 ? cycles : Math.Min(cycles, available / hourly);
            if (paid > 0)
            {
                Fuel(world, catalog, structure, service, checked((int)(paid * hourly)));
                module.NextFuelAt = checked(module.NextFuelAt.Value + paid * StructureRules.FuelInterval);
                world.SaveChanges();
            }
            if (paid < cycles)
            {
                long offlineAt = module.NextFuelAt!.Value;
                module.Online = false;
                item.Online = false;
                structure.LowPowerAt ??= module.NextFuelAt;
                module.NextFuelAt = null;
                world.SaveChanges();
                StructureIndustry.Synchronize(world, catalog, structure, offlineAt);
            }
        }
        world.SaveChanges();
    }
}
