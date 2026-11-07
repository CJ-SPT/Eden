using Eden.Gameplay.Repair;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Singleton)]
internal sealed class RemoteRepairSignal(SharedSpace space) : IRemoteRepairEvents
{
    internal event Action<RemoteRepairApplied>? Applied;

    public void Publish(RemoteRepairApplied repair)
    {
        space.ApplyDamage(repair.Health);
        Applied?.Invoke(repair);
    }
}
