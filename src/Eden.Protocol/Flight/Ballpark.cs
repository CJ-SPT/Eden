using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Mining;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal static class Ballpark
{
    internal static BlueInstance Snapshot(ShipContact own, int stamp, IReadOnlyList<StaticSpaceObject> objects, params ShipContact[] peers)
        => Snapshot(own, stamp, objects, peers, []);

    internal static BlueInstance Snapshot(ShipContact own, int stamp, IReadOnlyList<StaticSpaceObject> objects, ShipContact[] peers, IReadOnlyList<NpcContact> npcs)
    {
        var pilot = own.Pilot;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE
        writer.Write(stamp);
        List<object?> items = [];
        foreach (var body in objects)
        {
            items.Add(WriteObject(writer, body));
        }
        items.Add(WriteShip(writer, own, stamp));
        foreach (var peer in peers)
        {
            items.Add(WriteShip(writer, peer, stamp));
        }
        foreach (var npc in npcs) { items.Add(WriteNpc(writer, npc)); }
        var damage = peers.Prepend(own).Where(s => s.Health is not null).Select(s => s.Health!).Concat(npcs.Select(n => n.Health))
            .ToDictionary(s => s.ShipId, s => (object?)s.DamageState());
        return new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["state"] = stream.ToArray(),
            ["ego"] = pilot.ShipId,
            ["slims"] = items,
            ["solItem"] = Slim(Item(pilot.SystemId, 5, 1, 5, 2)),
            ["droneState"] = new List<object?>(),
            ["damageState"] = damage,
            ["effectStates"] = new List<object?>(),
            ["dbuffState"] = new Dictionary<long, object?>(),
            ["industryLevel"] = 0,
            ["researchLevel"] = 0,
        });
    }

    private static BlueInstance WriteObject(BinaryWriter writer, StaticSpaceObject body)
    {
        // Rigid balls have only the formation ID after the common header. Asteroids
        // use local coordinates; global celestials remain selectable system-wide.
        var flags = (body.GroupId == AsteroidDefinitions.BeltGroup ? DestinyBallFlags.None : DestinyBallFlags.Massive) |
            (body.CategoryId == AsteroidDefinitions.Category ? DestinyBallFlags.None : DestinyBallFlags.Global);
        WriteBallHeader(writer, body.Id, (byte)DestinyBallMode.Rigid, body.Radius, body.Position, (byte)flags);
        writer.Write((sbyte)-1);
        var item = Item(body.Id, body.TypeId, body.OwnerId, body.GroupId, body.CategoryId);
        if (body.DestinationGateId.HasValue)
        {
            // StargateSetup indexes these pairs even when there is no local insurgency.
            item["destinationCorruptionStageAndMaximum"] = new object?[] { 0, 5 };
            item["localCorruptionStageAndMaximum"] = new object?[] { 0, 5 };
            item["destinationSuppressionStageAndMaximum"] = new object?[] { 0, 5 };
            item["localSuppressionStageAndMaximum"] = new object?[] { 0, 5 };
            // Menus read the columns by name. JumpTransitionGate unpacks the same row in order.
            item["jumps"] = new BlueInstance("eve.common.script.sys.rowset.Rowset", new Dictionary<string, object?>
            {
                ["header"] = new List<object?> { "toCelestialID", "locationID" },
                ["lines"] = new List<object?> { new object?[] { body.DestinationGateId, body.DestinationSystemId } },
            });
        }
        if (body.CategoryId == 3) { item["corpID"] = body.OwnerId; }
        return Slim(item);
    }

    internal static object?[] AddObjects(IEnumerable<StaticSpaceObject> objects, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE, consumed by AddBalls in partial mode.
        writer.Write(stamp);
        var slims = objects.Select(body => (object?)WriteObject(writer, body)).ToList();
        return [stream.ToArray(), slims, new Dictionary<long, object?>()];
    }

    internal static object?[] AddShip(ShipContact ship, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // AddBalls reads the full-state format in partial mode.
        writer.Write(stamp);
        var slim = WriteShip(writer, ship, stamp);
        var damage = new Dictionary<long, object?>();
        if (ship.Health is not null) { damage[ship.Pilot.ShipId] = ship.Health.DamageState(); }
        return [stream.ToArray(), new List<object?> { slim }, damage];
    }

    internal static object?[] AddNpcs(IEnumerable<NpcContact> npcs, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0);
        writer.Write(stamp);
        var contacts = npcs.ToArray();
        var slims = contacts.Select(n => (object?)WriteNpc(writer, n)).ToList();
        return [stream.ToArray(), slims, contacts.ToDictionary(n => n.Id, n => (object?)n.Health.DamageState())];
    }

    private static BlueInstance WriteNpc(BinaryWriter writer, NpcContact npc)
    {
        // Local, free, stationary entity ships use the same free-ball layout as
        // player ships. EntityShip fits its graphics turret from the type catalog.
        WriteBallHeader(writer, npc.Id, (byte)DestinyBallMode.Stop, npc.Radius, npc.Position,
            (byte)(DestinyBallFlags.Free | DestinyBallFlags.Interactive | DestinyBallFlags.Massive));
        writer.Write(npc.Mass);
        writer.Write((byte)0);
        writer.Write(-1L);
        writer.Write(npc.CorporationId);
        writer.Write(-1);
        writer.Write(npc.MaxSpeed);
        WritePosition(writer, SpacePosition.Zero);
        writer.Write(npc.Agility);
        writer.Write(0f);
        writer.Write((sbyte)-1);
        var item = Item(npc.Id, npc.TypeId, npc.CorporationId, npc.GroupId, npc.CategoryId);
        item["name"] = npc.Name;
        item["corpID"] = npc.CorporationId;
        item["charID"] = null;
        item["modules"] = new List<object?>();
        return Slim(item);
    }

    private static BlueInstance WriteShip(BinaryWriter writer, ShipContact ship, int stamp)
    {
        var pilot = ship.Pilot;
        var velocity = ship.Velocity;
        var warpTarget = ship.Warp?.Destination ?? ship.AlignTo;
        var mode = warpTarget is not null ? DestinyBallMode.Warp : ship.FollowRange.HasValue ? DestinyBallMode.Follow : ship.Heading is null ? DestinyBallMode.Stop : DestinyBallMode.Goto;
        // Proper warp temporarily clears the massive flag. Aligning ships still collide.
        var flags = DestinyBallFlags.Free | DestinyBallFlags.Interactive | (ship.Warp is null ? DestinyBallFlags.Massive : DestinyBallFlags.None);
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

        var ibis = Item(pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.ShipGroupId, pilot.ShipCategoryId);
        ibis["name"] = pilot.ShipName;
        ibis["charID"] = pilot.PilotId;
        ibis["corpID"] = pilot.CorpId;
        ibis["modules"] = (ship.Modules ?? []).Select(module => (object?)new object?[] { module.ItemId, module.TypeId, module.FlagId }).ToList();
        return Slim(ibis);
    }

    private static void WritePosition(BinaryWriter writer, SpacePosition position)
    {
        writer.Write(position.X);
        writer.Write(position.Y);
        writer.Write(position.Z);
    }

    private static BlueInstance Slim(Dictionary<string, object?> item) => new("eve.common.script.util.slimItem.SlimItem", item);

    private static Dictionary<string, object?> Item(long id, int type, long owner, int group, int category) => new()
    {
        ["itemID"] = id,
        ["typeID"] = type,
        ["ownerID"] = owner,
        ["groupID"] = group,
        ["categoryID"] = category,
    };

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
