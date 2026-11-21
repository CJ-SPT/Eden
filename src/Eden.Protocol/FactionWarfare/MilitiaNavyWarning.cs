using Eden.Gameplay.FactionWarfare;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

internal sealed record MilitiaNavyWarning(MilitiaFaction Faction) : PilotNotification("OnRemoteMessage")
{
    // Build 3662168 gameui.OnRemoteMessage passes the message ID and payload to the notification UI.
    public override object?[] Arguments =>
        [
            "CustomNotify",
            new Dictionary<string, object?> { ["notify"] = new BlueUnicode($"The {Faction} navy has detected an enemy militia ship.") },
        ];
}
