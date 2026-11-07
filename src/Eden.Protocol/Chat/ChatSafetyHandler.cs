using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Chat;

[Injectable]
internal sealed class ChatSafetyHandler : IPilotCallHandler
{
    // SitesSvc hashes Python str([]) with MD5 to version its local URL cache.
    // Eden has no configured site flags; return the matching list and digest.
    internal const string EmptySitesHash = "d751713988987e9331980363e24189ce";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "browserLockdownSvc" || method is not ("GetFlaggedSitesHash" or "GetFlaggedSitesList"))
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 0 } || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid chat URL-safety request.");
        }
        return method == "GetFlaggedSitesHash" ? EmptySitesHash : BlueEmpty.List;
    }
}
