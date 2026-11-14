using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class SharedSettingsHandler(ISharedSettings settings) : ISelectedPilotCallHandler
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
            (service, method)
            is not (

                ("overviewPresetMgr", "StoreLinkAndGetID" or "GetStoredPreset")
                or
                ("sharedSettingsMgr", "StoreSettingLinkAndGetID" or "GetStoredSharedSetting")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected shared settings arguments.");
            }
            long owner = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("StoreLinkAndGetID", [var payload]):
                    return new SharedSettingKeyReply(settings.Store(owner, SharedSettingKind.Overview, SharedSettingsWire.Encode(payload)));
                case ("StoreSettingLinkAndGetID", [long kind, var payload]):
                    return new SharedSettingKeyReply(settings.Store(owner, ReadKind(kind), SharedSettingsWire.Encode(payload)));
                case ("GetStoredPreset", [object?[] key]) when key is [var hash, long id]:
                    return Read(owner, hash, id, SharedSettingKind.Overview);
                case ("GetStoredSharedSetting", [object?[] key]) when key is [var hash, long id, long kind]:
                    return Read(owner, hash, id, ReadKind(kind));
                default:
                    return new PilotCallResult.Failure("Invalid shared settings request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private PilotCallResult Read(long owner, object? hash, long id, SharedSettingKind kind)
    {
        if (hash is not (string or byte[]) || id <= 0)
        {
            throw new InvalidDataException("Invalid shared settings key.");
        }
        string? payload = settings.Read(owner, new(BlueMarshal.Text(hash), id, kind));
        return payload is null ? PilotCallResult.None : new SharedSettingPayloadReply(payload);
    }

    private static SharedSettingKind ReadKind(long kind) =>
        kind switch
        {
            (long)SharedSettingKind.InventoryFilter => SharedSettingKind.InventoryFilter,
            (long)SharedSettingKind.FleetBroadcast => SharedSettingKind.FleetBroadcast,
            _ => throw new InvalidDataException("Unknown shared setting type."),
        };
}

internal sealed record SharedSettingPayloadReply(string Payload) : BlueModel
{
    public override object ToBlue() => new BlueUnicode(Payload);
}

internal sealed record SharedSettingKeyReply(SharedSettingKey Key) : BlueModel
{
    public override object ToBlue()
    {
        Dictionary<string, object?> fields = new() { ["hashvalue"] = Key.Hash, ["sqID"] = Key.Id };
        if (Key.Kind != SharedSettingKind.Overview)
        {
            fields["settingTypeID"] = (long)Key.Kind;
        }
        return new BlueInstance("utillib.KeyVal", fields);
    }
}
