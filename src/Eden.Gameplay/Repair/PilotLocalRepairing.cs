using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Repair;

public sealed record LocalRepairer(long ItemId, int TypeId, double DurationMilliseconds, double Amount, double CapacitorNeed);

public sealed record LocalRepairSchedule(
    long ItemId,
    int TypeId,
    long StartedAt,
    long CompletesAt,
    RepairLayer Layer = RepairLayer.Armor,
    bool Ancillary = false
)
{
    public double DurationMilliseconds => (CompletesAt - StartedAt) / (double)TimeSpan.TicksPerMillisecond;
}

public sealed record LocalRepairActivation(LocalRepairSchedule Schedule, double Capacitor, Magazine? Magazine = null);

public interface IPilotLocalRepairing
{
    LocalRepairActivation Start(long pilotId, long moduleId, long now, RepairLayer layer = RepairLayer.Armor, bool ancillary = false);
    IReadOnlyList<LocalRepairSchedule> Read(long pilotId, long shipId, long now);
    ShipHealth Complete(long pilotId, long shipId, long now);
}

public static class ArmorRepairDefinitions
{
    public const int Group = 62;
    public const int RepairSystemsSkill = 3393;
    public const int SmallTechOne = 523;

    public static bool IsSupported(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        LocalRepairDefinitions.IsSupported(attributes);
}
