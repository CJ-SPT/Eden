using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Repair;

internal static class RemoteAssistanceWire
{
    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "shipModuleRemoteTrackingComputer" => DogmaEffect.RemoteTrackingComputer,
            "remoteSensorBoostFalloff" => DogmaEffect.RemoteSensorBooster,
            _ => null,
        };
}

internal sealed record RemoteAssistanceChanged(RemoteAssistanceContact Contact, bool Active)
    : PilotNotification(Active ? "OnEwarStart" : "OnEwarEnd")
{
    // Pinned buffBarConst.pyj uses assistance slots distinct from hostile EW.
    public override object?[] Arguments =>
        [
            Contact.SourceShipId,
            Contact.Module.Cycle.ItemId,
            Contact.TargetId,
            Contact.Module.Cycle.Effect == DogmaEffect.RemoteTrackingComputer ? "remoteTracking" : "sensorBooster",
        ];
}
