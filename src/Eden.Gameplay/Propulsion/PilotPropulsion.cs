using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Propulsion;

public enum NavigationSkill
{
    Navigation = 3449,
    Afterburner = 3450,
    FuelConservation = 3451,
    AccelerationControl = 3452,
    HighSpeedManeuvering = 3454,
}

public enum PropulsionModuleType
{
    OneMnAfterburnerI = 439,
    FiveMnMicrowarpdriveI = 434,
}

public sealed record NavigationBonus(int SkillId, DogmaAttributeId Attribute, double Percent, int? RequiredSkill = null, int? Group = null);

public sealed record PropulsionModule(
    long ItemId,
    int TypeId,
    double DurationMilliseconds,
    double CapacitorNeed,
    double SpeedFactor,
    double Thrust,
    double MassAddition,
    DogmaEffect Effect = DogmaEffect.Afterburner
)
{
    public FlightProperties Apply(FlightProperties basis)
    {
        double mass = basis.Mass + MassAddition;
        // Build 3662168 propulsionModules.Afterburner adds mass before reading
        // the ship mass used by its literal post-multiply speed modifier.
        double speed = basis.MaxSpeed * (1 + SpeedFactor / PropulsionDefinitions.Percent * Thrust / mass);
        if (
            !double.IsFinite(mass)
            || mass <= 0
            || !double.IsFinite(speed)
            || speed <= 0
            || speed > float.MaxValue
            || !double.IsFinite(DurationMilliseconds)
            || DurationMilliseconds <= 0
            || !double.IsFinite(CapacitorNeed)
            || CapacitorNeed < 0
            || !double.IsFinite(SpeedFactor)
            || SpeedFactor < 0
            || !double.IsFinite(Thrust)
            || Thrust <= 0
            || !double.IsFinite(MassAddition)
            || MassAddition < 0
        )
        {
            throw new InvalidDataException("Invalid propulsion module attributes.");
        }
        return basis with { Mass = mass, MaxSpeed = (float)speed };
    }
}

public sealed record PropulsionCycle(PropulsionModule Module, double Capacitor, double SignatureRadius);

public interface IPilotNavigationSkills
{
    IReadOnlyList<NavigationBonus> NavigationBonuses(long pilotId);
    double MaxSpeed(long pilotId);
}

public interface IPilotPropulsion : IPilotNavigationSkills
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    PropulsionCycle Activate(long pilotId, long moduleId, long now, DogmaEffect? effect = null);
    double Stop(long pilotId, long shipId, long moduleId);
    double SignatureRadius(long pilotId);
}

public static class PropulsionDefinitions
{
    public const int AfterburnerGroup = 46;
    public const double Percent = 100;
    private const int TechOne = 1;
    private const int TechTwo = 2;

    public static bool IsSupportedMicrowarpdrive(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is TechOne or TechTwo
        && !InventoryDefinitions.RequiresSpecificShip(attributes);
}
