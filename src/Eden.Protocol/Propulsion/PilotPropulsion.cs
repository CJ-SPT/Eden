using Eden.Protocol.Docking;

namespace Eden.Protocol.Propulsion;

public enum NavigationSkill
{
    Navigation = 3449,
    Afterburner = 3450,
    FuelConservation = 3451,
    AccelerationControl = 3452,
}

public enum PropulsionModuleType
{
    OneMnAfterburnerI = 439,
}

public sealed record NavigationBonus(int SkillId, DogmaAttributeId Attribute, double Percent, int? RequiredSkill = null, int? Group = null);
public sealed record Afterburner(long ItemId, int TypeId, double DurationMilliseconds, double CapacitorNeed,
    double SpeedFactor, double Thrust, double MassAddition)
{
    public FlightProperties Apply(FlightProperties basis)
    {
        double mass = basis.Mass + MassAddition;
        // Build 3662168 propulsionModules.Afterburner adds mass before reading
        // the ship mass used by its literal post-multiply speed modifier.
        double speed = basis.MaxSpeed * (1 + SpeedFactor / PropulsionDefinitions.Percent * Thrust / mass);
        if (!double.IsFinite(mass) || mass <= 0 || !double.IsFinite(speed) || speed <= 0 || speed > float.MaxValue ||
            !double.IsFinite(DurationMilliseconds) || DurationMilliseconds <= 0 || !double.IsFinite(CapacitorNeed) || CapacitorNeed < 0 ||
            !double.IsFinite(SpeedFactor) || SpeedFactor < 0 || !double.IsFinite(Thrust) || Thrust <= 0 ||
            !double.IsFinite(MassAddition) || MassAddition < 0)
        {
            throw new InvalidDataException("Invalid afterburner attributes.");
        }
        return basis with { Mass = mass, MaxSpeed = (float)speed };
    }
}

public sealed record PropulsionCycle(Afterburner Module, double Capacitor);

public interface IPilotNavigationSkills
{
    IReadOnlyList<NavigationBonus> NavigationBonuses(long pilotId);
    double MaxSpeed(long pilotId);
}

public interface IPilotPropulsion : IPilotNavigationSkills
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    PropulsionCycle Activate(long pilotId, long moduleId, long now);
}

public static class PropulsionDefinitions
{
    public const int AfterburnerGroup = 46;
    public const double Percent = 100;
    public const string EffectName = "moduleBonusAfterburner";
    public const string EffectGraphic = "effects.Afterburner";
}
