using Eden.Data.Catalog;
using Eden.Data.World.Alliances;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class AutonomousPilotPresence(WorldDatabase database) : IAutonomousPilotPresence
{
    private const int MaximumSamples = 32;
    private const int MaximumSightings = 12;
    private static readonly CatalogDefinition<CatalogItemDefinition?> s_hull = new(
        "autonomous-visible-hull",
        (catalog, type) => catalog.FittingType(type)
    );

    public bool Owns(long shipId) =>
        LiveWorld.Find(database.Path) is { } live
        && live.CapsuleerPilot(shipId) is { } pilot
        && (live.HaulerCheckpoints.ContainsKey(pilot) || live.MinerCheckpoints.ContainsKey(pilot));

    public AutonomousPilotContact? Read(int systemId, long shipId) =>
        LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.EnterShared("autonomous combat contact", new(systems: [systemId]));
                return ReadOwned(systemId, shipId);
            },
            nameof(Read)
        );

    private AutonomousPilotContact? ReadOwned(int system, long ship)
    {
        if (
            !database.Settings.Capsuleers.Enabled
            || LiveWorld.Find(database.Path) is not { } live
            || live.CapsuleerPilot(ship) is not { } pilotId
            || live.Economy.Peek<SavedCapsuleerSuspension>(pilotId) is not null
            || !live.Economy.ActiveShip(pilotId, ship)
            || live.Economy.Peek<FlightPosition>(ship) is not { } saved
            || saved.SystemId != system
            || live.Economy.Peek<Pilot>(pilotId) is not { } pilot
            || live.Economy.Peek<Ship>(ship) is not { } hull
            || database.Catalog.Definition(s_hull, hull.TypeId) is not { } type
        )
        {
            return null;
        }
        var hauler = live.HaulerCheckpoints.GetValueOrDefault(pilotId)?.Work;
        var miner = live.MinerCheckpoints.GetValueOrDefault(pilotId)?.Work;
        var navigation = hauler?.Navigation ?? miner?.Navigation;
        if (navigation is null)
        {
            return null;
        }
        bool protectedShip =
            navigation.Warp is not null
            || hauler?.Activity is HaulerActivity.Jumping or HaulerActivity.Inbound
            || hauler is not null && hauler.Stamp < hauler.ArrivalUntil;
        var position = live.PublishedPosition(system, ship) ?? new SpacePosition(saved.X, saved.Y, saved.Z);
        int? alliance = live.Economy.Peek<SavedAllianceMember>(pilot.CorporationId)?.AllianceId;
        return new(
            new(
                pilotId,
                ship,
                type.Id,
                (HullClass)type.GroupId,
                pilot.CorporationId,
                alliance,
                position,
                navigation.Velocity,
                (float)(type.Radius ?? 0),
                hauler?.Ratting?.Patrol?.Camp,
                database.Settings.Capsuleers.RegionalConflicts ? RegionalConflictStore.GroupForPilot(live, database.Catalog, pilotId) : null
            ),
            protectedShip
        );
    }

    public PilotSighting[] Observe(int systemId, long ownShipId, SpacePosition origin, long sample) =>
        LiveWorld.Execute(
            database.Path,
            systemId,
            () =>
            {
                using var ownership = LiveWorld.Find(database.Path)?.EnterShared("autonomous sight snapshot", new(systems: [systemId]));
                return ObserveOwned(systemId, ownShipId, origin, sample);
            },
            nameof(Observe)
        );

    private PilotSighting[] ObserveOwned(int system, long own, SpacePosition origin, long sample)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        var pilots = live.CapsuleerParticipants(new(systems: [system])).Order().ToArray();
        if (pilots.Length == 0)
        {
            return [];
        }
        int offset = (int)(TravelerRoutineRules.Mix(unchecked((ulong)(sample + own))) % (ulong)pilots.Length);
        List<PilotSighting> seen = [];
        for (int index = 0; index < Math.Min(pilots.Length, MaximumSamples); index++)
        {
            long pilot = pilots[(offset + index) % pilots.Length];
            long ship =
                live.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.ShipId
                ?? live.MinerCheckpoints.GetValueOrDefault(pilot)?.Work.ShipId
                ?? 0;
            if (
                ship != own
                && ReadOwned(system, ship) is { Protected: false } contact
                && origin.DistanceTo(contact.Sighting.Position) <= Eden.Gameplay.Combat.NpcEncounterDefinitions.VisibilityRange
            )
            {
                seen.Add(contact.Sighting);
            }
        }
        return seen.OrderBy(contact => origin.DistanceTo(contact.Position))
            .ThenBy(contact => contact.ShipId)
            .Take(MaximumSightings)
            .ToArray();
    }
}
