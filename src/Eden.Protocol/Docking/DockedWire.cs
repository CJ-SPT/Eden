using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

internal sealed record EscrowInfo(double Isk = 0, double Items = 0) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["iskEscrow"] = Isk, ["itemsEscrow"] = Items });
}

internal sealed record HomeStationInfo(int Id, int TypeId, int SystemId, bool IsFallback = false) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["id"] = Id,
                ["type_id"] = TypeId,
                ["solar_system_id"] = SystemId,
                ["is_fallback"] = IsFallback,
            }
        );
}

internal sealed record CorporationMemberInfo(long CharacterId, int CorporationId) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = CharacterId,
                ["corporationID"] = CorporationId,
                ["title"] = "",
                ["roles"] = 0,
                ["titleMask"] = 0,
            }
        );
}

internal sealed record StationItemBits(DockedPilot Pilot) : BlueModel
{
    public override object ToBlue() => new object?[] { Pilot.StationOwnerId, Pilot.StationId, Pilot.OperationId, Pilot.StationTypeId };
}

internal sealed record StationGuest(long CharacterId, int CorporationId) : BlueModel
{
    public override object ToBlue() => new object?[] { CharacterId, CorporationId, null, null };
}

// Both native station events take one four-field guest tuple, including departure.
internal sealed record StationGuestArrived(StationGuest Guest) : PilotNotification("OnCharNowInStation")
{
    public override object?[] Arguments => [Guest];
}

internal sealed record StationGuestDeparted(StationGuest Guest) : PilotNotification("OnCharNoLongerInStation")
{
    public override object?[] Arguments => [Guest];
}

internal sealed record LocationRow(long Id, string Name, int SystemId, double X, double Y, double Z) : BlueModel
{
    public override object ToBlue() => new object?[] { Id, Name, SystemId, X, Y, Z, null };
}

internal sealed record LocationTable(IReadOnlyList<LocationRow> Locations) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new object?[] { "locationID", "locationName", "solarSystemID", "x", "y", "z", "locationNameID" },
            new BlueList<LocationRow>(Locations),
        };
}

internal sealed record CompatibilityRowset(IReadOnlyList<string> Header, IReadOnlyList<IBlueWritable> Rows) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "eve.common.script.sys.rowset.Rowset",
            new Dictionary<string, object?> { ["header"] = new BlueList<string>(Header), ["lines"] = new BlueList<IBlueWritable>(Rows) }
        );
}

internal sealed record EveOwnerRow(long Id, string Name) : BlueModel
{
    private const int CharacterTypeId = 1373;

    public override object ToBlue() => new object?[] { Id, Name, CharacterTypeId };
}

internal sealed record StationInfoRow(DockedPilot Pilot) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Pilot.StationId,
            Pilot.StationTypeId,
            Pilot.Station.Name,
            Pilot.HomeSystemId,
            Pilot.HomeConstellationId,
            Pilot.HomeRegionId,
            Pilot.StationOwnerId,
            Pilot.StationOwnerId,
            Pilot.OperationId,
            Pilot.OrbitId,
            Pilot.X,
            Pilot.Y,
            Pilot.Z,
            0,
        };
}
