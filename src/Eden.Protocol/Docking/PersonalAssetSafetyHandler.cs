using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class PersonalAssetSafetyHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "structureAssetSafety" || method != "GetItemsInSafetyForCharacter")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid personal asset safety request.");
        }
        // No Upwell storage or asset-safety wraps exist in the saved world yet.
        return new PersonalAssetSafetyItems();
    }
}

internal sealed record PersonalAssetSafetyItems : BlueModel
{
    public override object ToBlue() => new List<object?>();
}
