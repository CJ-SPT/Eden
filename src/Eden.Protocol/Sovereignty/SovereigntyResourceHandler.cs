using Eden.Gameplay.Inventory;
using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Sovereignty;

[Injectable]
internal sealed class SovereigntyResourceHandler(ISovereigntyResources resources, IPilotEntosis entosis) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool component =
            service == "beyonce"
            && method == "CallComponentFromClient"
            && args.Length >= 3
            && args[1] is string or byte[]
            && BlueMarshal.Text(args[1]) == "orbitalSkyhook";
        bool listing = service == "colonyResourcesMgr" && method == "GetMyCorporationSkyhooks";
        bool access = service == "sovMgr" && method is "GetSovHubFuelAccessGroup" or "IsOnLocalSovHubFuelAccessGroup";
        if (!component && !listing && !access)
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected sovereignty resource arguments.");
        }
        try
        {
            if (listing && args.Length == 0)
            {
                return new CorporationSkyhooksReply(resources.Skyhooks(context.Pilot.PilotId, true));
            }
            if (access && method == "GetSovHubFuelAccessGroup" && args is [long system] && system is > 0 and <= int.MaxValue)
            {
                // Build 3662168's HubController passes its solar-system ID, unlike the fuel gateway.
                var hub = entosis.Hub((int)system) is { Claim: not null } claimed
                    ? claimed
                    : throw new InvalidDataException("This sovereignty hub has no active claim.");
                resources.Read(context.Pilot.PilotId, hub.HubId);
                return PilotCallResult.None;
            }
            if (access && method == "IsOnLocalSovHubFuelAccessGroup" && args.Length == 0)
            {
                return entosis.Hub(context.Pilot.SystemId) is { } local && resources.CanManageHub(context.Pilot.PilotId, local.HubId);
            }
            if (component && args is [long id, _, var operation] && BlueMarshal.Text(operation) == "IsCharacterAuthorised")
            {
                return resources.CanManageSkyhook(context.Pilot.PilotId, id);
            }
            if (
                component
                && args is [long skyhook, _, var command, long flag, long quantity]
                && BlueMarshal.Text(command) == "ExtractReagentsToShip"
                && flag == (long)InventoryFlag.Cargo
                && quantity is > 0 and <= int.MaxValue
            )
            {
                var result = resources.CollectReagents(context.Pilot.PilotId, skyhook, (int)quantity);
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return result.Changes.Sum(change => change.Item.Quantity - (change.Previous?.Quantity ?? 0));
            }
            return new PilotCallResult.Failure("Invalid sovereignty resource request.");
        }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationSkyhooksReply(IReadOnlyList<SovereigntySkyhook> Skyhooks) : BlueModel
{
    public override object ToBlue() =>
        new BlueList<CorporationSkyhookRow>(Skyhooks.Select(row => new CorporationSkyhookRow(row)).ToArray());
}

internal sealed record CorporationSkyhookRow(SovereigntySkyhook Skyhook) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["skyhookID"] = Skyhook.Id,
                ["itemID"] = Skyhook.Id,
                ["solarSystemID"] = Skyhook.SystemId,
                ["planetID"] = Skyhook.PlanetId,
                ["typeID"] = SovereigntyResourceRules.SkyhookTypeId,
                ["state"] = (int)Skyhook.State,
                ["corporationID"] = Skyhook.CorporationId,
                ["ownerCorpID"] = Skyhook.CorporationId,
                ["allianceID"] = Skyhook.AllianceId == 0 ? null : Skyhook.AllianceId,
            }
        );
}
