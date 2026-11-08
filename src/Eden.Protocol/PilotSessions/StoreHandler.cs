using Eden.Infrastructure.Registration;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class StoreHandler : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "storeManager" || method is not ("get_offers" or "get_products" or "get_categories"))
        {
            return PilotCallResult.Unsupported;
        }
        if (
            arguments is not object?[] { Length: 1 } args
            || args[0] is not long storeId
            || !Enum.IsDefined((NativeStoreId)storeId)
            || keywords is { Count: > 0 }
        )
        {
            return new PilotCallResult.Failure("Choose a supported store for catalog information.");
        }

        // VgsClient expects a Python list. Empty catalogs select its existing
        // StoreUnavailableError path; Eden has no commercial offers or products.
        return BlueEmpty.List;
    }

    // Build 3662168 vgs/common/const.pyj store identifiers.
    private enum NativeStoreId : long
    {
        InGame = 4,
        Code = 5,
        RealMoney = 9,
    }
}
