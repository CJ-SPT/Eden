using System.Globalization;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Ammunition;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Fittings;

[Injectable]
internal sealed partial class PilotDeveloperFittingStore(WorldDatabase database, TimeProvider clock) : IPilotDeveloperFittings
{
    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Developer fittings require a running world.");

    public DeveloperShipSetup Setup(long shipId) =>
        Resident.Economy.Inspect(world =>
        {
            var ship = world.Ships.Find(shipId) ?? throw new InvalidDataException("Choose an assembled ship.");
            return new DeveloperShipSetup(
                ship.TypeId,
                Fitted(world, ship.OwnerId, ship.Id).ToDictionary(item => item.FlagId, item => item.TypeId)
            );
        });

    public DeveloperFitChange Fit(long pilot, long ship, DeveloperFitRequest request) =>
        Change(
            pilot,
            ship,
            (world, catalog, owner, now) =>
            {
                var type = Type(catalog, request.Type);
                var category = (InventoryCategory)type.CategoryId;
                if (request.Quantity <= 0 || catalog.Read(CatalogData.Client.Types, (long)type.Id)?.Published != 1)
                {
                    throw new InvalidDataException("Choose a published type and positive quantity.");
                }
                if (category == InventoryCategory.Charge)
                {
                    return LoadCharge(world, catalog, owner, type.Id, request, now);
                }
                if (category is InventoryCategory.Fighter or InventoryCategory.Drone)
                {
                    PlaceInBay(world, catalog, owner, type.Id, category, request);
                    return null;
                }
                int maximumRack = (int)InventoryFlag.HighSlot7 - (int)InventoryFlag.HighSlot0 + 1;
                if (
                    category is not (InventoryCategory.Module or InventoryCategory.Subsystem)
                    || request.Quantity > maximumRack
                    || request.Flag.HasValue && request.Quantity != 1
                )
                {
                    throw new InvalidDataException("Choose a supported module count and specify one slot per explicit fit.");
                }
                bool inSpace = owner.ActiveShip.Position is not null;
                for (int i = 0; i < request.Quantity; i++)
                {
                    long item =
                        world
                            .Filter<OwnedItem>(row =>
                                row.OwnerId == owner.Id
                                && row.LocationId == ship
                                && row.TypeId == type.Id
                                && row.FlagId == (int)InventoryFlag.Cargo
                            )
                            .OrderBy(row => row.Id)
                            .Select(row => (long?)row.Id)
                            .FirstOrDefault()
                        ?? InventoryPersistence.MergeOutput(world, catalog, owner.Id, ship, (int)InventoryFlag.Cargo, type.Id, 1).Item.Id;
                    world.SaveChanges();
                    var changed = PilotFittingStore.Fit(
                        world,
                        catalog,
                        owner.Id,
                        [item],
                        ship,
                        request.Flag ?? (int)InventoryFlag.AutoFit,
                        now,
                        mobileRefit: inSpace
                    );
                    foreach (
                        var fitted in changed.Where(change =>
                            InventoryDefinitions.IsFittedSlot(change.Item.FlagId)
                            && !InventoryDefinitions.IsRigSlot(change.Item.FlagId)
                            && change.Previous?.FlagId != change.Item.FlagId
                        )
                    )
                    {
                        PilotFittingStore.SetOnline(
                            world,
                            catalog,
                            owner.Id,
                            ship,
                            fitted.Item.Id,
                            true,
                            now,
                            mobileRefit: inSpace,
                            allowOfflineIfInsufficient: true
                        );
                    }
                }
                return null;
            }
        );

    public DeveloperFitChange Unload(long pilot, long ship, string selector) =>
        Change(
            pilot,
            ship,
            (world, catalog, owner, now) =>
            {
                var items = Fitted(world, pilot, ship);
                if (!selector.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    var match = long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out long item)
                        ? items.FirstOrDefault(row => row.Id == item)
                        : null;
                    if (match is null)
                    {
                        int type = Type(catalog, selector).Id;
                        match = items.FirstOrDefault(row => row.TypeId == type);
                    }
                    items = [match ?? throw new InvalidDataException("That module is not fitted to the active ship.")];
                }
                if (items.Length == 0)
                {
                    return null;
                }
                bool inSpace = owner.ActiveShip.Position is not null;
                int flag = (int)(inSpace ? InventoryFlag.Cargo : InventoryFlag.Hangar);
                var magazines = items
                    .Select(module => (Module: module, Charge: PilotAmmunitionStore.Contents(world, module.Id)))
                    .Where(pair => pair.Charge is not null)
                    .Select(pair => new MagazineChange(PilotAmmunitionStore.Read(world, pair.Module, pair.Charge!), null))
                    .ToArray();
                PilotInventoryStore.Move(
                    world,
                    catalog,
                    pilot,
                    inSpace ? ship : owner.ActiveShip.DockedLocationId,
                    new(items.Select(row => row.Id).ToArray(), ship, flag, null),
                    now,
                    mobileRefit: inSpace,
                    preserveRigs: true
                );
                return new([], magazines, 0);
            }
        );

    public DeveloperFitChange Online(long pilot, long ship) =>
        Change(
            pilot,
            ship,
            (world, catalog, owner, now) =>
            {
                foreach (var item in Fitted(world, pilot, ship).Where(row => !row.Online && !InventoryDefinitions.IsRigSlot(row.FlagId)))
                {
                    PilotFittingStore.SetOnline(
                        world,
                        catalog,
                        pilot,
                        ship,
                        item.Id,
                        true,
                        now,
                        mobileRefit: owner.ActiveShip.Position is not null,
                        allowOfflineIfInsufficient: true
                    );
                }
                return null;
            }
        );

    private DeveloperFitChange Change(
        long pilot,
        long ship,
        Func<EconomyTransaction, CatalogDbContext, Pilot, long, AmmunitionTransfer?> change
    )
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilot], ships: [ship]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var owner = world.Pilots.Find(pilot);
        if (owner is null || owner.ActiveShipId != ship || owner.ActiveShip.StoredInId is not null)
        {
            throw new InvalidDataException("Use the current active ship.");
        }
        world.RequireUncloaked(ship);
        var before = Inventory();
        var ammunition = change(world, catalog, owner, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
        world.SaveChanges();
        var after = Inventory();
        var items = before
            .Keys.Union(after.Keys)
            .Order()
            .Where(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id))
            .Select(id => new InventoryChange(
                after.GetValueOrDefault(id) ?? before[id] with { Quantity = 0 },
                before.GetValueOrDefault(id)
            ))
            .ToArray();
        world.Commit();
        return new(items, ammunition?.Magazines ?? [], ammunition?.ReloadMilliseconds ?? 0);

        Dictionary<long, InventoryItem> Inventory()
        {
            var list = PilotInventoryStore.List(world, catalog, pilot, ship);
            return (
                owner.ActiveShip.Position is null
                    ? list.Concat(PilotInventoryStore.List(world, catalog, pilot, owner.ActiveShip.DockedLocationId))
                    : list
            ).ToDictionary(item => item.Id);
        }
    }

    private static AmmunitionTransfer LoadCharge(
        EconomyTransaction world,
        CatalogDbContext catalog,
        Pilot owner,
        int type,
        DeveloperFitRequest request,
        long now
    )
    {
        long ship = owner.ActiveShipId;
        var module =
            Fitted(world, owner.Id, ship).SingleOrDefault(row => row.FlagId == request.Flag)
            ?? throw new InvalidDataException("Specify a fitted slot for ammunition.");
        var stack = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = owner.Id,
                    TypeId = type,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = request.Quantity,
                }
            )
            .Entity;
        world.SaveChanges();
        return PilotAmmunitionStore.Load(world, catalog, owner.Id, ship, [module.Id], [stack.Id], ship, now);
    }

    private static void PlaceInBay(
        EconomyTransaction world,
        CatalogDbContext catalog,
        Pilot owner,
        int type,
        InventoryCategory category,
        DeveloperFitRequest request
    )
    {
        int flag = (int)(category == InventoryCategory.Drone ? InventoryFlag.DroneBay : InventoryFlag.FighterBay);
        if (request.Flag is int specified && specified != flag)
        {
            throw new InvalidDataException("Choose the corresponding drone or fighter bay.");
        }
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, owner.Id, owner.ActiveShipId, flag);
        double free = capacity - InventoryPersistence.UsedVolume(world, catalog, owner.Id, owner.ActiveShipId, flag);
        double volume = PilotInventoryStore.Volume(catalog, type) * request.Quantity;
        if (capacity <= 0 || free < volume)
        {
            throw new CargoCapacityException(Math.Max(0, free), volume);
        }
        InventoryPersistence.MergeOutput(world, catalog, owner.Id, owner.ActiveShipId, flag, type, request.Quantity);
    }

    private static OwnedItem[] Fitted(WorldAction world, long pilot, long ship) =>
        world
            .Filter<OwnedItem>(row => row.OwnerId == pilot && row.LocationId == ship)
            .AsEnumerable()
            .Where(row => InventoryDefinitions.IsFittedSlot(row.FlagId))
            .OrderBy(row => row.FlagId)
            .ToArray();

    private static Eden.Data.Catalog.CatalogItemDefinition Type(CatalogDbContext catalog, string name)
    {
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
        {
            return catalog.RequireItem(id);
        }
        var matches = catalog
            .Definitions.Items.Values.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches is [var type] ? type : throw new InvalidDataException("Choose an exact, unambiguous catalog type.");
    }
}
