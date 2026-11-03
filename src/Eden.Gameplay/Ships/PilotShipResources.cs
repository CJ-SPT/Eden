using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Ships;

public sealed record ShipResourceSnapshot(ShipHealth Health, double Capacitor, IReadOnlyDictionary<DogmaAttributeId, double> Attributes);

public interface IPilotShipResources
{
    ShipResourceSnapshot Read(long pilotId, long shipId, long now);
    double Capacitor(long pilotId, long now);
}
