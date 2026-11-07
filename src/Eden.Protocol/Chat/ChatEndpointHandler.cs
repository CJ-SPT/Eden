using Eden.Gameplay.Chat;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Chat;

// Endpoint discovery is needed during client initialization. Authentication and
// channel access remain owned by the selected-pilot handler.
[Injectable]
internal sealed class ChatEndpointHandler(IPilotChat chat) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "XmppChatMgr" || method is not ("Hostname" or "GetDeprecatedPrefsFallback"))
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 0 } || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid chat endpoint request.");
        }
        return method == "Hostname" ? chat.Endpoint.Domain : chat.Endpoint.Address;
    }
}
