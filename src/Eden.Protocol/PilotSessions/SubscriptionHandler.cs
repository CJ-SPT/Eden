using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class SubscriptionHandler : IPilotCallHandler
{
    // Build 3662168 PLEXPanel treats zero as Alpha and a nonzero FILETIME as an
    // Omega expiry. Eden's saved Omega grade does not expire. The last UTC calendar
    // day is a stable presentation value, within native date formatting bounds;
    // it is not a saved subscription deadline and never changes the clone grade.
    private static readonly long s_permanentOmegaExpiry = new DateTime(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "subscriptionMgr" || method != "GetSubscriptionTime")
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 0 } || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Subscription information does not accept arguments.");
        }
        return context.Account.CloneGrade == AccountCloneGrade.Omega ? s_permanentOmegaExpiry : 0L;
    }
}
