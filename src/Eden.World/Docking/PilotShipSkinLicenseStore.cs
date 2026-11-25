using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Docking;

[Injectable]
internal sealed class PilotShipSkinLicenseStore(WorldDatabase database, TimeProvider clock) : IPilotShipSkinLicenses
{
    private sealed record CatalogLicense(int SkinId, long Days, bool IsSingleUse, ClientSkins Skin);

    private static readonly CatalogDefinition<ClientSkins?> s_skin = new(
        "ship-skin-license",
        (catalog, id) =>
            catalog.Read(CatalogData.Client.Skins, CatalogKeyFor(id)) is { IsStructureSkin: not true } skin && skin.SkinID == id
                ? skin
                : null
    );

    public IReadOnlyList<ShipSkinLicense> List(long pilotId, int? hullTypeId = null) =>
        Resident.Economy.Inspect(
            world =>
            {
                long now = Now;
                using var catalog = hullTypeId is not null ? new CatalogDbContext(database.CatalogPath) : null;
                return world
                    .Filter<SavedShipSkinLicense>(license => license.PilotId == pilotId)
                    .AsEnumerable()
                    .Where(license =>
                        ShipSkinLicenseRules.IsActive(license.Expires, now)
                        && (hullTypeId is not int hull || SupportsHull(catalog!, license.SkinId, hull))
                    )
                    .OrderBy(license => license.SkinId)
                    .Select(Snapshot)
                    .ToArray();
            },
            new(pilots: [pilotId])
        );

    public ShipSkinLicense? Read(long pilotId, int skinId) =>
        Resident.Economy.Inspect(
            world =>
                world.Set<SavedShipSkinLicense>().Find(pilotId, skinId) is { } license
                && ShipSkinLicenseRules.IsActive(license.Expires, Now)
                    ? Snapshot(license)
                    : null,
            new(pilots: [pilotId])
        );

    public IReadOnlyList<ShipSkinActivation> Activate(long pilotId, IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }
        using var world = Resident.Economy.Begin(new(pilots: [pilotId]));
        if (
            world.Pilots.Find(pilotId) is not { } pilot
            || pilot.ActiveShip is not { StoredInId: null } ship
            || ship.OwnerId != pilotId
            || world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is not null
        )
        {
            throw new InvalidDataException("Select an active pilot to activate a SKIN license.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        var savedLicenses = world.Set<SavedShipSkinLicense>();
        HashSet<int> plannedSkins = [];
        List<(OwnedItem Item, InventoryItem Before, SavedShipSkinLicense? Saved, ShipSkinLicense License)> plan = [];
        foreach (long id in itemIds.Distinct())
        {
            if (
                id <= 0
                || InventoryPersistence.OwnedStack(world, pilotId, id) is not { Quantity: > 0 } item
                || !PilotInventoryStore.AccessibleItems(world, pilot, item.LocationId, item.FlagId)
                || world.Set<SavedContractEscrow>().Find(id) is not null
                || world.ContainedInventoryIds(id).Count != 0
            )
            {
                throw new InvalidDataException("That SKIN license is not in your accessible personal inventory.");
            }
            if (LicenseForItem(catalog, item.TypeId) is not { } definition)
            {
                throw new InvalidDataException("That item is not a ship SKIN license.");
            }
            int skinId = definition.SkinId;
            var saved = savedLicenses.Find(pilotId, skinId);
            if (!plannedSkins.Add(skinId) || saved is not null && ShipSkinLicenseRules.IsActive(saved.Expires, now))
            {
                throw new InvalidDataException("That SKIN license is already active on this character.");
            }
            ShipSkinLicense license = new(skinId, ShipSkinLicenseRules.Expiration(definition.Days, now), definition.IsSingleUse);
            plan.Add((item, PilotInventoryStore.Read(catalog, item), saved, license));
        }

        List<ShipSkinActivation> activations = [];
        foreach (var (item, before, saved, license) in plan)
        {
            var record = saved ?? world.Add(new SavedShipSkinLicense { PilotId = pilotId, SkinId = license.SkinId }).Entity;
            record.Expires = license.Expires;
            record.IsSingleUse = license.IsSingleUse;
            item.Quantity--;
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
            activations.Add(new(license, new(before with { Quantity = item.Quantity }, before)));
        }
        world.Commit();
        return activations;
    }

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Ship SKIN licenses require the resident world.");

    internal static bool SupportsHull(CatalogDbContext catalog, int skinId, int hullTypeId) =>
        catalog.Definition(s_skin, skinId)?.Types?.Contains(hullTypeId) == true;

    internal static bool IsPublishedShipLicense(CatalogDbContext catalog, int typeId) =>
        catalog.Read(CatalogData.Client.Types, (long)typeId) is { Published: 1, Volume: double volume }
        && double.IsFinite(volume)
        && volume >= 0
        && LicenseForItem(catalog, typeId) is { Days: ShipSkinLicenseRules.Permanent or > 0, Skin.Types: { IsEmpty: false } };

    private static CatalogLicense? LicenseForItem(CatalogDbContext catalog, int typeId) =>
        catalog.Read(CatalogData.Client.Skinlicenses, CatalogKeyFor(typeId))
            is { SkinID: > 0 and <= int.MaxValue and long id, Duration: long days } definition
        && definition.LicenseTypeID == typeId
        && catalog.Definition(s_skin, (int)id) is { } skin
            ? new((int)id, days, definition.IsSingleUse == true, skin)
            : null;

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static string CatalogKeyFor(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static ShipSkinLicense Snapshot(SavedShipSkinLicense license) => new(license.SkinId, license.Expires, license.IsSingleUse);
}
