using Eden.Gameplay.Docking;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal static class PilotSessionWire
{
    internal const string PacketNamespace = "carbon.common.script.net.machoNetPacket.";

    private static Dictionary<string, object?> SessionFields(DockedPilot pilot)
    {
        return new()
        {
            ["charid"] = pilot.PilotId,
            ["corpid"] = pilot.CorpId,
            ["shipid"] = pilot.ShipId,
            ["stationid"] = pilot.Position is null ? pilot.StationId : null,
            ["solarsystemid"] = pilot.Position is null ? null : pilot.SystemId,
            ["stationid2"] = pilot.Position is null ? pilot.StationId : null,
            ["locationid"] = pilot.LocationId,
            ["solarsystemid2"] = pilot.SystemId,
            ["constellationid"] = pilot.ConstellationId,
            ["regionid"] = pilot.RegionId,
            ["raceID"] = pilot.Profile.RaceId,
            ["bloodlineID"] = pilot.Profile.BloodlineId,
            ["genderID"] = pilot.Profile.GenderId,
        };
    }

    internal static BlueInstance SessionChange(long accountId, long clientId, DockedPilot pilot, DockedPilot? previous = null)
    {
        var before = previous is null ? new Dictionary<string, object?>() : SessionFields(previous);
        var changes = SessionFields(pilot)
            .Where(field => !Equals(before.GetValueOrDefault(field.Key), field.Value))
            .ToDictionary(field => field.Key, field => (object?)new object?[] { before.GetValueOrDefault(field.Key), field.Value });
        return new(
            PacketNamespace + "SessionChangeNotification",
            new object?[]
            {
                16,
                new MachoAddress.Node(PilotLogin.NodeId).ToBlue(),
                new MachoAddress.Client(clientId).ToBlue(),
                accountId,
                new object?[] { clientId, new object?[] { 0, changes }, new List<object?>() },
                new Dictionary<string, object?>(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
    }

    internal static BlueInstance Response(object?[] state, string name, int command, object? body) =>
        new(
            PacketNamespace + name,
            new object?[]
            {
                command,
                state[2],
                state[1],
                state[3],
                body,
                new Dictionary<string, object?>(),
                null,
                null,
                state[8],
                null,
                state[10],
                state[11],
                state[12],
                null,
            }
        );

    internal static BlueInstance InitialState(long accountId, long clientId, long roles = (long)PilotRole.Player)
    {
        return new BlueInstance(
            PacketNamespace + "SessionInitialStateNotification",
            new object?[]
            {
                18,
                new MachoAddress.Node(PilotLogin.NodeId).ToBlue(),
                new MachoAddress.Client(clientId).ToBlue(),
                accountId,
                new object?[] { clientId, 5, new AccountSessionState(accountId, roles) },
                new Dictionary<string, object?>(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
    }

    internal static BlueInstance Notification(long accountId, long clientId, PilotNotification notification)
    {
        // Broadcast -> service -> object-call layers each wrap the payload.
        object?[] update = [1, notification.Arguments];
        object?[] payload = [0, BlueMarshal.Encode(new object?[] { 0, update })];
        return new BlueInstance(
            PacketNamespace + "Notification",
            new object?[]
            {
                12,
                new MachoAddress.Node(PilotLogin.NodeId).ToBlue(),
                new MachoAddress.Broadcast(notification.Name, clientId).ToBlue(),
                accountId,
                new object?[] { payload },
                new Dictionary<string, object?>(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
            }
        );
    }
}
