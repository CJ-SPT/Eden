using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Information;

internal static class InformationWire
{
    internal static DogmaEffect? Effect(string name) =>
        name switch
        {
            "shipScan" => DogmaEffect.ShipScan,
            "cargoScan" => DogmaEffect.CargoScan,
            "targetPassively" => DogmaEffect.PassiveTargeting,
            "targetHostiles" => DogmaEffect.AutomaticTargeting,
            "tractorBeamCan" => DogmaEffect.TractorBeam,
            "salvaging" => DogmaEffect.Salvaging,
            _ => null,
        };

    internal static List<object?> Items(IReadOnlyList<ScanItem> items) =>
        items.Select(i => (object?)new object?[] { i.TypeId, i.Quantity }).ToList();
}

internal sealed record ShipScanCompleted(ShipScan Scan) : PilotNotification("OnShipScanCompleted")
{
    public override object?[] Arguments =>
        [Scan.ShipId, Scan.CapacitorCharge, Scan.CapacitorCapacity, InformationWire.Items(Scan.Hardware)];
}

internal sealed record CargoScanCompleted(CargoScan Scan) : PilotNotification("OnCargoScanComplete")
{
    public override object?[] Arguments => [Scan.TargetId, InformationWire.Items(Scan.Cargo)];
}

internal sealed record MiningSurveyReply(IReadOnlyList<MiningScanResult> Results) : BlueModel
{
    public override object ToBlue() => Results.Select(r => (object?)new object?[] { r.AsteroidId, r.TypeId, r.Quantity }).ToList();
}

internal sealed record DirectionalScanReply(IReadOnlyList<DirectionalScanContact> Contacts) : BlueModel
{
    public override object ToBlue() =>
        Contacts
            .Select(c =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["id"] = c.Id,
                            ["typeID"] = c.TypeId,
                            ["groupID"] = c.GroupId,
                        }
                    )
            )
            .ToList();
}
