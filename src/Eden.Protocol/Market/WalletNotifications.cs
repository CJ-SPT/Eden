using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Market;

internal sealed record AccountBalanceChanged(long PilotId, double Balance) : PilotNotification("OnAccountChange")
{
    public override object?[] Arguments => ["cash", PilotId, Balance];
}
