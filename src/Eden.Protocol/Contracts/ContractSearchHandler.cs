using Eden.Gameplay.Contracts;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Contracts;

[Injectable]
internal sealed class ContractSearchHandler(IPilotContracts contracts) : ISelectedPilotCallHandler
{
    private static readonly HashSet<string> s_fields =
    [
        "itemTypes",
        "itemTypeName",
        "itemCategoryID",
        "itemGroupID",
        "contractType",
        "securityClasses",
        "locationID",
        "endLocationID",
        "issuerID",
        "minPrice",
        "maxPrice",
        "minReward",
        "maxReward",
        "minCollateral",
        "maxCollateral",
        "minVolume",
        "maxVolume",
        "excludeTrade",
        "excludeMultiple",
        "excludeNoBuyout",
        "availability",
        "description",
        "searchHint",
        "sortBy",
        "sortDir",
        "startNum",
    ];

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "contractProxy" || method != "SearchContracts")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (args.Length != 0 || keywords?.Keys.Any(k => !s_fields.Contains(k)) == true)
            {
                throw new InvalidDataException("Invalid contract search arguments.");
            }
            var fields = keywords ?? [];
            ContractSearch search = new(
                Numbers(fields.GetValueOrDefault("itemTypes")),
                OptionalText(fields, "itemTypeName"),
                OptionalNumber(fields, "itemCategoryID"),
                OptionalNumber(fields, "itemGroupID"),
                OptionalNumber(fields, "contractType") ?? (int)ContractType.AuctionAndItemExchange,
                Numbers(fields.GetValueOrDefault("securityClasses")),
                OptionalId(fields, "locationID"),
                OptionalId(fields, "endLocationID"),
                OptionalId(fields, "issuerID"),
                Amount(fields, "minPrice"),
                Amount(fields, "maxPrice"),
                Amount(fields, "minReward"),
                Amount(fields, "maxReward"),
                Amount(fields, "minCollateral"),
                Amount(fields, "maxCollateral"),
                Volume(fields, "minVolume"),
                Volume(fields, "maxVolume"),
                Flag(fields, "excludeTrade"),
                Flag(fields, "excludeMultiple"),
                Flag(fields, "excludeNoBuyout"),
                OptionalNumber(fields, "availability") ?? 0,
                OptionalText(fields, "description"),
                OptionalNumber(fields, "searchHint") ?? 0,
                (ContractSort)(OptionalNumber(fields, "sortBy") ?? 0),
                Flag(fields, "sortDir", true),
                OptionalNumber(fields, "startNum") ?? 0
            );
            return new ContractSearchReply(contracts.Search(context.Pilot.PilotId, search));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This contract search number is too large.");
        }
    }

    private static long? OptionalId(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is { } value ? ContractRequestWire.Id(value) : null;

    private static int? OptionalNumber(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is { } value ? ContractRequestWire.Number(value) : null;

    private static string? OptionalText(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is { } value ? ContractRequestWire.Text(value) : null;

    private static long? Amount(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) is { } value ? ContractRequestWire.Amount(value) : null;

    private static bool Flag(Dictionary<string, object?> fields, string key, bool fallback = false) =>
        fields.GetValueOrDefault(key) is { } value ? ContractRequestWire.Flag(value) : fallback;

    private static double? Volume(Dictionary<string, object?> fields, string key) =>
        fields.GetValueOrDefault(key) switch
        {
            null => null,
            long number => number,
            double number => number,
            _ => throw new InvalidDataException("Invalid contract volume."),
        };

    private static IReadOnlyList<int>? Numbers(object? value) =>
        value switch
        {
            null => null,
            List<object?> list when list.Count <= ContractSearchRules.MaximumResults && list.All(i => i is long) => list.Select(
                    ContractRequestWire.Number
                )
                .ToArray(),
            HashSet<long> set when set.Count <= ContractSearchRules.MaximumResults => set.Select(i => checked((int)i)).ToArray(),
            _ => throw new InvalidDataException("Invalid contract search type selection."),
        };
}

internal sealed record ContractSearchReply(ContractSearchResult Result) : BlueModel
{
    public override object ToBlue() =>
        ContractWire.Fields(
            new()
            {
                ["contracts"] = new BlueList<ContractDetailsReply>(Result.Contracts.Select(c => new ContractDetailsReply(c)).ToArray()),
                ["numFound"] = Result.Found,
                ["searchTime"] = Result.Seconds,
                ["maxResults"] = ContractSearchRules.MaximumResults,
            }
        );
}
