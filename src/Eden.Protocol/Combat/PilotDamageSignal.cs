using Eden.Gameplay.Combat;
using Eden.Protocol.Flight;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotDamageSignal(SharedSpace space) : IPilotDamageEvents
{
    internal event Action<PilotDamage>? Applied;

    public void Publish(PilotDamage damage)
    {
        space.ApplyDamage(damage.Shot.Health);
        Applied?.Invoke(damage);
    }
}
