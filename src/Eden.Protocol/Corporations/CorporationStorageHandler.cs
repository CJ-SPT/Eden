using System.Globalization;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationStorageHandler(ICorporationStorage storage) : ISelectedPilotCallHandler
{
    internal const string InventoryPrefix = "@corporationOffice:";
    internal const int OfficeTypeId = 27;
    internal const int OfficeGroupId = 16;
    internal const int OfficeCategoryId = 1;
    internal const int OfficeFolderFlag = 2;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool inventory = service.StartsWith(InventoryPrefix, StringComparison.Ordinal);
        bool bindOffice =
            service == "invbroker"
            && method == "GetInventoryFromId"
            && args is [long id, ..]
            && id is >= CorporationStorageRules.FirstOfficeId and <= CorporationStorageRules.LastOfficeId;
        if (service != "officeManager" && !inventory && !bindOffice)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long actor = context.Pilot.PilotId;
            if (bindOffice)
            {
                if (
                    args.Length is < 1 or > 2
                    || args.Length == 2 && args[1] is not (null or bool or 0L or 1L)
                    || keywords is { Count: > 0 }
                )
                {
                    throw new InvalidDataException("Invalid office inventory binding.");
                }
                var office = storage.Office(actor, (long)args[0]!);
                return context.Reference($"{InventoryPrefix}{office.Id}", PilotBindingLifetime.Location);
            }
            if (inventory)
            {
                if (
                    !long.TryParse(
                        service.AsSpan(InventoryPrefix.Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out long officeId
                    )
                )
                {
                    throw new InvalidDataException("Invalid corporation office reference.");
                }
                var office = storage.Office(actor, officeId);
                if (method is "Add" or "MultiAdd")
                {
                    return Move(context, officeId, method, args, keywords);
                }
                if (
                    method == "ListByFlags"
                    && args.Length == 0
                    && keywords is { Count: 1 }
                    && keywords.GetValueOrDefault("flags") is List<object?> flags
                    && flags.All(f =>
                        f is long value && value is >= CorporationStorageRules.FirstFlag and <= CorporationStorageRules.LastFlag
                    )
                )
                {
                    var items = flags.Cast<long>().Distinct().SelectMany(flag => storage.List(actor, officeId, (int)flag));
                    return new BlueList<InventoryRow>(items.Select(InventoryRow.From).ToArray());
                }
                if (
                    method is "List" or "StackAll"
                    && args.Length == 0
                    && keywords is { Count: 1 }
                    && keywords.TryGetValue("flag", out var flagValue)
                )
                {
                    args = [flagValue];
                    keywords = null;
                }
                // invCacheContainer.List always forwards flag=None for an unfiltered read.
                if (method == "List" && args is [null])
                {
                    args = [];
                }
                if (keywords is { Count: > 0 } || args.Length > 1 || args.Length == 1 && args[0] is not long)
                {
                    throw new InvalidDataException("Invalid corporation inventory arguments.");
                }
                int? selectedFlag = args is [long flag] && flag is >= int.MinValue and <= int.MaxValue ? (int)flag : null;
                if (args.Length == 1 && selectedFlag is null)
                {
                    throw new InvalidDataException("Invalid corporation hangar division.");
                }
                switch (method)
                {
                    case "List":
                        return new BlueList<InventoryRow>(storage.List(actor, officeId, selectedFlag).Select(InventoryRow.From).ToArray());
                    case "GetSelfInvItem" when args.Length == 0:
                        return new InventoryRow(
                            office.Id,
                            OfficeTypeId,
                            office.CorporationId,
                            office.StationId,
                            OfficeFolderFlag,
                            -1,
                            OfficeGroupId,
                            OfficeCategoryId
                        );
                    case "StackAll" when selectedFlag.HasValue:
                        NotifyItems(context, storage.Stack(actor, officeId, selectedFlag.Value));
                        return PilotCallResult.None;
                    default:
                        throw new InvalidDataException("Unsupported corporation inventory request.");
                }
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected office arguments.");
            }
            switch (method, args)
            {
                case ("GetMyCorporationsOffices", []):
                    return new BlueList<CorporationOfficeReply>(
                        storage.Offices(actor).Select(o => new CorporationOfficeReply(o)).ToArray()
                    );
                case ("GetCorporationsWithOffices", []):
                    return new BlueList<int>(storage.Tenants(actor));
                case ("GetEmptyOfficeCount", []):
                    return storage.EmptyOffices(actor);
                case ("GetPriceQuote", [long corporation]) when corporation == context.Pilot.CorpId:
                    return storage.Quote(actor) / (double)MarketDefinitions.CentsPerIsk;
                case ("RentOffice", [var price]) when price is long or double:
                    double value = Convert.ToDouble(price, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(value) || value < 0)
                    {
                        throw new InvalidDataException("Invalid office rental quote.");
                    }
                    var rented = storage.Rent(actor, checked((long)decimal.Round((decimal)value * MarketDefinitions.CentsPerIsk)));
                    context.Notifications.Add(new CorporationOfficeRentalChanged(rented.CorporationId, rented.Id));
                    return PilotCallResult.None;
                case ("UnrentOffice", []):
                    var removed = storage.Unrent(actor);
                    context.Notifications.Add(new CorporationOfficeRentalChanged(removed.CorporationId, removed.Id));
                    return PilotCallResult.None;
                case ("PrimeOfficeItem", []):
                    var local = storage.Offices(actor).FirstOrDefault(o => o.StationId == context.Pilot.StationId);
                    if (local is not null)
                    {
                        storage.Office(actor, local.Id);
                    }
                    return PilotCallResult.None;
                case ("HasCorpImpoundedItems", []):
                    storage.EmptyOffices(actor);
                    return false;
                default:
                    throw new InvalidDataException("Invalid office request.");
            }
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The requested amount exceeds the supported limit.");
        }
    }

    private PilotCallResult Move(
        PilotCallContext context,
        long destination,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (
            args is not [var idsValue, long source]
            || keywords?.Keys.Any(k => k is not ("flag" or "qty")) == true
            || keywords?.GetValueOrDefault("flag") is not long flag
            || flag is < CorporationStorageRules.FirstFlag or > CorporationStorageRules.LastFlag
        )
        {
            throw new InvalidDataException("Choose an office destination division and local source items.");
        }
        long[] ids = (method, idsValue) switch
        {
            ("Add", long id) => [id],
            ("MultiAdd", List<object?> list) when list.All(i => i is long) => list.Cast<long>().ToArray(),
            _ => throw new InvalidDataException("Invalid corporation transfer items."),
        };
        object? quantityValue = keywords?.GetValueOrDefault("qty");
        int? quantity = quantityValue switch
        {
            null => null,
            long q when q is > 0 and <= int.MaxValue => (int)q,
            _ => throw new InvalidDataException("Choose a positive transfer quantity."),
        };
        NotifyItems(context, storage.Move(context.Pilot.PilotId, destination, new(ids, source, (int)flag, quantity)));
        return PilotCallResult.None;
    }

    internal static void NotifyItems(PilotCallContext context, IReadOnlyList<InventoryChange> changes)
    {
        foreach (var change in changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
    }
}

internal sealed record CorporationOfficeReply(CorporationOffice Office) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["officeID"] = Office.Id,
                ["corporationID"] = Office.CorporationId,
                ["stationID"] = Office.StationId,
                ["solarsystemID"] = Office.SystemId,
                ["officeFolderID"] = Office.Id,
            }
        );
}

internal sealed record CorporationOfficeRentalChanged(int CorporationId, long OfficeId) : PilotNotification("OnOfficeRentalChange")
{
    public override object?[] Arguments => [CorporationId, OfficeId];
}
