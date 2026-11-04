using Eden.Gameplay.Insurance;
using Eden.Gameplay.Market;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Insurance;

internal sealed record InsuranceContractReply(InsurancePolicy Policy) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["contractID"] = Policy.Id,
                ["shipID"] = Policy.ShipId,
                ["ownerID"] = Policy.OwnerId,
                ["typeID"] = Policy.TypeId,
                ["fraction"] = (int)Policy.Level / 100d,
                ["startDate"] = Policy.StartTime,
                ["endDate"] = Policy.EndTime,
            }
        );
}

internal sealed record InsurancePricesReply(IReadOnlyDictionary<int, long> Prices) : BlueModel
{
    public override object ToBlue() =>
        Prices.ToDictionary(price => (long)price.Key, price => (object?)((double)price.Value / MarketDefinitions.CentsPerIsk));
}

internal sealed record InsuranceOwnerValues(long OwnerId) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["ownerName"] = OwnerId };
}
