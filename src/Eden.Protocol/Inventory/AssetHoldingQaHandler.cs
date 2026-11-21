using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Inventory;

[Injectable]
internal sealed class AssetHoldingQaHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "inventoryAssetHoldingMgr"
            || method
                is not (
                    "is_item_validation_enabled"
                    or "set_item_validation_enabled"
                    or "is_item_failure_enabled"
                    or "set_item_failure_enabled"
                )
            || (context.Roles & (long)PilotRole.QualityAssurance) == 0
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected Asset Holding QA options.");
        }
        // Build 3662168 reads both flags while building Insider's QA menu.
        // The switches track entitlement QA settings independently of ordinary inventory operations.
        switch (method, args)
        {
            case ("is_item_validation_enabled", []):
                return context.AssetHoldingItemValidationEnabled;
            case ("set_item_validation_enabled", [bool validationEnabled]):
                context.AssetHoldingItemValidationEnabled = validationEnabled;
                return PilotCallResult.None;
            case ("is_item_failure_enabled", []):
                return context.AssetHoldingItemFailureEnabled;
            case ("set_item_failure_enabled", [bool failureEnabled]):
                context.AssetHoldingItemFailureEnabled = failureEnabled;
                return PilotCallResult.None;
            default:
                return new PilotCallResult.Failure("Invalid Asset Holding QA arguments.");
        }
    }
}
