using Eden.Gameplay.Contracts;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

internal sealed record CourierPackageReply(CourierPackageInfo Package) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contractID"] = Package.ContractId,
                ["itemID"] = Package.PackageId,
                ["acceptorID"] = Package.AcceptorId,
                ["status"] = (int)Package.Status,
            }
        );
}

internal sealed record CourierProgressRow(PersonalContract Contract) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contractID"] = Contract.Id,
                ["startStationID"] = Contract.StationId,
                ["endStationID"] = Contract.Courier!.Destination.StationId,
                ["expires"] = CourierRules.Deadline(Contract.Accepted!.Value, Contract.Courier.DurationDays),
            }
        );
}
