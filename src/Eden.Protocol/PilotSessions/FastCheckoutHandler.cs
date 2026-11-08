using Eden.Infrastructure.Registration;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class FastCheckoutHandler : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (
            service != "FastCheckoutService"
            || method is not ("GetTestingConfiguration" or "IsFastCheckoutEnabledForUser" or "ClearFastCheckoutCacheForUser")
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 0 } || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Checkout configuration does not accept arguments.");
        }
        // Eden has no payment integration or per-user checkout cache.
        return method switch
        {
            "GetTestingConfiguration" => new DisabledFastCheckoutConfigurationReply(),
            "IsFastCheckoutEnabledForUser" => false,
            _ => PilotCallResult.None,
        };
    }
}

internal sealed record DisabledFastCheckoutConfigurationReply : BlueModel
{
    // Build 3662168 fastCheckoutClientService._update_config unpacks this tuple as
    // China funnel enabled, fake offer URL, and use-shell-execute, in that order.
    public override object ToBlue() => new object?[] { false, string.Empty, false };
}
