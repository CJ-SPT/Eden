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
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

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
        var contacts =
            live?.Reads?.DirectionalContacts(observer.SystemId, () => LoadDirectional(observer.SystemId))
            ?? LoadDirectional(observer.SystemId);
        live?.Tractors.Advance(now);
        var origin = live?.Combat?.InformationPosition(observer.ShipId) ?? observer.Position;
        return contacts
            .Where(entry =>
                entry.Contact.Id != observer.ShipId
                && (!entry.Ship || new Eden.World.Propulsion.PilotSpatialModuleStore(database).Status(entry.Contact.Id).CloakModuleId == 0)
                && (
                    entry.LoggedOffPilot is { } pilot
                        ? live?.Combat?.DroneLogoffDeadline(pilot, entry.ExpiresAt) ?? entry.ExpiresAt
                        : entry.ExpiresAt
                ) > now
            )
            .Select(entry =>
            {
                var contact = entry.Contact;
                var position = live?.Combat?.DirectionalPosition(contact.Id) ?? live?.Tractors.Position(contact.Id) ?? contact.Position;
                return position == contact.Position ? contact : contact with { Position = position };
            })
            .Where(contact => InformationRules.InCone(origin, contact.Position, direction, angle, range))
            .OrderBy(contact => contact.Id)
            .ToArray();
    }

    private DirectionalObserver LoadObserver(long pilotId)
    {
        using WorldDbContext world = new(database.Path) { ReadsDirectionalPositions = true };
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var position = Position(pilot);
        double maximum = PilotFittingStore
            .ShipAttributes(world, catalog, pilot)
            .GetValueOrDefault(DogmaAttributeId.MaxDirectionalScanRange, InformationRules.MaximumDirectionalRange);
        return new(pilot.ActiveShipId, System(catalog, pilot), position, maximum);
    }

    private IReadOnlyList<DirectionalEntry> LoadDirectional(int system)
    {
        // These are membership/name baselines. Current resident coordinates are overlaid
        // under the world owner above, without projecting combat or tractor state to SQL.
        using WorldDbContext world = new(database.Path) { ReadsDirectionalPositions = true };
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<DirectionalEntry> contacts = [];
        var logoffs = world.Set<SavedLogoff>().AsNoTracking().Where(l => l.SystemId == system).ToDictionary(l => l.ShipId);
        foreach (
            var ship in world
                .Ships.Include(s => s.Position)
                .Where(s => !world.Set<SavedCapsuleerSuspension>().Any(pause => pause.PilotId == s.OwnerId))
                .AsNoTracking()
                .Where(s => s.Position != null && (s.Position.SystemId == null || s.Position.SystemId == system))
        )
        {
            int location = ship.Position!.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
            if (location != system || catalog.FittingAttributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.DirectionalScanImmune) != 0)
            {
                continue;
            }
            var type = catalog.RequireItem(ship.TypeId);
            logoffs.TryGetValue(ship.Id, out var logoff);
            contacts.Add(
                new(
                    new(ship.Id, type.Id, type.GroupId, ship.Name, new(ship.Position.X, ship.Position.Y, ship.Position.Z)),
                    logoff?.ExpiresAt ?? long.MaxValue,
                    logoff?.PilotId,
                    Ship: true
                )
            );
        }
        foreach (var wreck in world.Set<WorldWreck>().AsNoTracking().Where(w => w.SystemId == system))
        {
            contacts.Add(
                new(
                    new(wreck.Id, wreck.TypeId, catalog.RequireItem(wreck.TypeId).GroupId, wreck.Name, new(wreck.X, wreck.Y, wreck.Z)),
                    wreck.ExpiresAt
                )
            );
        }
        foreach (var can in world.Set<SavedJetcan>().AsNoTracking().Where(c => c.SystemId == system))
        {
            var type = catalog.RequireItem(JetcanRules.TypeId);
            contacts.Add(new(new(can.Id, type.Id, type.GroupId, type.Name, new(can.X, can.Y, can.Z)), can.ExpiresAt));
        }
        var drones =
            from drone in world.Set<WorldDrone>().AsNoTracking()
            join item in world.Set<OwnedItem>() on drone.ItemId equals item.Id
            where drone.SystemId == system && !world.Set<SavedCapsuleerSuspension>().Any(pause => pause.PilotId == drone.PilotId)
            select new
            {
                drone.ItemId,
                item.TypeId,
                drone.X,
                drone.Y,
                drone.Z,
            };
        foreach (var drone in drones)
        {
            var type = catalog.RequireItem(drone.TypeId);
            contacts.Add(new(new(drone.ItemId, type.Id, type.GroupId, type.Name, new(drone.X, drone.Y, drone.Z))));
        }
        foreach (
            var npc in world.Set<WorldNpc>().AsNoTracking().Where(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
        )
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
