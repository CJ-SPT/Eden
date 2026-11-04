using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

internal static class ContractRequestWire
{
    private static readonly string[] s_fields =
    [
        "contractType",
        "isPrivate",
        "assignedToID",
        "minutesExpire",
        "numDays",
        "startStationID",
        "destinationID",
        "price",
        "reward",
        "collateral",
        "title",
        "description",
        "itemList",
        "startStationDivision",
        "requestItemTypeList",
        "forCorp",
        "multiContract",
    ];

    internal static ContractDraft Draft(object? value)
    {
        if (
            value is not BlueNewObject { Name: BlueNewObject.ContractClass, State: Dictionary<string, object?> fields }
            || fields.Count != s_fields.Length
            || s_fields.Any(name => !fields.ContainsKey(name))
        )
        {
            throw new InvalidDataException("Expected a complete native contract request.");
        }
        var type = (ContractType)Number(fields["contractType"]);
        if (
            type is not (ContractType.ItemExchange or ContractType.Courier)
            || Flag(fields["forCorp"])
            || Flag(fields["multiContract"])
            || Id(fields["startStationDivision"]) != (int)InventoryFlag.Hangar
        )
        {
            throw new InvalidDataException("Choose a personal item-exchange or courier contract in a station hangar.");
        }
        int destination = Number(fields["destinationID"]);
        int days = Number(fields["numDays"]);
        long collateral = Amount(fields["collateral"]);
        if (type == ContractType.ItemExchange && (destination != 0 || days != 0 || collateral != 0))
        {
            throw new InvalidDataException("Item exchanges do not have courier delivery terms.");
        }
        long assignee = Id(fields["assignedToID"]);
        if (Flag(fields["isPrivate"]) != (assignee > 0) || assignee < 0)
        {
            throw new InvalidDataException("Choose a valid contract assignee.");
        }
        var items = Pairs(fields["itemList"]);
        var requested = Pairs(fields["requestItemTypeList"]);
        return new(
            assignee == 0 ? null : assignee,
            Number(fields["startStationID"]),
            Number(fields["minutesExpire"]),
            Amount(fields["price"]),
            Amount(fields["reward"]),
            Text(fields["title"]),
            Text(fields["description"]),
            items.Select(pair => new ContractOffer(Id(pair[0]), Number(pair[1]))).ToArray(),
            requested.Select(pair => new ContractRequest(Number(pair[0]), Number(pair[1]))).ToArray(),
            type,
            type == ContractType.Courier ? new(destination, days, collateral) : null
        );
    }

    private static IReadOnlyList<List<object?>> Pairs(object? value) =>
        value is List<object?> list && list.Count <= ContractAssetRules.MaximumSelection && list.All(i => i is List<object?> { Count: 2 })
            ? list.Cast<List<object?>>().ToArray()
            : throw new InvalidDataException("Invalid contract item list.");

    internal static long Id(object? value) => value is long number ? number : throw new InvalidDataException("Expected a contract number.");

    internal static int Number(object? value) => checked((int)Id(value));

    internal static bool Flag(object? value) =>
        value switch
        {
            bool flag => flag,
            long flag when flag is 0 or 1 => flag != 0,
            _ => throw new InvalidDataException("Invalid contract flag."),
        };

    internal static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Invalid contract text.");

    internal static long Amount(object? value) =>
        value switch
        {
            long amount => ContractRules.Cents(amount),
            double amount => ContractRules.Cents(amount),
            _ => throw new InvalidDataException("Invalid contract amount."),
        };
}
