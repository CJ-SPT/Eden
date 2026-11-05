using Eden.Gameplay.Market;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Market;

[Injectable(InjectionType.Scoped)]
internal sealed class WalletBalanceMonitor(IPilotWalletReader wallet, TimeProvider clock)
{
    private long? _pilot;
    private long _balance;
    private long _nextPoll;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _balance = wallet.Balance(_pilot.Value);
            _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
        }
    }

    public void Reset() => _pilot = null;

    public void Notify(PilotCallContext context)
    {
        Enter(context);
        _balance = wallet.Balance(context.Pilot.PilotId);
        context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, (double)_balance / MarketDefinitions.CentsPerIsk));
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        long balance = wallet.Balance(context.Pilot.PilotId);
        double isk = (double)balance / MarketDefinitions.CentsPerIsk;
        // Existing market/combat/contract operations already enqueue their own
        // committed update. Polling also reaches credits from another session.
        if (
            balance != _balance
            && !context.Notifications.OfType<AccountBalanceChanged>().Any(n => n.PilotId == context.Pilot.PilotId && n.Balance == isk)
        )
        {
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, isk));
        }
        _balance = balance;
        _nextPoll = checked(now + TimeSpan.TicksPerSecond);
    }
}
