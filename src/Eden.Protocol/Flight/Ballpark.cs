using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal static class Ballpark
{
    internal const float StationRadius = 100000;
    internal const float ShipRadius = 47;
    internal const double Mass = 1163000;
    internal const float Agility = 4.5f;
    internal const float MaxSpeed = 295;

    internal static BlueInstance Snapshot(DockedPilot pilot, int stamp)
    {
        SpacePosition ship = pilot.Position!;
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((byte)0); // DESTINY_FULLSTATE
        writer.Write(stamp);
        WriteBallHeader(writer, pilot.StationId, 11, StationRadius, new(pilot.X, pilot.Y, pilot.Z), 2 | 4);
        writer.Write((sbyte)-1); // Rigid balls have only a formation ID after the flags.
        WriteBallHeader(writer, pilot.ShipId, 2, ShipRadius, ship, 1 | 4 | 8);
        writer.Write(Mass);
        writer.Write((byte)0); // Uncloaked
        writer.Write(-1L); // Force-field harmonic
        writer.Write(pilot.CorpId);
        writer.Write(-1);
        writer.Write(MaxSpeed);
        writer.Write(0d);
        writer.Write(0d);
        writer.Write(0d);
        writer.Write(Agility); // Agility
        writer.Write(0f); // Stopped on entry or reconnect
        writer.Write((sbyte)-1);

        var station = Item(pilot.StationId, pilot.StationTypeId, pilot.StationOwnerId, 15, 3);
        var ibis = Item(pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.ShipGroupId, pilot.ShipCategoryId);
        ibis["name"] = pilot.ShipName;
        ibis["charID"] = pilot.PilotId;
        ibis["corpID"] = pilot.CorpId;
        ibis["modules"] = new List<object?>();
        station["corpID"] = pilot.StationOwnerId;
        return new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["state"] = stream.ToArray(),
            ["ego"] = pilot.ShipId,
            ["slims"] = new List<object?> { Slim(station), Slim(ibis) },
            ["solItem"] = Slim(Item(pilot.SystemId, 5, 1, 5, 2)),
            ["droneState"] = new List<object?>(),
            ["damageState"] = new Dictionary<long, object?>(),
            ["effectStates"] = new List<object?>(),
            ["dbuffState"] = new Dictionary<long, object?>(),
            ["industryLevel"] = 0,
            ["researchLevel"] = 0,
        });
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
