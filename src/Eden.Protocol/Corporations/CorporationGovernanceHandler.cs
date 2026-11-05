using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationGovernanceHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "voteManager" || method != "CanViewVotes")
        {
            return PilotCallResult.Unsupported;
        }
        if (args is not [long corporation] || corporation is <= 0 or > int.MaxValue || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Choose a corporation to query voting access.");
        }
        // Build 3662168 corporationWindow.construct_corporation_panels asks for
        // this capability on every open, including NPC corporations. Voting is
        // not implemented yet; False omits its panels without failing startup.
        return false;
    }
}
