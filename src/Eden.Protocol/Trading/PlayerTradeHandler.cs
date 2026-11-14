using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Trading;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Trading;

[Injectable(InjectionType.Scoped)]
internal sealed class PlayerTradeHandler(SharedPlayerTrades trades, IPlayerTradeStore store) : ISelectedPilotCallHandler, IDisposable
{
    internal const string Service = "trademgr";
    private const string BoundPrefix = "@trade:";
    private string? _lastBound;

    public void Advance(PilotCallContext context) => trades.Advance(this, context);

    public void Dispose()
    {
        trades.Remove(this);
        _lastBound = null;
    }

    internal IReadOnlyList<LocationRow> Locations(IReadOnlyCollection<long> ids) => trades.Locations(this, ids);

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bound = service.StartsWith(BoundPrefix, StringComparison.Ordinal);
        if (!bound && service != Service)
        {
            return PilotCallResult.Unsupported;
        }
        Advance(context);
        try
        {
            if (bound && long.TryParse(service.AsSpan(BoundPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
            {
                return BoundCall(context, id, method, args, keywords);
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Invalid trade request.");
            }
            return (method, args) switch
            {
                ("InitiateTrade", [long partner]) => trades.Initiate(this, partner),
                ("MachoResolveObject", [long container]) => Resolve(container),
                ("MachoBindObject", [long container, var first]) => Bind(context, container, first),
                _ => PilotCallResult.Unsupported,
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private int Resolve(long container)
    {
        trades.Require(this, container);
        return PilotLogin.NodeId;
    }

    private PilotCallResult Bind(PilotCallContext context, long id, object? first)
    {
        trades.Require(this, id);
        PilotCallResult result = first switch
        {
            null => PilotCallResult.None,
            object?[] { Length: 2 or 3 } call
                when call[1] is object?[] args && call.ElementAtOrDefault(2) is null or Dictionary<string, object?> => BoundCall(
                context,
                id,
                BlueMarshal.Text(call[0]),
                args,
                (call.ElementAtOrDefault(2) as Dictionary<string, object?>)?.Where(pair => pair.Key != "machoVersion").ToDictionary()
            ),
            _ => throw new InvalidDataException("Invalid first trade call."),
        };
        if (result is not PilotCallResult.Success success)
        {
            return result;
        }
        string service = BoundPrefix + id.ToString(CultureInfo.InvariantCulture);
        if (_lastBound is not null && _lastBound != service)
        {
            context.ReleaseReference(_lastBound);
        }
        _lastBound = service;
        return new BoundPilotReply(context.Reference(service, PilotBindingLifetime.Pilot), success.Reply);
    }

    private PilotCallResult BoundCall(
        PilotCallContext context,
        long id,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (method is "Add" or "MultiAdd")
        {
            return trades.Add(this, id, store, ReadMove(method, args, keywords));
        }
        if (keywords is { Count: > 0 })
        {
            throw new InvalidDataException("Invalid trade arguments.");
        }
        return (method, args) switch
        {
            ("List", []) => trades.Require(this, id),
            ("GetSelfInvItem" or "GetItem", []) => Container(context, id),
            ("GetItem", [long item]) when item == id => Container(context, id),
            ("OfferMoney", [var amount]) => trades.OfferMoney(this, id, TradeManifest.MoneyCents(amount), store),
            ("MakeOffer", [var manifest]) => trades.Accept(this, id, TradeManifest.Read(manifest), store),
            ("Abort", []) => trades.Abort(this, id),
            _ => PilotCallResult.Unsupported,
        };
    }

    private TradeContainer Container(PilotCallContext context, long id)
    {
        trades.Require(this, id);
        var pilot = context.Pilot;
        return new(id, pilot.DockedLocationId, pilot.StationTypeId, pilot.PilotId);
    }

    private static InventoryMoveRequest ReadMove(string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (
            args is not [var items, long source]
            || source <= 0
            || keywords?.Keys.Any(key => key is not ("flag" or "qty")) == true
            || keywords?.GetValueOrDefault("flag") is not (null or (long)InventoryFlag.None or (long)InventoryFlag.Hangar)
        )
        {
            throw new InvalidDataException("Choose items and a valid inventory source for the trade.");
        }
        int? quantity = keywords?.GetValueOrDefault("qty") switch
        {
            null => null,
            long amount when amount is > 0 and <= int.MaxValue => (int)amount,
            _ => throw new InvalidDataException("Choose a positive trade quantity."),
        };
        long[] ids = (method, items) switch
        {
            ("Add", long item) => [item],
            ("MultiAdd", List<object?> list) when list.Count <= PlayerTradeRules.MaximumItemsPerPilot && list.All(i => i is long) =>
                list.Cast<long>().ToArray(),
            _ => throw new InvalidDataException("Choose items to trade."),
        };
        return new(ids, source, (int)InventoryFlag.Hangar, quantity);
    }
}
