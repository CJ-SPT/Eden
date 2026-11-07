using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Market;

[Injectable(InjectionType.Scoped)]
internal sealed class PersonalMarketMonitor(IPersonalMarket market, IPilotWalletReader wallet)
{
    private long? _pilot;
    private long _cursor;
    private long _revision;
    private bool _more;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _revision = market.Revision;
            _cursor = market.Observe(_pilot.Value);
            _more = false;
        }
    }

    public void Reset() => _pilot = null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long revision = market.Revision;
        if (!_more && _revision == revision)
        {
            return;
        }
        var changes = market.Changes(context.Pilot.PilotId, _cursor);
        foreach (var change in changes)
        {
            foreach (var item in change.Inventory)
            {
                InventoryChangesWire.NotifyItemChange(context, item);
                if (item.Previous is null)
                {
                    context.Notifications.Add(new MarketItemReceived(item.Item));
                }
            }
            context.Notifications.Add(
                new OwnOrdersChanged(
                    [new(change.Order)],
                    change.Kind switch
                    {
                        MarketOrderChangeKind.Placed => "Add",
                        MarketOrderChangeKind.Modified => "Modify",
                        _ => "Expiry",
                    }
                )
            );
            _cursor = change.Id;
        }
        if (changes.Count > 0)
        {
            context.Notifications.Add(
                new AccountBalanceChanged(
                    context.Pilot.PilotId,
                    (double)wallet.Balance(context.Pilot.PilotId) / MarketDefinitions.CentsPerIsk
                )
            );
        }
        _more = changes.Count == PersonalMarketRules.ChangeBatch;
        _revision = revision;
    }
}
