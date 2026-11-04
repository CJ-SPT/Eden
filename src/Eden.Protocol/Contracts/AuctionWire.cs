using Eden.Gameplay.Contracts;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

internal sealed record ContractBidRow(ContractBid Bid) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["bidID"] = Bid.Id,
                ["bidderID"] = Bid.BidderId,
                ["amount"] = ContractWire.Isk(Bid.AmountCents),
                ["dateBid"] = Bid.Time,
            }
        );
}
