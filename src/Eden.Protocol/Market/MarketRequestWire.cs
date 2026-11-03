using Eden.Protocol.Serialization;

namespace Eden.Protocol.Market;

internal static class MarketRequestWire
{
    internal static Dictionary<string, object?> Fields(object? value) =>
        value is BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields }
            ? fields
            : throw new InvalidDataException("Expected a market item request.");

    internal static long Id(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is long id ? id : throw new InvalidDataException($"Invalid market {key}.");

    internal static int Number(Dictionary<string, object?> fields, string key) => checked((int)Id(fields, key));

    internal static long Price(object? price) =>
        MarketDefinitions.PriceCents(
            price switch
            {
                double number => number,
                long number => number,
                _ => throw new InvalidDataException("Invalid market price."),
            }
        );

    internal static MarketSale Sale(object? value)
    {
        var fields = Fields(value);
        return new(
            Id(fields, "itemID"),
            Number(fields, "stationID"),
            Number(fields, "typeID"),
            Price(fields.GetValueOrDefault("price")),
            Number(fields, "quantity")
        );
    }

    internal static MarketPurchase Purchase(object? value)
    {
        var fields = Fields(value);
        return new(Number(fields, "typeID"), Price(fields.GetValueOrDefault("price")), Number(fields, "quantity"));
    }
}
