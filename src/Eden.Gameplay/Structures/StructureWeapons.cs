using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;

namespace Eden.Gameplay.Structures;

public sealed record StructureWeaponCycle(
    long ModuleId,
    long TargetId,
    int TypeId,
    long StartedAt,
    long ReadyAt,
    double DurationMilliseconds,
    int Repeats,
    bool Stopping = false,
    DogmaEffect Effect = DogmaEffect.UseMissiles,
    long ReactivationAt = 0
)
{
    public bool Active(long now) => ReactivationAt == 0 || ReadyAt > now;
}

public sealed record StructureWeaponActivation(
    StructureWeaponCycle Cycle,
    MissileLaunch? Launch,
    CapacitorActivation? Neutralization = null,
    TackleActivation? Tackle = null
);

public sealed record StructureWeaponFrame(IReadOnlyList<StructureWeaponActivation> Started, IReadOnlyList<StructureWeaponCycle> Stopped);

public interface IPilotStructureWeapons
{
    StructureWeaponActivation Activate(
        StructureControlToken token,
        long moduleId,
        long targetId,
        int repeats,
        int stamp,
        DogmaEffect effect = DogmaEffect.UseMissiles
    );
    void Deactivate(StructureControlToken token, long moduleId, DogmaEffect? effect = null);
    StructureWeaponFrame Advance(StructureControlToken token, int stamp);
    IReadOnlyList<StructureWeaponCycle> Read(StructureControlToken token);
}

public static class StructureWeaponRules
{
    // Build 3662168: Standup anticapital and multirole launchers use the standard useMissiles effect.
    public const int AnticapitalLauncherGroup = 1327;
    public const int MultiroleLauncherGroup = 1562;
    public const int WebifierGroup = 1441;
    public const int WarpDisruptorGroup = 1442;
    public const int ElectronicSystemsSkill = 37798;
    public const int NeutralizerGroup = 1329;
    public const int EngineeringSystemsSkill = 37799;
    public const int MissileSystemsSkill = 37796;
    public const DogmaAttributeId MissileSkillBonus = (DogmaAttributeId)292;

    public static bool UsesElectronicSystems(int group) => group is WebifierGroup or WarpDisruptorGroup;

    public static bool MissileLauncher(int group) => group is AnticapitalLauncherGroup or MultiroleLauncherGroup;

    public static double DamageMultiplier(int level, double bonusPercent)
    {
        if (level is < 0 or > 5 || !double.IsFinite(bonusPercent) || bonusPercent < 0)
        {
            throw new InvalidDataException("Invalid structure missile operating skill.");
        }
        return 1 + level * bonusPercent / 100;
    }

    public static double CapacitorCostMultiplier(int level, double bonusPercent)
    {
        if (level is < 0 or > 5 || !double.IsFinite(bonusPercent) || bonusPercent > 0)
        {
            throw new InvalidDataException("Invalid structure engineering capacitor cost.");
        }
        return Math.Max(0, 1 + level * bonusPercent / 100);
    }

    public static long ReadyAt(double milliseconds, long now)
    {
        double ticks = Math.Ceiling(milliseconds * TimeSpan.TicksPerMillisecond);
        if (!double.IsFinite(ticks) || ticks <= 0 || ticks >= long.MaxValue - (double)now)
        {
            throw new InvalidDataException("The defensive weapon has no valid firing cycle.");
        }
        return checked(now + (long)ticks);
    }

    public static void CheckPlayer(double security, CrimewatchFlag flag, bool outlaw, bool retaliation, bool warTarget = false)
    {
        // A structure's green safety belongs to the structure, independently of its operator's private engagements.
        _ = CrimewatchRules.Decide(
            new(security, ShipSafetyLevel.Full, false, false, flag, outlaw, retaliation, CorporationWarTarget: warTarget)
        );
    }
}
