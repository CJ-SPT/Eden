using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

public sealed record ArmorRepairer(long ItemId, int TypeId, double DurationMilliseconds, double ArmorAmount, double CapacitorNeed);

public sealed record ArmorRepairSchedule(long ItemId, int TypeId, long StartedAt, long CompletesAt)
{
    public double DurationMilliseconds => (CompletesAt - StartedAt) / (double)TimeSpan.TicksPerMillisecond;
}

public sealed record ArmorRepairActivation(ArmorRepairSchedule Schedule, double Capacitor);

public interface IPilotArmorRepairing
{
    ArmorRepairActivation Start(long pilotId, long moduleId, long now);
    IReadOnlyList<ArmorRepairSchedule> Read(long pilotId, long shipId, long now);
    ShipHealth Complete(long pilotId, long shipId, long now);
}

public static class ArmorRepairDefinitions
{
    private const int TechOne = 1;
    private const int TechTwo = 2;
    public const int Group = 62;
    public const int RepairSystemsSkill = 3393;
    public const int SmallTechOne = 523;

    public static bool IsSupported(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is TechOne or TechTwo
        && !InventoryDefinitions.RequiresSpecificShip(attributes);
}
