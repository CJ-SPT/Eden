using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Trading;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Trading;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedPlayerTrades
{
    // Runtime-only IDs in a separate range from persisted locations and item identities.
    private const long FirstTradeId = 25_000_000_000_000;

    private sealed class Presence(DockedPilot pilot)
    {
        internal DockedPilot Pilot { get; set; } = pilot;
        internal Trade? Trade { get; set; }
        internal List<PilotNotification> Notifications { get; } = [];
        internal List<InventoryChange> Inventory { get; } = [];
    }

    private sealed record OfferedItem(TradeItemOffer Offer, InventoryRow Row);

    private sealed class Trader(Presence presence)
    {
        internal Presence Presence { get; } = presence;
        internal long PilotId { get; } = presence.Pilot.PilotId;
        internal List<OfferedItem> Items { get; } = [];
        internal long Money { get; set; }
        internal TradeManifest? Acceptance { get; set; }

        internal TradeSide Side() => new(PilotId, Money, Items.Select(item => item.Offer).ToArray());
    }

    private sealed class Trade(long id, Presence initiator, Presence partner)
    {
        internal long Id { get; } = id;
        internal long StationId { get; } = initiator.Pilot.DockedLocationId;
        internal Trader Initiator { get; } = new(initiator);
        internal Trader Partner { get; } = new(partner);
        internal TradeAmounts Amounts => new(Initiator.Money, Partner.Money);
        internal TradeAcceptances State => new(Initiator.Acceptance, Partner.Acceptance);
        internal IEnumerable<OfferedItem> Offers => Initiator.Items.Concat(Partner.Items);
        internal TradeItems Items => new(Offers.Select(item => item.Row).ToArray());

        internal TradeList List() => new(Id, Initiator.PilotId, Partner.PilotId, State, Amounts, Items);

        internal Trader For(Presence pilot) => ReferenceEquals(Initiator.Presence, pilot) ? Initiator : Partner;

        internal void Notify(PilotNotification notification)
        {
            Initiator.Presence.Notifications.Add(notification);
            Partner.Presence.Notifications.Add(notification);
        }

        internal void Reset()
        {
            Initiator.Acceptance = null;
            Partner.Acceptance = null;
            Notify(new TradeOfferChanged(Id, State));
        }

        internal void Close(PilotNotification notification)
        {
            Notify(notification);
            Initiator.Presence.Trade = null;
            Partner.Presence.Trade = null;
        }
    }

    private readonly object _gate = new();
    private readonly Dictionary<PlayerTradeHandler, Presence> _pilots = [];
    private readonly Dictionary<long, Presence> _byPilotId = [];
    private long _nextId = FirstTradeId;

    internal void Advance(PlayerTradeHandler connection, PilotCallContext context)
    {
        lock (_gate)
        {
            var pilot = context.ActiveFlight?.Pilot;
            if (_pilots.TryGetValue(connection, out var presence))
            {
                if (presence.Pilot.PilotId == pilot?.PilotId)
                {
                    if (pilot.Position is not null || presence.Pilot.DockedLocationId != pilot.DockedLocationId)
                    {
                        Cancel(presence);
                    }
                    foreach (var change in presence.Inventory)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    presence.Inventory.Clear();
                    context.Notifications.AddRange(presence.Notifications);
                    presence.Notifications.Clear();
                }
                if (pilot is { Position: null } && presence.Pilot.PilotId == pilot.PilotId)
                {
                    presence.Pilot = pilot;
                    return;
                }
                RemoveCore(connection);
            }
            if (pilot is { Position: null } && !_byPilotId.ContainsKey(pilot.PilotId))
            {
                var added = new Presence(pilot);
                _pilots.Add(connection, added);
                _byPilotId.Add(pilot.PilotId, added);
            }
        }
    }

    internal void Remove(PlayerTradeHandler connection)
    {
        lock (_gate)
        {
            RemoveCore(connection);
        }
    }

    private void RemoveCore(PlayerTradeHandler connection)
    {
        if (_pilots.Remove(connection, out var presence))
        {
            Cancel(presence);
            _byPilotId.Remove(presence.Pilot.PilotId);
        }
    }

    private static void Cancel(Presence presence)
    {
        if (presence.Trade is { } trade)
        {
            // The client may have yet to receive the initial notification.
            trade.Initiator.Presence.Notifications.RemoveAll(n => n is TradeInitiated started && started.Session.ContainerId == trade.Id);
            trade.Partner.Presence.Notifications.RemoveAll(n => n is TradeInitiated started && started.Session.ContainerId == trade.Id);
            trade.Close(new TradeCancelled(trade.Id));
        }
    }

    internal TradeMoniker Initiate(PlayerTradeHandler connection, long partnerId)
    {
        lock (_gate)
        {
            if (
                !_pilots.TryGetValue(connection, out var actor)
                || !_byPilotId.TryGetValue(partnerId, out var partner)
                || ReferenceEquals(actor, partner)
                || actor.Pilot.DockedLocationId != partner.Pilot.DockedLocationId
            )
            {
                throw new InvalidDataException("Choose another connected pilot docked at your location.");
            }
            if (actor.Trade is { } current && ReferenceEquals(current, partner.Trade))
            {
                return new(current.Id);
            }
            if (actor.Trade is not null || partner.Trade is not null)
            {
                throw new InvalidDataException("One pilot is already trading.");
            }

            Trade trade = new(++_nextId, actor, partner);
            actor.Trade = partner.Trade = trade;
            TradeMoniker moniker = new(trade.Id);
            partner.Notifications.Add(new TradeInitiated(actor.Pilot.PilotId, moniker));
            return moniker;
        }
    }

    internal TradeList Require(PlayerTradeHandler connection, long id)
    {
        lock (_gate)
        {
            return Find(connection, id).List();
        }
    }

    private Trade Find(PlayerTradeHandler connection, long id) =>
        _pilots.GetValueOrDefault(connection)?.Trade is { } trade && trade.Id == id
            ? trade
            : throw new InvalidDataException("This trade session has ended.");

    internal bool Add(PlayerTradeHandler connection, long id, IPlayerTradeStore store, InventoryMoveRequest request)
    {
        lock (_gate)
        {
            var trade = Find(connection, id);
            var trader = trade.For(_pilots[connection]);
            if (trader.Items.Count + request.ItemIds.Count > PlayerTradeRules.MaximumItemsPerPilot)
            {
                throw new InvalidDataException("The trade window has reached its item limit.");
            }
            var quotes = store.Quote(trader.PilotId, trade.StationId, request);
            var heldIds = trader
                .Items.SelectMany(item => item.Offer.Asset.Contents.Append(item.Offer.Asset.Root))
                .Select(i => i.Id)
                .ToHashSet();
            if (quotes.SelectMany(offer => offer.Asset.Contents.Append(offer.Asset.Root)).Any(item => !heldIds.Add(item.Id)))
            {
                throw new InvalidDataException("A selected item is already included in the trade.");
            }

            var staged = quotes.Select(offer => new OfferedItem(offer, DisplayRow(trade, offer))).ToArray();
            trader.Items.AddRange(staged);
            long[] named = staged.Where(item => item.Offer.Asset.ShipName is not null).Select(item => item.Row.Id).ToArray();
            if (named.Length > 0)
            {
                trade.Notify(new TradeLocationsNeeded(named));
            }
            foreach (var item in staged)
            {
                // Offers have virtual rows. The authoritative assets remain in the owner's inventory until settlement.
                trade.Notify(new ItemChanged(item.Row, new Dictionary<InventoryColumn, long> { [InventoryColumn.Location] = 0 }));
            }
            trade.Reset();
            return true;
        }
    }

    private InventoryRow DisplayRow(Trade trade, TradeItemOffer offer) =>
        InventoryRow.From(
            offer.Asset.Root with
            {
                Id = ++_nextId,
                LocationId = trade.Id,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = offer.Quantity,
            }
        );

    internal bool OfferMoney(PlayerTradeHandler connection, long id, long amount, IPlayerTradeStore store)
    {
        lock (_gate)
        {
            var trade = Find(connection, id);
            var trader = trade.For(_pilots[connection]);
            if (amount > store.Balance(trader.PilotId))
            {
                throw new InvalidDataException("You do not have enough ISK to offer that amount.");
            }
            if (trader.Money != amount)
            {
                trader.Money = amount;
                trade.Notify(new TradeMoneyChanged(id, trade.Amounts));
                trade.Reset();
            }
            return true;
        }
    }

    internal bool Accept(PlayerTradeHandler connection, long id, TradeManifest manifest, IPlayerTradeStore store)
    {
        lock (_gate)
        {
            var trade = Find(connection, id);
            var presence = _pilots[connection];
            var items = trade.Items;
            if (
                manifest.ContainerId != id
                || manifest.Money != trade.Amounts
                || manifest.Items.Rows.Count != items.Rows.Count
                || !items.Rows.ToHashSet().SetEquals(manifest.Items.Rows)
            )
            {
                presence.Notifications.Add(new TradeOfferReset(id));
                presence.Notifications.Add(new TradeMoneyChanged(id, trade.Amounts));
                trade.Reset();
                return false;
            }

            trade.For(presence).Acceptance = new(id, trade.Amounts, items);
            if (trade.Initiator.Acceptance is null || trade.Partner.Acceptance is null)
            {
                trade.Notify(new TradeOfferChanged(id, trade.State));
                return true;
            }

            TradeSettlement result;
            bool settled = false;
            try
            {
                result = store.Settle(trade.StationId, trade.Initiator.Side(), trade.Partner.Side());
                settled = true;
            }
            finally
            {
                if (!settled)
                {
                    trade.Reset();
                }
            }

            trade.Initiator.Presence.Inventory.AddRange(result.InitiatorChanges);
            trade.Partner.Presence.Inventory.AddRange(result.PartnerChanges);
            trade.Close(new TradeCompleted(id, items));
            return true;
        }
    }

    internal bool Abort(PlayerTradeHandler connection, long id)
    {
        lock (_gate)
        {
            if (_pilots.GetValueOrDefault(connection) is { } pilot && pilot.Trade?.Id == id)
            {
                Cancel(pilot);
            }
            // Closing the window can issue Abort after a completion or remote cancellation.
            return true;
        }
    }

    internal IReadOnlyList<LocationRow> Locations(PlayerTradeHandler connection, IReadOnlyCollection<long> ids)
    {
        lock (_gate)
        {
            if (_pilots.GetValueOrDefault(connection)?.Trade is not { } trade)
            {
                return [];
            }
            var pilot = _pilots[connection].Pilot;
            return trade
                .Offers.Where(item => ids.Contains(item.Row.Id) && item.Offer.Asset.ShipName is not null)
                .Select(item => new LocationRow(item.Row.Id, item.Offer.Asset.ShipName!, pilot.SystemId, pilot.X, pilot.Y, pilot.Z))
                .ToArray();
        }
    }
}
