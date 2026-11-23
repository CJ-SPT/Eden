using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Docking;

[Injectable]
internal sealed class PilotShipSkinStore(WorldDatabase database, TimeProvider clock) : IPilotShipSkins
{
    public ShipSkinViewer? Viewer(long accountId, long pilotId) =>
        Resident.Economy.Inspect(
            world =>
            {
                if (accountId <= 0 || LivingPilot(world, pilotId) is not { } pilot || pilot.AccountId != accountId)
                {
                    return null;
                }
                var ship = pilot.ActiveShip;
                SpacePosition? position = ship.Position is { } flight ? new(flight.X, flight.Y, flight.Z) : null;
                return new ShipSkinViewer(pilotId, ship.Id, ViewerSystem(world, ship), position);
            },
            new(pilots: [pilotId])
        );

    public AppliedShipSkin? ReadOwned(long pilotId, long shipId) =>
        Resident.Economy.Inspect(
            world =>
                AccessibleShip(world, pilotId, shipId) is { } ship
                    ? ReadCurrent(ship, world.Set<SavedAppliedShipSkin>().Find(shipId), Now)
                    : null,
            new(pilots: [pilotId], ships: [shipId])
        );

    public IReadOnlyList<AppliedShipSkin> ReadVisible(IReadOnlyCollection<long> shipIds) =>
        shipIds.Count == 0
            ? []
            : Resident.Economy.Inspect(
                world =>
                {
                    long now = Now;
                    var skins = world.Set<SavedAppliedShipSkin>();
                    var losses = world.Set<ShipLoss>();
                    List<AppliedShipSkin> visible = [];
                    foreach (long id in shipIds.Distinct())
                    {
                        if (world.Ships.Find(id) is { Position: not null, StoredInId: null } ship && losses.Find(id) is null)
                        {
                            visible.Add(ReadCurrent(ship, skins.Find(id), now));
                        }
                    }
                    return visible;
                },
                new(ships: shipIds)
            );

    public ShipSkinApplication Apply(long pilotId, long shipId, int? skinId)
    {
        using var world = Resident.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        var ship =
            AccessibleShip(world, pilotId, shipId)
            ?? throw new InvalidDataException("Use your active ship or an owned ship in your current hangar or ship bay.");
        long now = Now;
        var saved = world.Set<SavedAppliedShipSkin>().Find(shipId);
        var before = ReadCurrent(ship, saved, now);
        long? expires = null;
        if (skinId is int id)
        {
            var license = world.Set<SavedShipSkinLicense>().Find(pilotId, id);
            if (license is null || !ShipSkinLicenseRules.IsActive(license.Expires, now))
            {
                throw new InvalidDataException("That SKIN license is not active on this character.");
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (!PilotShipSkinLicenseStore.SupportsHull(catalog, id, ship.TypeId))
            {
                throw new InvalidDataException("That SKIN is not valid for this ship type.");
            }
            // Each application keeps the entitlement's expiry at the time of application.
            expires = license.Expires;
        }

        AppliedShipSkin after = new(shipId, pilotId, skinId, expires);
        if (skinId is null)
        {
            if (saved is not null)
            {
                world.Remove(saved);
            }
        }
        else if (before != after)
        {
            var row = saved ?? world.Add(new SavedAppliedShipSkin { ShipId = shipId }).Entity;
            row.PilotId = pilotId;
            row.SkinId = skinId.Value;
            row.Expires = expires;
        }
        world.Commit();
        return new(after, before != after);
    }

    private LiveWorld Resident =>
        LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Ship SKIN appearance requires the resident world.");

    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private int ViewerSystem(WorldAction world, Ship ship)
    {
        if (ship.Position?.SystemId is int system && system > 0)
        {
            return system;
        }
        if (ship is { Position: null, StructureId: long structureId })
        {
            return world.StructureSystemId(structureId);
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.RequireStation(ship.StationId).SolarSystemId;
    }

    private static Pilot? LivingPilot(WorldAction world, long pilotId) =>
        world.Pilots.Find(pilotId) is { ActiveShip: { StoredInId: null } ship } pilot
        && ship.OwnerId == pilotId
        && world.Set<SavedCharacterDeletion>().Find(pilotId)?.DeletedAt is null
        && world.Set<ShipLoss>().Find(ship.Id) is null
            ? pilot
            : null;

    private static Ship? AccessibleShip(WorldAction world, long pilotId, long shipId)
    {
        if (
            LivingPilot(world, pilotId) is not { } pilot
            || world.Ships.Find(shipId) is not { } ship
            || ship.OwnerId != pilotId
            || world.Set<ShipLoss>().Find(shipId) is not null
        )
        {
            return null;
        }
        var active = pilot.ActiveShip;
        var parent = ship.StoredInId is long parentId ? world.Ships.Find(parentId) : null;
        return ShipStorageRules.CanRead(
            new(pilotId, active.Id, active.DockedLocationId, active.Position is null),
            ShipStoragePersistence.Location(ship),
            parent is null ? null : ShipStoragePersistence.Location(parent)
        )
            ? ship
            : null;
    }

    private static AppliedShipSkin ReadCurrent(Ship ship, SavedAppliedShipSkin? saved, long now) =>
        saved is not null && saved.PilotId == ship.OwnerId && ShipSkinLicenseRules.IsActive(saved.Expires, now)
            ? new(ship.Id, ship.OwnerId, saved.SkinId, saved.Expires)
            : new(ship.Id, ship.OwnerId, null, null);
}
