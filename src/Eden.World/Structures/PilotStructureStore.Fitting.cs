using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore : IPilotStructureFitting
{
    public StructureResult Fit(long pilotId, long structureId, IReadOnlyList<StructureServiceFit> equipment) =>
        FitUpdate(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                if (equipment.Count == 0 || equipment.Select(item => item.ItemId).Distinct().Count() != equipment.Count)
                {
                    throw new InvalidDataException("Choose distinct structure equipment items.");
                }
                var hull = StructureCatalog.Require(catalog, structure.TypeId);
                foreach (var fit in equipment)
                {
                    var item = AccessibleItem(world, pilot, structure, fit.ItemId);
                    var profile = StructureEquipmentCatalog.Require(catalog, item.TypeId);
                    var attributes = catalog.FittingAttributes(structure.TypeId);
                    int first = profile.Service ? StructureRules.FirstServiceSlot : profile.Fitting.FirstFlag;
                    int count = profile.Service
                        ? hull.ServiceSlots
                        : checked((int)attributes.GetValueOrDefault(profile.Fitting.SlotAttribute));
                    var slots = world
                        .Filter<OwnedItem>(row => row.LocationId == structure.Id && row.OwnerId == structure.CorporationId)
                        .Select(row => row.FlagId)
                        .ToHashSet();
                    int slot = fit.Slot ?? Enumerable.Range(first, count).FirstOrDefault(flag => !slots.Contains(flag));
                    if (slot < first || slot >= first + count || slots.Contains(slot))
                    {
                        throw new InvalidDataException("The selected structure slot is unavailable.");
                    }
                    if (profile.Service)
                    {
                        var service = StructureCatalog.Service(catalog, item.TypeId);
                        StructureRules.CheckServiceFit(hull, service, slot);
                        if (
                            service.MaximumFitted > 0
                            && world
                                .Filter<OwnedItem>(row =>
                                    row.LocationId == structure.Id
                                    && row.OwnerId == structure.CorporationId
                                    && row.TypeId == item.TypeId
                                    && row.FlagId >= StructureRules.FirstServiceSlot
                                    && row.FlagId < StructureRules.FirstServiceSlot + StructureRules.MaximumServiceSlots
                                )
                                .Count() >= service.MaximumFitted
                        )
                        {
                            throw new InvalidDataException("The maximum number of these services is already fitted.");
                        }
                    }
                    if (item.Quantity > 1)
                    {
                        item.Quantity--;
                        item = world
                            .Add(
                                new OwnedItem
                                {
                                    Id = InventoryPersistence.NextId(world),
                                    TypeId = item.TypeId,
                                    Quantity = 1,
                                }
                            )
                            .Entity;
                    }
                    item.OwnerId = structure.CorporationId;
                    item.LocationId = structure.Id;
                    item.FlagId = slot;
                    item.Singleton = true;
                    item.Online = !profile.Service && profile.Fitting.Rack != ModuleRack.Rig;
                    if (profile.Service)
                    {
                        world.Add(
                            new SavedStructureService
                            {
                                ItemId = item.Id,
                                StructureId = structure.Id,
                                Slot = slot,
                            }
                        );
                    }
                }
            }
        );

    public StructureResult SetOnline(long pilotId, long structureId, long itemId, bool online)
    {
        RequireResidentFitting();
        bool service = WorldActionScope.Inspect(
            database,
            world => world.Set<SavedStructureService>().Find(itemId)?.StructureId == structureId
        );
        if (service)
        {
            return SetServiceOnline(pilotId, structureId, itemId, online);
        }
        return FitUpdate(
            pilotId,
            structureId,
            (world, catalog, structure, _, now) =>
            {
                var item = FittedEquipment(world, structure, itemId);
                if (InventoryDefinitions.IsRigSlot(item.FlagId) && online != item.Online)
                {
                    throw new InvalidDataException("Structure rigs apply while fitted and have no online switch.");
                }
                if (world.Set<ModuleCooldown>().Find(itemId)?.ReadyAt > now)
                {
                    throw new InvalidDataException("Wait for the paid equipment cycle to finish.");
                }
                item.Online = online;
            }
        );
    }

    public StructureResult Remove(long pilotId, long structureId, long itemId) =>
        FitUpdate(
            pilotId,
            structureId,
            (world, _, structure, _, now) =>
            {
                var item = FittedEquipment(world, structure, itemId);
                if (
                    world.Set<ModuleCooldown>().Find(itemId)?.ReadyAt > now
                    || world.Filter<OwnedItem>(row => row.LocationId == itemId).Any()
                )
                {
                    throw new InvalidDataException("Stop the paid cycle and unload the equipment before removing it.");
                }
                if (InventoryDefinitions.IsRigSlot(item.FlagId))
                {
                    world.Remove(item);
                }
                else
                {
                    item.OwnerId = pilotId;
                    item.FlagId = (int)InventoryFlag.Hangar;
                    item.Online = false;
                    item.Singleton = false;
                }
            }
        );

    StructureFittingState IPilotStructureFitting.Read(long pilotId, long structureId)
    {
        RequireResidentFitting();
        return Execute(
            (world, catalog, _) =>
            {
                var structure = Require(world, structureId);
                Manage(world, structure, pilotId);
                return StructureEquipmentPersistence.Read(world, catalog, structure);
            },
            pilotId: pilotId,
            structureId: structureId
        );
    }

    private StructureResult FitUpdate(
        long pilotId,
        long structureId,
        Action<WorldAction, CatalogDbContext, SavedStructure, Eden.Data.World.Characters.Pilot, long> change
    )
    {
        RequireResidentFitting();
        return Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, now) =>
            {
                if (pilot.ActiveShip.StructureId != structure.Id || pilot.ActiveShip.Position is not null)
                {
                    throw new InvalidDataException("Dock in the structure before changing its defensive fitting.");
                }
                var before = ShipHealth.DefenseFrom(StructureEquipmentPersistence.Read(world, catalog, structure).Attributes);
                var battle = StructureBattlePersistence.Read(world, catalog, structure);
                if (
                    !structure.HasCore
                    || structure.State != StructureState.ShieldVulnerable
                    || structure.UnanchorAt is not null
                    || battle.Health.Shield < before.Shield
                    || battle.RepairAt is not null
                )
                {
                    throw new InvalidDataException(
                        "Structure fitting requires an operational core, full shields and no active repair or reinforcement."
                    );
                }
                change(world, catalog, structure, pilot, now);
                StructureEquipmentPersistence.Check(world, catalog, structure);
            }
        );
    }

    private void RequireResidentFitting()
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            throw new InvalidOperationException("Defensive structure fitting requires the resident world.");
        }
    }

    private static OwnedItem FittedEquipment(WorldAction world, SavedStructure structure, long itemId) =>
        world.Set<OwnedItem>().Find(itemId) is { } item
        && item.OwnerId == structure.CorporationId
        && item.LocationId == structure.Id
        && InventoryDefinitions.IsFittedSlot(item.FlagId)
            ? item
            : throw new InvalidDataException("This equipment is not fitted to this structure.");
}
