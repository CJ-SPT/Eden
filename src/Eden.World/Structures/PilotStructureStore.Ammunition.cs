using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal sealed partial class PilotStructureStore : IPilotStructureAmmunition
{
    bool IPilotStructureAmmunition.Handles(long structureId) =>
        Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live
        && live.Economy.Inspect(world => world.Structure(structureId) is not null);

    StructureResult IPilotStructureAmmunition.Deposit(long pilotId, long structureId, long itemId, int? quantity) =>
        ((IPilotStructureAmmunition)this).Deposit(pilotId, structureId, [new(itemId, quantity)]);

    StructureResult IPilotStructureAmmunition.Deposit(long pilotId, long structureId, IReadOnlyList<StructureAmmoDeposit> items) =>
        AmmunitionUpdate(
            pilotId,
            structureId,
            (world, catalog, structure, pilot) =>
            {
                if (items.Count == 0 || items.Select(item => item.ItemId).Distinct().Count() != items.Count)
                {
                    throw new InvalidDataException("Choose distinct ammunition stacks to deposit.");
                }
                foreach (var deposit in items)
                {
                    var item = AccessibleItem(world, pilot, structure, deposit.ItemId);
                    if (catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge)
                    {
                        throw new InvalidDataException("The structure ammunition bay accepts charges and scripts.");
                    }
                    MoveAmmunition(
                        world,
                        item,
                        deposit.Quantity ?? item.Quantity,
                        new(structure.Id, structure.CorporationId, (int)InventoryFlag.Cargo)
                    );
                }
            }
        );

    StructureResult IPilotStructureAmmunition.Withdraw(long pilotId, long structureId, long itemId, int quantity, bool toCargo) =>
        AmmunitionUpdate(
            pilotId,
            structureId,
            (world, catalog, structure, pilot) =>
            {
                var item = world.Set<OwnedItem>().Find(itemId);
                if (
                    item is null
                    || item.LocationId != structure.Id
                    || item.OwnerId != structure.CorporationId
                    || item.FlagId != (int)InventoryFlag.Cargo
                    || catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Charge
                )
                {
                    throw new InvalidDataException("Choose an ammunition stack in this structure's bay.");
                }
                var destination = new StructureAmmoDestination(
                    toCargo ? pilot.ActiveShipId : structure.Id,
                    pilot.Id,
                    (int)(toCargo ? InventoryFlag.Cargo : InventoryFlag.Hangar)
                );
                CheckAmmunitionDestination(world, catalog, structure, pilot, destination, item.TypeId, quantity);
                MoveAmmunition(world, item, quantity, destination);
            }
        );

    IReadOnlyList<Magazine> IPilotStructureAmmunition.Read(long pilotId, long structureId) =>
        AmmunitionAction(pilotId, structureId, (world, _, structure, _, _) => Magazines(world, structure), docked: false);

    IReadOnlyDictionary<DogmaAttributeId, double> IPilotStructureAmmunition.ChargeAttributes(
        long pilotId,
        long structureId,
        long moduleId
    ) =>
        AmmunitionAction(
            pilotId,
            structureId,
            (world, catalog, structure, _, _) =>
                StructureEquipmentPersistence.Read(world, catalog, structure).Charges.GetValueOrDefault(moduleId)
                ?? throw new InvalidDataException("The structure module has no loaded charge."),
            docked: false
        );

    AmmunitionTransfer IPilotStructureAmmunition.Load(
        long pilotId,
        long structureId,
        IReadOnlyList<long> moduleIds,
        IReadOnlyList<long> chargeIds,
        long sourceId
    ) =>
        AmmunitionAction<AmmunitionTransfer>(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, now) =>
            {
                var modules = AmmunitionModules(world, catalog, structure, moduleIds, now, unloading: false);
                if (chargeIds.Count == 0 || chargeIds.Distinct().Count() != chargeIds.Count)
                {
                    throw new InvalidDataException("Choose distinct ammunition stacks.");
                }
                var sources = chargeIds
                    .Select(id => world.Set<OwnedItem>().Find(id) ?? throw new InvalidDataException("The ammunition stack is unavailable."))
                    .OrderBy(item => item.Id)
                    .ToArray();
                if (
                    sources.Any(item =>
                        item.LocationId != sourceId
                        || item.Quantity <= 0
                        || item.Singleton
                        || !(
                            item.OwnerId == structure.CorporationId && sourceId == structure.Id && item.FlagId == (int)InventoryFlag.Cargo
                            || item.OwnerId == pilot.Id && sourceId == pilot.ActiveShipId && item.FlagId == (int)InventoryFlag.Cargo
                            || item.OwnerId == pilot.Id && sourceId == structure.Id && item.FlagId == (int)InventoryFlag.Hangar
                        )
                    )
                    || sources.Select(item => item.TypeId).Distinct().Count() != 1
                )
                {
                    throw new InvalidDataException(
                        "Load one charge type from your cargo, docked hangar or this structure's ammunition bay."
                    );
                }
                var charge = StructureChargeCatalog.Require(catalog, sources[0].TypeId);
                double volume = PilotInventoryStore.Volume(catalog, charge.TypeId);
                var fitting = StructureEquipmentPersistence.Read(world, catalog, structure);
                var before = sources.ToDictionary(item => item.Id, item => PilotInventoryStore.Read(catalog, item));
                List<InventoryChange> changes = [];
                List<MagazineChange> magazines = [];
                double reload = 0;
                foreach (var module in modules)
                {
                    var profile = StructureEquipmentCatalog.Require(catalog, module.TypeId);
                    if (!StructureAmmunitionRules.Compatible(profile, charge))
                    {
                        throw new InvalidDataException("The charge group or size does not match this structure module.");
                    }
                    int limit = StructureAmmunitionRules.MaximumRounds(catalog.RequireItem(module.TypeId).Capacity ?? 0, volume);
                    long availableSources = sources.Sum(item => (long)item.Quantity);
                    if (availableSources == 0)
                    {
                        continue;
                    }
                    var loaded = MagazineContents(world, structure, module.Id);
                    var previous = loaded is null ? null : PilotAmmunitionStore.Read(world, module, loaded);
                    if (loaded is not null && loaded.TypeId != charge.TypeId)
                    {
                        var old = PilotInventoryStore.Read(catalog, loaded);
                        loaded.LocationId = structure.Id;
                        loaded.FlagId = (int)InventoryFlag.Cargo;
                        changes.Add(new(PilotInventoryStore.Read(catalog, loaded), old));
                        loaded = null;
                    }
                    int amount = checked((int)Math.Min(Math.Max(0, limit - (loaded?.Quantity ?? 0)), availableSources));
                    if (amount == 0)
                    {
                        continue;
                    }
                    loaded ??= world
                        .Add(
                            new OwnedItem
                            {
                                Id = InventoryPersistence.NextId(world),
                                TypeId = charge.TypeId,
                                OwnerId = structure.CorporationId,
                                LocationId = module.Id,
                                FlagId = (int)InventoryFlag.Cargo,
                            }
                        )
                        .Entity;
                    loaded.Quantity = checked(loaded.Quantity + amount);
                    int remaining = amount;
                    foreach (var source in sources.Where(item => item.Quantity > 0))
                    {
                        int take = Math.Min(source.Quantity, remaining);
                        if (take > 0)
                        {
                            Consume(world, source, take);
                            remaining -= take;
                        }
                        if (remaining == 0)
                        {
                            break;
                        }
                    }
                    double duration = fitting.Equipment[module.Id].GetValueOrDefault(DogmaAttributeId.ReloadTime);
                    long ready = StructureAmmunitionRules.ReloadDeadline(duration, now);
                    var cooldown =
                        world.Set<ModuleCooldown>().Find(module.Id) ?? world.Add(new ModuleCooldown { ModuleId = module.Id }).Entity;
                    cooldown.Kind = ModuleCooldownKind.Reload;
                    cooldown.ReadyAt = ready;
                    reload = Math.Max(reload, duration);
                    magazines.Add(new(previous, new(module.Id, structure.Id, module.FlagId, charge.TypeId, loaded.Quantity, ready)));
                }
                if (magazines.Count == 0)
                {
                    throw new InvalidDataException("The selected structure magazines are already full.");
                }
                foreach (var source in sources.Where(item => item.Quantity != before[item.Id].Quantity))
                {
                    changes.Add(new(PilotInventoryStore.Read(catalog, source), before[source.Id]));
                }
                StructureEquipmentPersistence.Check(world, catalog, structure);
                return new(changes, magazines, reload);
            }
        );

    AmmunitionTransfer IPilotStructureAmmunition.Unload(
        long pilotId,
        long structureId,
        IReadOnlyList<long> moduleIds,
        StructureAmmoDestination destination,
        int? quantity
    ) =>
        AmmunitionAction<AmmunitionTransfer>(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, now) =>
            {
                if (quantity is <= 0)
                {
                    throw new InvalidDataException("Choose a positive ammunition quantity.");
                }
                var modules = AmmunitionModules(world, catalog, structure, moduleIds, now, unloading: true);
                List<InventoryChange> changes = [];
                List<MagazineChange> magazines = [];
                foreach (var module in modules)
                {
                    var loaded = MagazineContents(world, structure, module.Id);
                    if (loaded is null)
                    {
                        continue;
                    }
                    int amount = quantity ?? loaded.Quantity;
                    var before = PilotInventoryStore.Read(catalog, loaded);
                    var previous = PilotAmmunitionStore.Read(world, module, loaded);
                    CheckAmmunitionDestination(world, catalog, structure, pilot, destination, loaded.TypeId, amount);
                    var moved = MoveAmmunition(world, loaded, amount, destination);
                    changes.Add(new(PilotInventoryStore.Read(catalog, moved), moved == loaded ? before : null));
                    magazines.Add(new(previous, moved == loaded ? null : PilotAmmunitionStore.Read(world, module, loaded)));
                }
                StructureEquipmentPersistence.Check(world, catalog, structure);
                return new(changes, magazines);
            }
        );

    private StructureResult AmmunitionUpdate(
        long pilotId,
        long structureId,
        Action<WorldAction, CatalogDbContext, SavedStructure, Pilot> change
    )
    {
        RequireResidentFitting();
        return Update(
            pilotId,
            structureId,
            (world, catalog, structure, pilot, _) =>
            {
                RequireAmmunitionDocking(structure, pilot);
                change(world, catalog, structure, pilot);
            }
        );
    }

    private T AmmunitionAction<T>(
        long pilotId,
        long structureId,
        Func<WorldAction, CatalogDbContext, SavedStructure, Pilot, long, T> action,
        bool docked = true
    )
    {
        RequireResidentFitting();
        return Execute(
            (world, catalog, now) =>
            {
                var structure = Require(world, structureId);
                var pilot = Manage(world, structure, pilotId);
                if (docked)
                {
                    RequireAmmunitionDocking(structure, pilot);
                }
                return action(world, catalog, structure, pilot, now);
            },
            pilotId: pilotId,
            structureId: structureId
        );
    }

    private static void RequireAmmunitionDocking(SavedStructure structure, Pilot pilot)
    {
        if (pilot.ActiveShip.StructureId != structure.Id || pilot.ActiveShip.Position is not null)
        {
            throw new InvalidDataException("Dock in the structure before moving or reloading its ammunition.");
        }
    }

    private static OwnedItem? MagazineContents(WorldAction world, SavedStructure structure, long moduleId)
    {
        var item = world.Filter<OwnedItem>(item => item.LocationId == moduleId).SingleOrDefault();
        if (item is not null && (item.OwnerId != structure.CorporationId || item.Quantity <= 0))
        {
            throw new InvalidDataException("The structure magazine has invalid ownership or quantity.");
        }
        return item;
    }

    internal static IReadOnlyList<Magazine> Magazines(WorldAction world, SavedStructure structure) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == structure.Id && item.OwnerId == structure.CorporationId)
            .Where(item => item.FlagId >= (int)InventoryFlag.LowSlot0 && item.FlagId <= (int)InventoryFlag.HighSlot7)
            .OrderBy(item => item.FlagId)
            .AsEnumerable()
            .Select(module =>
                MagazineContents(world, structure, module.Id) is { } loaded ? PilotAmmunitionStore.Read(world, module, loaded) : null
            )
            .OfType<Magazine>()
            .ToArray();

    private static OwnedItem[] AmmunitionModules(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        IReadOnlyList<long> ids,
        long now,
        bool unloading
    )
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Choose distinct fitted structure modules.");
        }
        var modules = ids.Select(id => FittedEquipment(world, structure, id)).OrderBy(item => item.FlagId).ToArray();
        foreach (var module in modules)
        {
            var profile = StructureEquipmentCatalog.Require(catalog, module.TypeId);
            if (
                profile.Service
                || profile.Fitting.Rack == ModuleRack.Rig
                || !unloading
                    && !Eden.Gameplay.Combat.MissileDefinitions.ChargeGroupAttributes.Any(attribute =>
                        profile.Attributes.GetValueOrDefault(attribute) > 0
                    )
            )
            {
                throw new InvalidDataException("This structure module does not accept ammunition.");
            }
            if (world.Set<ModuleCooldown>().Find(module.Id)?.ReadyAt > now)
            {
                throw new InvalidDataException("Wait for the paid module cycle or reload to finish.");
            }
        }
        return modules;
    }

    private static OwnedItem MoveAmmunition(WorldAction world, OwnedItem item, int quantity, StructureAmmoDestination destination)
    {
        if (quantity <= 0 || quantity > item.Quantity || item.Singleton && quantity != item.Quantity)
        {
            throw new InvalidDataException("The requested ammunition quantity is unavailable.");
        }
        if (quantity < item.Quantity)
        {
            item.Quantity -= quantity;
            item = world
                .Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        TypeId = item.TypeId,
                        Quantity = quantity,
                        VolumeOverride = item.VolumeOverride,
                        CrystalDamage = item.CrystalDamage,
                    }
                )
                .Entity;
        }
        item.OwnerId = destination.OwnerId;
        item.LocationId = destination.LocationId;
        item.FlagId = destination.FlagId;
        return item;
    }

    private static void CheckAmmunitionDestination(
        WorldAction world,
        CatalogDbContext catalog,
        SavedStructure structure,
        Pilot pilot,
        StructureAmmoDestination destination,
        int typeId,
        int quantity
    )
    {
        bool bay =
            destination.LocationId == structure.Id
            && destination.OwnerId == structure.CorporationId
            && destination.FlagId == (int)InventoryFlag.Cargo;
        bool hangar =
            destination.LocationId == structure.Id && destination.OwnerId == pilot.Id && destination.FlagId == (int)InventoryFlag.Hangar;
        bool cargo =
            destination.LocationId == pilot.ActiveShipId
            && destination.OwnerId == pilot.Id
            && destination.FlagId == (int)InventoryFlag.Cargo;
        if (!bay && !hangar && !cargo)
        {
            throw new InvalidDataException("Unload into this structure's ammunition bay, your docked hangar or active ship cargo.");
        }
        if (cargo)
        {
            double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilot.Id, pilot.ActiveShipId, destination.FlagId);
            double used = InventoryPersistence.UsedVolume(world, catalog, pilot.Id, pilot.ActiveShipId, destination.FlagId);
            double required = PilotInventoryStore.Volume(catalog, typeId) * quantity;
            if (used + required > capacity + InventoryDefinitions.ResourceTolerance)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used), required);
            }
        }
    }
}
