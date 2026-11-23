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
            var definition = catalog.Read(CatalogData.Client.Skinlicenses, CatalogKeyFor(item.TypeId));
            if (
                definition is not { SkinID: long skinId64, Duration: long days }
                || definition.LicenseTypeID != item.TypeId
                || skinId64 is <= 0 or > int.MaxValue
                || catalog.Definition(s_skin, (int)skinId64) is null
            )
            {
                throw new InvalidDataException("That item is not a ship SKIN license.");
            }
            int skinId = (int)skinId64;
            var saved = savedLicenses.Find(pilotId, skinId);
            if (!plannedSkins.Add(skinId) || saved is not null && ShipSkinLicenseRules.IsActive(saved.Expires, now))
            {
                throw new InvalidDataException("That SKIN license is already active on this character.");
            }
            ShipSkinLicense license = new(skinId, ShipSkinLicenseRules.Expiration(days, now), definition.IsSingleUse == true);
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

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static string CatalogKeyFor(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static ShipSkinLicense Snapshot(SavedShipSkinLicense license) => new(license.SkinId, license.Expires, license.IsSingleUse);
}
