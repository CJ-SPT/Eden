using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Inventory;

[Injectable]
internal sealed partial class CargoContainerStore(WorldDatabase database, TimeProvider clock) : ICargoContainers
{
    private LiveWorld Live => LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Containers require a resident world.");

    public bool Handles(long itemId) =>
        LiveWorld.Find(database.Path) is { } live
        && live.Economy.Inspect(world =>
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            return world.Set<OwnedItem>().Find(itemId) is { } item
                && CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, item));
        });

    public CargoContainerContents Read(long pilotId, long itemId, CargoContainerAccess access, bool includeContents = true) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                if (includeContents)
                {
                    RequireOpen(world, catalog, item, access);
                }
                return new CargoContainerContents(
                    PilotInventoryStore.Read(catalog, item),
                    includeContents ? PilotInventoryStore.List(world, catalog, item.OwnerId, item.Id) : [],
                    catalog.RequireItem(item.TypeId).Capacity ?? 0
                );
            }
        );

    public CargoContainerContents Preview(long pilotId, long itemId, long stationId, CargoContainerAccess access) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                RequireOpen(world, catalog, item, access);
                return new CargoContainerContents(
                    PilotInventoryStore.Read(catalog, item),
                    PilotInventoryStore.List(world, catalog, item.OwnerId, item.Id),
                    catalog.RequireItem(item.TypeId).Capacity ?? 0
                );
            },
            stationId
        );

    public IReadOnlyList<PersonalAssetName> Names(long pilotId, IReadOnlySet<long> itemIds)
    {
        if (LiveWorld.Find(database.Path) is not { } live || itemIds.Count == 0)
        {
            return [];
        }
        return live.Economy.Inspect<IReadOnlyList<PersonalAssetName>>(world =>
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            List<PersonalAssetName> names = [];
            foreach (long id in itemIds)
            {
                if (world.Set<SavedCargoContainer>().Find(id)?.Name is not { } name)
                {
                    continue;
                }
                try
                {
                    Require(world, catalog, pilotId, id);
                    names.Add(new(id, name));
                }
                catch (InvalidDataException)
                {
                    // Name-cache requests may mix accessible assets with arbitrary or stale location IDs.
                }
            }
            return names;
        });
    }

    public string Rename(long pilotId, long itemId, string name)
    {
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = Require(world, catalog, pilotId, itemId, administer: true);
        var settings = Settings(world, itemId);
        string result = CargoContainerRules.Name(name, catalog.RequireItem(item.TypeId).Name);
        settings.Name = result;
        world.Commit();
        return result;
    }

    public bool HasPassword(long pilotId, long itemId, CargoContainerPasswordType type) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                CheckPasswordTarget(catalog, item, type);
                return Password(world.Set<SavedCargoContainer>().Find(itemId), type) is not null;
            }
        );

    public void SetPassword(long pilotId, long itemId, CargoContainerPasswordType type, string? oldPassword, string newPassword)
    {
        CargoContainerRules.CheckPassword(newPassword);
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = Require(world, catalog, pilotId, itemId, administer: true);
        CheckPasswordTarget(catalog, item, type);
        var settings = Settings(world, itemId);
        if (Password(settings, type) is { } current && current != oldPassword)
        {
            throw new InvalidDataException("The old container password is incorrect.");
        }
        if (type == CargoContainerPasswordType.General)
        {
            settings.GeneralPassword = newPassword;
        }
        else
        {
            settings.ConfigurationPassword = newPassword;
        }
        world.Commit();
    }

    public string? RetrievePassword(long pilotId, long itemId, CargoContainerPasswordType type) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                Require(world, catalog, pilotId, itemId, administer: true);
                CheckPasswordTarget(catalog, item, type);
                return Password(world.Set<SavedCargoContainer>().Find(itemId), type);
            }
        );

    public void EnterPassword(long pilotId, long itemId, CargoContainerPasswordType type, string password) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                CheckPasswordTarget(catalog, item, type);
                if (Password(world.Set<SavedCargoContainer>().Find(itemId), type) is { } current && current != password)
                {
                    // EnterPassword must raise on failure: the native wrapper retries the original call regardless of its return value.
                    throw new InvalidDataException("The container password is incorrect.");
                }
                return true;
            }
        );

    public CargoContainerConfiguration Configuration(long pilotId, long itemId, CargoContainerAccess access) =>
        Inspect(
            pilotId,
            itemId,
            (world, catalog, item) =>
            {
                Require(world, catalog, pilotId, itemId, administer: true);
                CheckAuditContainer(catalog, item);
                var settings = world.Set<SavedCargoContainer>().Find(itemId);
                RequirePassword(settings, CargoContainerPasswordType.Configuration, access);
                return settings?.Configuration ?? CargoContainerConfiguration.None;
            }
        );

    public void Configure(long pilotId, long itemId, CargoContainerConfiguration configuration, CargoContainerAccess access)
    {
        CargoContainerRules.CheckConfiguration(configuration);
        using var world = Live.Economy.Begin(new(pilots: [pilotId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = Require(world, catalog, pilotId, itemId, administer: true);
        CheckAuditContainer(catalog, item);
        var settings = Settings(world, itemId);
        RequirePassword(settings, CargoContainerPasswordType.Configuration, access);
        settings.Configuration = configuration;
        world.Commit();
    }

    private T Inspect<T>(long pilotId, long itemId, Func<WorldAction, CatalogDbContext, OwnedItem, T> read, long? previewStation = null) =>
        Live.Economy.Inspect(
            world =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                return read(world, catalog, Require(world, catalog, pilotId, itemId, previewStation: previewStation));
            },
            new(pilots: [pilotId])
        );

    internal static OwnedItem Require(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long itemId,
        bool administer = false,
        long? previewStation = null
    )
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var item = world.Set<OwnedItem>().Find(itemId);
        if (item is not { Singleton: true } || !CargoContainerAssemblyRules.IsContainer(PilotInventoryStore.Read(catalog, item)))
        {
            throw new InvalidDataException("Choose an assembled cargo container.");
        }
        if (item.OwnerId == pilotId)
        {
            if (
                !PilotInventoryStore.AccessibleItems(world, pilot, item.LocationId, item.FlagId)
                && !(
                    previewStation == item.LocationId
                    && item.FlagId == (int)InventoryFlag.Hangar
                    && catalog.FindStation(item.LocationId) is not null
                )
            )
            {
                throw new InvalidDataException("Use a container in an owned ship hold or hangar at your current location.");
            }
        }
        else if (item.OwnerId == pilot.CorporationId)
        {
            var office = CorporationStorageStore.LocalOffice(world, pilotId, item.LocationId);
            var corporation = CorporationRoleStore.Current(world, pilotId);
            var access = CorporationRoleStore.Effective(world, pilotId, corporation);
            if (
                !CorporationStorageRules.CanQuery(access, office.StationId, corporation.StationId, item.FlagId)
                || administer && !access.Roles.Has(CorporationRole.EquipmentConfig)
            )
            {
                throw new InvalidDataException("Corporation division query and equipment configuration rights are required.");
            }
        }
        else
        {
            throw new InvalidDataException("This container does not belong to you or your corporation.");
        }
        return item;
    }

    private static SavedCargoContainer Settings(WorldAction world, long itemId) =>
        world.Set<SavedCargoContainer>().Find(itemId) ?? world.Add(new SavedCargoContainer { ItemId = itemId }).Entity;

    private static string? Password(SavedCargoContainer? settings, CargoContainerPasswordType type) =>
        type == CargoContainerPasswordType.General ? settings?.GeneralPassword : settings?.ConfigurationPassword;

    private static void RequirePassword(SavedCargoContainer? settings, CargoContainerPasswordType type, CargoContainerAccess access)
    {
        if (Password(settings, type) is { } password && access.Password(type) != password)
        {
            throw new CargoContainerPasswordRequiredException(type);
        }
    }

    internal static void RequireOpen(WorldAction world, CatalogDbContext catalog, OwnedItem item, CargoContainerAccess access)
    {
        var settings = world.Set<SavedCargoContainer>().Find(item.Id);
        if (
            catalog.RequireItem(item.TypeId).GroupId == (int)PersonalAssetContainerGroup.SecureCargo
            || settings?.Configuration.HasFlag(CargoContainerConfiguration.PasswordToOpen) == true
        )
        {
            RequirePassword(settings, CargoContainerPasswordType.General, access);
        }
    }

    private static void CheckPasswordTarget(CatalogDbContext catalog, OwnedItem item, CargoContainerPasswordType type)
    {
        CargoContainerRules.CheckPasswordType(type);
        int group = catalog.RequireItem(item.TypeId).GroupId;
        if (
            !CargoContainerRules.SupportsPassword(group)
            || type == CargoContainerPasswordType.Configuration && group != (int)PersonalAssetContainerGroup.AuditLogSecure
        )
        {
            throw new InvalidDataException("This container does not support that password type.");
        }
    }

    private static void CheckAuditContainer(CatalogDbContext catalog, OwnedItem item)
    {
        if (catalog.RequireItem(item.TypeId).GroupId != (int)PersonalAssetContainerGroup.AuditLogSecure)
        {
            throw new InvalidDataException("Choose an audit log secure container to configure.");
        }
    }
}
