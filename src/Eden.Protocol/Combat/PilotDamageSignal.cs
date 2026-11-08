using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotDamageSignal(SharedSpace space, ICapsuleerSimulation? capsuleers = null, ICapsuleerHauling? hauling = null)
    : IPilotDamageEvents
{
    internal event Action<PilotDamage>? Applied;

    public void Publish(PilotDamage damage)
    {
        space.ApplyDamage(damage.Health);
        capsuleers?.ObserveAggression(damage.Health.ShipId);
        hauling?.ObserveAggression(damage.Health.ShipId);
        Applied?.Invoke(damage);
    }
}
