using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class MultifitCapabilityHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "publicQaToolsServer" || method != "CanGiveItemForMultifit")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("The multifit item-creation capability check takes no arguments.");
        }
        // Build 3662168 MultiFitWnd uses this Boolean only for its QA "Create Missing"
        // button. Eden does not provide that public-QA item-spawning path, even for developers.
        return false;
    }
}
