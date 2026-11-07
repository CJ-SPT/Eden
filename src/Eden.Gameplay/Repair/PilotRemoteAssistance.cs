using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public sealed record RemoteAssistanceContact(
    long PilotId,
    long SourceShipId,
    long TargetId,
    RemoteAssistanceModule Module,
    double EdgeDistance,
    long StartedAt,
    long ExpiresAt
);

public sealed record RemoteAssistanceActivation(RemoteAssistanceContact Contact, double Capacitor);

public interface IPilotRemoteAssistance
{
    RemoteAssistanceActivation Activate(long pilotId, long moduleId, DogmaEffect effect, long targetId, IReadOnlySet<long> locks, long now);
    bool IsActive(long pilotId, long shipId, long moduleId);
    IReadOnlyList<RemoteAssistanceContact> Incoming(long targetId);
    void Stop(long pilotId, long shipId, long moduleId);
    void Cancel(long pilotId, long shipId);
}
