using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Localization;

[Injectable]
internal sealed class LocalizationHandler : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service != "localizationServer" || method != "UpdateLocalizationQAWrap")
        {
            return PilotCallResult.Unsupported;
        }
        if (arguments is not object?[] { Length: 1 } values || values[0] is not bool enabled || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Expected a localization QA wrapping flag.");
        }
        // Build 3662168 sends this both from QA character selection and settings.
        // It is a connection session variable, not a saved pilot preference.
        context.LocalizationQaWrap = enabled;
        return PilotCallResult.None;
    }
}
