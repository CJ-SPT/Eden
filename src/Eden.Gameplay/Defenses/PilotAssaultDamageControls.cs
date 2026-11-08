using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Defenses;

public sealed record AssaultDamageControlActivation(
    AssaultDamageControl Module,
    AssaultDamageControlSchedule Schedule,
    double Capacitor,
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes
);

public interface IPilotAssaultDamageControls
{
    AssaultDamageControlActivation Activate(long pilotId, long moduleId, long now);
    bool Active(long pilotId, long shipId, long moduleId, long now);
    IReadOnlyDictionary<DogmaAttributeId, double> Stop(long pilotId, long shipId, long moduleId, long expiresAt);
}
