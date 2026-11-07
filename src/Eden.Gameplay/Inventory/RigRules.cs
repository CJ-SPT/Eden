using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

// Build 3662168 ship rig groups; structure rigs and blueprints are separate families.
public enum RigGroup
{
    Armor = 773,
    Shield = 774,
    EnergyWeapon = 775,
    HybridWeapon = 776,
    ProjectileWeapon = 777,
    Drones = 778,
    Launcher = 779,
    Core = 781,
    Navigation = 782,
    ElectronicSystems = 786,
    ResourceProcessing = 1232,
    Scanning = 1233,
    Targeting = 1234,
    Anchor = 1308,
}

public enum RiggingSkill
{
    Jury = 26252,
    Armor = 26253,
    Astronautics = 26254,
    Drones = 26255,
    ElectronicSuperiority = 26256,
    ProjectileWeapon = 26257,
    EnergyWeapon = 26258,
    HybridWeapon = 26259,
    Launcher = 26260,
    Shield = 26261,
}

public static class RigRules
{
    public const int MaximumSlots = 3;

    public static IReadOnlySet<int> Groups { get; } = Enum.GetValues<RigGroup>().Select(g => (int)g).ToHashSet();

    public static void CheckFit(
        IReadOnlyDictionary<DogmaAttributeId, double> hull,
        IReadOnlyDictionary<DogmaAttributeId, double> rig,
        double calibrationUsed
    )
    {
        double size = rig.GetValueOrDefault(DogmaAttributeId.RigSize);
        double cost = rig.GetValueOrDefault(DogmaAttributeId.CalibrationCost);
        double capacity = hull.GetValueOrDefault(DogmaAttributeId.CalibrationCapacity);
        if (size is not (1 or 2 or 3 or 4) || hull.GetValueOrDefault(DogmaAttributeId.RigSize) != size)
        {
            throw new InvalidDataException("The rig size does not match this ship.");
        }
        if (
            !double.IsFinite(cost)
            || cost <= 0
            || !double.IsFinite(capacity)
            || !double.IsFinite(calibrationUsed)
            || calibrationUsed < 0
            || calibrationUsed + cost > capacity
        )
        {
            throw new InvalidDataException("The ship does not have enough calibration for this rig.");
        }
    }
}

public interface IPilotRigRemoval
{
    IReadOnlyList<InventoryChange> DestroyRig(long pilotId, long shipId, long itemId);
}
