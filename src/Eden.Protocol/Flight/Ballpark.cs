using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Protocol.Combat;

namespace Eden.Protocol.Flight;

internal static partial class Ballpark
{
    internal static BallparkSnapshot Snapshot(
        ShipContact own,
        int stamp,
        IReadOnlyList<StaticSpaceObject> objects,
        params ShipContact[] peers
    ) => Snapshot(own, stamp, objects, peers, []);

    internal static BallparkSnapshot Snapshot(
        ShipContact own,
        int stamp,
        IReadOnlyList<StaticSpaceObject> objects,
        ShipContact[] peers,
        IReadOnlyList<NpcContact> npcs,
        IReadOnlyList<WreckContact>? wrecks = null,
        IReadOnlyList<MissileContact>? missiles = null,
        IReadOnlyList<DroneContact>? drones = null
    )
    {
        var pilot = own.Pilot;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE
        writer.Write(stamp);
        List<SlimItem> items = [];
        foreach (var body in objects)
        {
            items.Add(WriteObject(writer, body));
        }
        items.Add(WriteShip(writer, own, stamp));
        foreach (var peer in peers)
        {
            items.Add(WriteShip(writer, peer, stamp));
        }
        foreach (var npc in npcs)
        {
            items.Add(WriteNpc(writer, npc, stamp));
        }
        foreach (var wreck in wrecks ?? [])
        {
            items.Add(WriteWreck(writer, wreck));
        }
        foreach (var missile in missiles ?? [])
        {
            items.Add(WriteMissile(writer, missile));
        }
        foreach (var drone in drones ?? [])
        {
            items.Add(WriteDrone(writer, drone));
        }
        var damage = peers
            .Prepend(own)
            .Where(s => s.Health is not null)
            .Select(s => s.Health!)
            .Concat(npcs.Select(n => n.Health))
            .Concat((drones ?? []).Select(d => d.Health))
            .ToDictionary(s => s.ShipId, s => s.DamageState());
        return new(
            stream.ToArray(),
            pilot.ShipId,
            items,
            new SolarSystemSlim(pilot.SystemId),
            damage,
            drones?.Where(d => d.ShipId != 0).Select(d => new Eden.Protocol.Drones.DroneState(d)).ToArray()
        );
    }

    private static SlimItem WriteObject(BinaryWriter writer, StaticSpaceObject body)
    {
        // Rigid balls have only the formation ID after the common header.
        // Room scenery and asteroids stay local; celestials are system-wide.
        var flags =
            (body.GroupId == AsteroidDefinitions.BeltGroup || !body.Solid ? DestinyBallFlags.None : DestinyBallFlags.Massive)
            | (body.CategoryId == AsteroidDefinitions.Category || !body.IsGlobal ? DestinyBallFlags.None : DestinyBallFlags.Global);
        WriteBallHeader(writer, body.Id, (byte)NavigationMode.Rigid, body.Radius, body.Position, (byte)flags);
        writer.Write((sbyte)-1);
        return new StaticSlimItem(body);
    }

    internal static BallparkAddition AddObjects(IEnumerable<StaticSpaceObject> objects, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE, consumed by AddBalls in partial mode.
        writer.Write(stamp);
        var slims = objects.Select(body => WriteObject(writer, body)).ToList();
        return new(stream.ToArray(), slims, new Dictionary<long, ShipDamageState>());
    }

    internal static BallparkAddition AddShip(ShipContact ship, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // AddBalls reads the full-state format in partial mode.
        writer.Write(stamp);
        var slim = WriteShip(writer, ship, stamp);
        var damage = new Dictionary<long, ShipDamageState>();
        if (ship.Health is not null)
        {
            damage[ship.Pilot.ShipId] = ship.Health.DamageState();
        }
        return new(stream.ToArray(), [slim], damage);
    }

    internal static BallparkAddition AddNpcs(IEnumerable<NpcContact> npcs, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0);
        writer.Write(stamp);
        var contacts = npcs.ToArray();
        var slims = contacts.Select(n => WriteNpc(writer, n, stamp)).ToList();
        return new(stream.ToArray(), slims, contacts.ToDictionary(n => n.Id, n => n.Health.DamageState()));
    }

    internal static BallparkAddition AddWrecks(IEnumerable<WreckContact> wrecks, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0);
        writer.Write(stamp);
        var slims = wrecks.Select(w => WriteWreck(writer, w)).ToList();
        return new(stream.ToArray(), slims, new Dictionary<long, ShipDamageState>());
    }

    internal static BallparkAddition AddMissiles(IEnumerable<MissileContact> missiles, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0);
        writer.Write(stamp);
        var slims = missiles.Select(missile => WriteMissile(writer, missile)).ToList();
        return new(stream.ToArray(), slims, new Dictionary<long, ShipDamageState>());
    }

    private static SlimItem WriteMissile(BinaryWriter writer, MissileContact missile)
    {
        var flight = missile.Flight;
        var drive = flight.Drive;
        MissileFlightRules.Validate(flight);
        float followRange = -(drive.Radius + missile.TargetRadius);
        if (!float.IsFinite(missile.TargetRadius) || missile.TargetRadius < 0 || !float.IsFinite(followRange))
        {
            throw new InvalidDataException("Invalid missile follow range.");
        }
        if (flight.Status != MissileFlightStatus.Flying)
        {
            throw new InvalidDataException("A completed missile cannot enter the ballpark.");
        }
        WriteBallHeader(
            writer,
            missile.Id,
            (byte)(flight.Orphaned ? NavigationMode.Goto : NavigationMode.Missile),
            drive.Radius,
            flight.Position,
            (byte)DestinyBallFlags.Free
        );
        writer.Write(drive.Mass);
        writer.Write((byte)0);
        writer.Write(-1L);
        writer.Write(missile.CorporationId);
        writer.Write(-1);
        writer.Write(drive.MaxVelocity);
        WritePosition(writer, flight.Velocity);
        writer.Write(drive.Agility);
        writer.Write(1f);
        writer.Write((sbyte)-1);
        if (!flight.Orphaned)
        {
            // Carbon Thunkers.cpp mode 5: follow ID/range, launching ball ID,
            // effect stamp, and the current absolute guidance point.
            writer.Write(missile.TargetId);
            writer.Write(followRange);
            writer.Write(missile.SourceShipId);
            writer.Write(missile.LaunchStamp);
        }
        WritePosition(writer, flight.Goto);
        return new MissileSlimItem(missile);
    }

    private static SlimItem WriteWreck(BinaryWriter writer, WreckContact wreck)
    {
        WriteBallHeader(
            writer,
            wreck.Id,
            (byte)NavigationMode.Stop,
            wreck.Radius,
            wreck.Position,
            (byte)(DestinyBallFlags.Free | DestinyBallFlags.Interactive)
        );
        writer.Write(wreck.Mass);
        writer.Write((byte)0);
        writer.Write(-1L);
        writer.Write(wreck.CorporationId);
        writer.Write(-1);
        writer.Write(0f);
        WritePosition(writer, SpacePosition.Zero);
        writer.Write(WreckDefinitions.StationaryAgility);
        writer.Write(0f);
        writer.Write((sbyte)-1);
        return WreckSlim(wreck);
    }

    internal static WreckSlimItem WreckSlim(WreckContact wreck) => new(wreck);

    private static SlimItem WriteNpc(BinaryWriter writer, NpcContact npc, int stamp)
    {
        if (npc.Role == NpcRole.MissionStructure)
        {
            // Carbon Thunkers.cpp omits the free-ball fields for rigid mode. Structures
            // retain their entity slim and damage state while remaining fixed local balls.
            WriteBallHeader(writer, npc.Id, (byte)NavigationMode.Rigid, npc.Radius, npc.Position, (byte)DestinyBallFlags.Massive);
            writer.Write((sbyte)-1);
            return new NpcSlimItem(npc);
        }
        // Entity ships share the free-ball movement layout with player ships.
        // EntityShip fits its graphics turret from the type catalog.
        var course = npc.Navigation;
        var mode =
            npc.Warp is not null ? NavigationMode.Warp
            : course is null || course.State == NpcNavigationState.Idle ? NavigationMode.Stop
            : course.State == NpcNavigationState.Orbiting ? NavigationMode.Orbit
            : course.TargetId != 0 ? NavigationMode.Follow
            : NavigationMode.Goto;
        WriteBallHeader(
            writer,
            npc.Id,
            (byte)mode,
            npc.Radius,
            npc.Position,
            (byte)(
                DestinyBallFlags.Free | DestinyBallFlags.Interactive | (npc.Warp is null ? DestinyBallFlags.Massive : DestinyBallFlags.None)
            )
        );
        writer.Write(npc.Mass);
        writer.Write((byte)0);
        writer.Write(-1L);
        writer.Write(npc.CorporationId);
        writer.Write(-1);
        writer.Write(npc.MaxSpeed);
        WritePosition(writer, course?.Velocity ?? SpacePosition.Zero);
        writer.Write(npc.Agility);
        writer.Write((float)(course?.SpeedFraction ?? 0));
        writer.Write((sbyte)-1);
        if (npc.Warp is { } warp)
        {
            WritePosition(writer, warp.Destination);
            // Without shared motion, the contact's warp age is relative to tick zero.
            // A negative native effect stamp means alignment, not an active warp.
            writer.Write(npc.Stamp == 0 ? stamp + warp.StartStamp : warp.StartStamp);
            writer.Write(warp.Distance);
            writer.Write(0d);
            writer.Write((long)warp.WireFactor);
        }
        else if (course?.TargetId is > 0)
        {
            writer.Write(course.TargetId);
            writer.Write((float)course.FollowRange);
        }
        else if (course?.Destination is { } destination)
        {
            WritePosition(writer, destination);
        }
        return new NpcSlimItem(npc);
    }

    private static SlimItem WriteShip(BinaryWriter writer, ShipContact ship, int stamp)
    {
        var pilot = ship.Pilot;
        var velocity = ship.Velocity;
        var warpTarget = ship.Warp?.Destination ?? ship.AlignTo;
        var mode =
            warpTarget is not null ? NavigationMode.Warp
            : ship.FollowRange.HasValue ? ship.FollowMode
            : ship.Heading is null ? NavigationMode.Stop
            : NavigationMode.Goto;
        // Proper warp temporarily clears the massive flag. Aligning ships still collide.
        var flags =
            DestinyBallFlags.Free | DestinyBallFlags.Interactive | (ship.Warp is null ? DestinyBallFlags.Massive : DestinyBallFlags.None);
        WriteBallHeader(writer, pilot.ShipId, (byte)mode, pilot.Flight.ShipRadius, pilot.Position!, (byte)flags);
        writer.Write(pilot.Flight.Mass);
        writer.Write((byte)0); // Uncloaked
        writer.Write(-1L); // Force-field harmonic
        writer.Write(pilot.CorpId);
        writer.Write(-1);
        writer.Write(pilot.Flight.MaxSpeed);
        writer.Write(velocity.X);
        writer.Write(velocity.Y);
        writer.Write(velocity.Z);
        writer.Write(pilot.Flight.Agility);
        writer.Write((float)ship.SpeedFraction);
        writer.Write((sbyte)-1);

        if (warpTarget is not null)
        {
            WritePosition(writer, warpTarget);
            writer.Write(ship.Warp?.StartStamp ?? -(1 + stamp - ship.AlignStamp));
            writer.Write(ship.Warp?.Distance ?? 0d);
            writer.Write(0d); // minRange was already applied when choosing the destination.
            writer.Write((long)(ship.Warp?.WireFactor ?? ship.WarpFactor));
        }
        else if (ship.FollowRange is double range)
        {
            writer.Write((long)ship.FollowTargetId);
            writer.Write((float)range);
        }
        else if (ship.Heading is { } heading)
        {
            WritePosition(writer, pilot.Position! + heading * 1e17);
        }

        return new ShipSlimItem(ship);
    }

    private static void WritePosition(BinaryWriter writer, SpacePosition position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
    }

    private static void WriteBallHeader(BinaryWriter writer, long id, byte mode, float radius, SpacePosition pos, byte flags)
    {
        writer.Write(id);
        writer.Write(mode);
        writer.Write(radius);
        writer.Write(pos.X);
        writer.Write(pos.Y);
        writer.Write(pos.Z);
        writer.Write(flags);
    }
}
