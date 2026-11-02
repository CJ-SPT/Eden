using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal static class Ballpark
{
    internal static BlueInstance Snapshot(DockedPilot pilot, int stamp, params ShipContact[] peers)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE
        writer.Write(stamp);
        WriteBallHeader(writer, pilot.StationId, 11, pilot.Flight.StationRadius, new(pilot.X, pilot.Y, pilot.Z), 2 | 4);
        writer.Write((sbyte)-1); // Rigid balls have only a formation ID after the flags.
        var station = Item(pilot.StationId, pilot.StationTypeId, pilot.StationOwnerId, pilot.Station.GroupId, pilot.Station.CategoryId);
        station["corpID"] = pilot.StationOwnerId;
        List<object?> items = [Slim(station), WriteShip(writer, pilot, new(0, 0, 0))];
        foreach (var peer in peers)
        {
            items.Add(WriteShip(writer, peer.Pilot, peer.Velocity));
        }
        return new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["state"] = stream.ToArray(),
            ["ego"] = pilot.ShipId,
            ["slims"] = items,
            ["solItem"] = Slim(Item(pilot.SystemId, 5, 1, 5, 2)),
            ["droneState"] = new List<object?>(),
            ["damageState"] = new Dictionary<long, object?>(),
            ["effectStates"] = new List<object?>(),
            ["dbuffState"] = new Dictionary<long, object?>(),
            ["industryLevel"] = 0,
            ["researchLevel"] = 0,
        });
    }

    internal static object?[] AddShip(ShipContact ship, int stamp)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // AddBalls reads the full-state format in partial mode.
        writer.Write(stamp);
        var slim = WriteShip(writer, ship.Pilot, ship.Velocity);
        return [stream.ToArray(), new List<object?> { slim }, new Dictionary<long, object?>()];
    }

    private static BlueInstance WriteShip(BinaryWriter writer, DockedPilot pilot, SpacePosition velocity)
    {
        WriteBallHeader(writer, pilot.ShipId, 2, pilot.Flight.ShipRadius, pilot.Position!, 1 | 4 | 8);
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
        writer.Write(0f); // Stopped on entry or reconnect
        writer.Write((sbyte)-1);

        var ibis = Item(pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.ShipGroupId, pilot.ShipCategoryId);
        ibis["name"] = pilot.ShipName;
        ibis["charID"] = pilot.PilotId;
        ibis["corpID"] = pilot.CorpId;
        ibis["modules"] = new List<object?>();
        return Slim(ibis);
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
