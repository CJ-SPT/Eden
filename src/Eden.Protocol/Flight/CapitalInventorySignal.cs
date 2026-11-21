using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Flight;

[Injectable(InjectionType.Singleton)]
internal sealed class CapitalInventorySignal : ICapitalInventoryEvents
{
    internal event Action<long, IReadOnlyList<InventoryChange>>? FuelSpent;

    public void Publish(long pilotId, IReadOnlyList<InventoryChange> changes) => FuelSpent?.Invoke(pilotId, changes);
}
