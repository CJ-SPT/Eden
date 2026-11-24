using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Information;

internal sealed record DirectionalObserver(long ShipId, int SystemId, SpacePosition Position, double MaximumRange);

internal sealed record DirectionalEntry(
    DirectionalScanContact Contact,
    long ExpiresAt = long.MaxValue,
    long? LoggedOffPilot = null,
    bool Ship = false
);

internal sealed partial class PilotInformationStore
{
    public IReadOnlyList<DirectionalScanContact> Directional(long pilotId, double angle, double range, SpacePosition direction, long now)
    {
        return LiveWorld.Execute(database.Path, () => DirectionalOwned(pilotId, angle, range, direction, now), nameof(Directional));
    }

    private IReadOnlyList<DirectionalScanContact> DirectionalOwned(
        long pilotId,
        double angle,
        double range,
        SpacePosition direction,
        long now
    )
    {
        range = InformationRules.DirectionalRange(range, InformationRules.MaximumDirectionalRange);
        _ = InformationRules.InCone(SpacePosition.Zero, SpacePosition.Zero, direction, angle, range);
        var live = LiveWorld.Find(database.Path);
        using var ownership = live?.Enter();
        var observer = live?.Reads?.DirectionalObserver(pilotId, () => LoadObserver(pilotId)) ?? LoadObserver(pilotId);
        range = InformationRules.DirectionalRange(range, observer.MaximumRange);
        IEnumerable<DirectionalEntry> contacts = live is null
            ? LoadDirectional(observer.SystemId, includeJetcans: true)
            : live.Reads?.DirectionalContacts(observer.SystemId, () => LoadDirectional(observer.SystemId))
                ?? LoadDirectional(observer.SystemId);
        if (live is not null)
        {
            var cans =
                live.Reads?.Jetcans(observer.SystemId, now)
                ?? Eden.World.Loot.JetcanStore.ReadContacts(live, database, observer.SystemId, now).Contacts;
            contacts = contacts.Concat(
                cans.Select(can => new DirectionalEntry(new(can.Id, can.TypeId, can.GroupId, can.Name, can.Position)))
            );
        }
        live?.Tractors.Advance(now);
        var origin = live?.Combat?.InformationPosition(observer.ShipId) ?? observer.Position;
        return ScanVisible(contacts, live, observer.ShipId, now)
            .Where(contact => InformationRules.InCone(origin, contact.Position, direction, angle, range))
            .OrderBy(contact => contact.Id)
            .ToArray();
    }

    // Autonomous hunters consume ship bearings with the same visibility rules as a player's scan.
    internal IEnumerable<DirectionalScanContact> DirectionalShips(
        EconomyTransaction world,
        CatalogDbContext catalog,
        Eden.Gameplay.Docking.DockedPilot pilot,
        long now
    )
    {
        var live = LiveWorld.Find(database.Path);
        var maximum = catalog.Definitions.Definition(s_directionalRange, pilot.ShipTypeId);
        return ScanVisible(DirectionalShipEntries(world, catalog, pilot.SystemId), live, pilot.ShipId, now)
            .Where(contact => contact.Position.DistanceTo(pilot.Position!) <= maximum);
    }

    private static readonly CatalogDefinition<double> s_directionalRange = new(
        "directional-scan-range",
        (catalog, type) =>
            InformationRules.DirectionalRange(
                InformationRules.MaximumDirectionalRange,
                catalog
                    .FittingAttributes(type)
                    .GetValueOrDefault(DogmaAttributeId.MaxDirectionalScanRange, InformationRules.MaximumDirectionalRange)
            )
    );

    private static readonly CatalogDefinition<bool> s_directionalImmunity = new(
        "directional-scan-immunity",
        (catalog, type) => catalog.FittingAttributes(type).GetValueOrDefault(DogmaAttributeId.DirectionalScanImmune) != 0
    );

    private IEnumerable<DirectionalScanContact> ScanVisible(IEnumerable<DirectionalEntry> entries, LiveWorld? live, long ownShip, long now)
    {
        var spatial = new Eden.World.Propulsion.PilotSpatialModuleStore(database);
        foreach (var entry in entries)
        {
            if (entry.Contact.Id == ownShip || entry.Ship && spatial.Status(entry.Contact.Id).Cloaked(now))
            {
                continue;
            }
            long expiry = entry.LoggedOffPilot is { } pilot
                ? live?.Combat?.DroneLogoffDeadline(pilot, entry.ExpiresAt) ?? entry.ExpiresAt
                : entry.ExpiresAt;
            if (expiry > now)
            {
                var contact = entry.Contact;
                var position = live?.Combat?.DirectionalPosition(contact.Id) ?? live?.Tractors.Position(contact.Id) ?? contact.Position;
                yield return position == contact.Position ? contact : contact with { Position = position };
            }
        }
    }

    private static IEnumerable<DirectionalEntry> DirectionalShipEntries(WorldAction world, CatalogDbContext catalog, int system)
    {
        var logoffs = world.Filter<SavedLogoff>(l => l.SystemId == system).ToDictionary(l => l.ShipId);
        foreach (
            var position in world
                .Filter<Eden.Data.World.Docking.FlightPosition>(p => p.SystemId == system)
                .Concat(world.Filter<Eden.Data.World.Docking.FlightPosition>(p => p.SystemId == null))
        )
        {
            var ship = world.Ships.Find(position.ShipId);
            if (ship?.Position is null || world.Set<SavedCapsuleerSuspension>().Find(ship.OwnerId) is not null)
            {
                continue;
            }
            int location = ship.Position!.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
            if (location != system || catalog.Definitions.Definition(s_directionalImmunity, ship.TypeId))
            {
                continue;
            }
            var type = catalog.RequireItem(ship.TypeId);
            logoffs.TryGetValue(ship.Id, out var logoff);
            yield return new(
                new(ship.Id, type.Id, type.GroupId, ship.Name, new(ship.Position.X, ship.Position.Y, ship.Position.Z)),
                logoff?.ExpiresAt ?? long.MaxValue,
                logoff?.PilotId,
                Ship: true
            );
        }
    }

    private DirectionalObserver LoadObserver(long pilotId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var pilot = Pilot(world, pilotId);
                var position = Position(pilot);
                double maximum = PilotFittingStore
                    .ShipAttributes(world, catalog, pilot)
                    .GetValueOrDefault(DogmaAttributeId.MaxDirectionalScanRange, InformationRules.MaximumDirectionalRange);
                return new DirectionalObserver(pilot.ActiveShipId, System(catalog, pilot), position, maximum);
            }
        );
    }

    private IReadOnlyList<DirectionalEntry> LoadDirectional(int system, bool includeJetcans = false)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => LoadDirectional(world, catalog, system, includeJetcans));
    }

    private static IReadOnlyList<DirectionalEntry> LoadDirectional(WorldAction world, CatalogDbContext catalog, int system, bool jetcans)
    {
        var contacts = DirectionalShipEntries(world, catalog, system).ToList();
        foreach (var wreck in world.Filter<WorldWreck>(w => w.SystemId == system))
        {
            contacts.Add(
                new(
                    new(wreck.Id, wreck.TypeId, catalog.RequireItem(wreck.TypeId).GroupId, wreck.Name, new(wreck.X, wreck.Y, wreck.Z)),
                    wreck.ExpiresAt
                )
            );
        }
        if (world is Eden.World.Runtime.Economy.EconomyTransaction)
        {
            var type = catalog.RequireItem(JetcanRules.TypeId);
            foreach (var container in world.Filter<Eden.Data.World.Structures.SavedStructureLoot>(c => c.SystemId == system))
            {
                contacts.Add(
                    new(
                        new(container.Id, type.Id, type.GroupId, container.Name, new(container.X, container.Y, container.Z)),
                        container.ExpiresAt
                    )
                );
            }
        }
        if (jetcans)
        {
            foreach (var can in world.Filter<SavedJetcan>(c => c.SystemId == system))
            {
                var type = catalog.RequireItem(JetcanRules.TypeId);
                contacts.Add(new(new(can.Id, type.Id, type.GroupId, type.Name, new(can.X, can.Y, can.Z)), can.ExpiresAt));
            }
        }
        foreach (var drone in world.Filter<WorldDrone>(d => d.SystemId == system))
        {
            if (
                world.Set<SavedCapsuleerSuspension>().Find(drone.PilotId) is not null
                || world.Set<OwnedItem>().Find(drone.ItemId) is not { } item
            )
            {
                continue;
            }
            var type = catalog.RequireItem(item.TypeId);
            contacts.Add(new(new(drone.ItemId, type.Id, type.GroupId, type.Name, new(drone.X, drone.Y, drone.Z))));
        }
        foreach (var npc in world.Filter<WorldNpc>(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null))
        {
            if (NpcPopulationRules.IsMining(npc.Role))
            {
                var type = catalog.RequireItem(npc.TypeId);
                contacts.Add(new(new(npc.Id, type.Id, type.GroupId, type.Name, new(npc.X, npc.Y, npc.Z))));
            }
        }
        foreach (var celestial in catalog.CelestialsInSystem(system))
        {
            // The pinned map projection includes stargates. Beacons are deliberately
            // absent from d-scan, even when they are visible on the overview.
            if (celestial.GroupId == (int)CelestialGroup.Beacon)
            {
                continue;
            }
            contacts.Add(
                new(
                    new(
                        celestial.Id,
                        celestial.TypeId,
                        celestial.GroupId,
                        catalog.RequireItem(celestial.TypeId).Name,
                        new(celestial.X, celestial.Y, celestial.Z)
                    )
                )
            );
        }
        foreach (var station in catalog.StationsInSystem(system))
        {
            contacts.Add(
                new(
                    new(
                        station.Id,
                        station.TypeId,
                        catalog.RequireItem(station.TypeId).GroupId,
                        catalog.StationName(station.Id),
                        new(station.X, station.Y, station.Z)
                    )
                )
            );
        }
        return Array.AsReadOnly(contacts.ToArray());
    }

    private enum CelestialGroup
    {
        Beacon = 310,
    }
}
