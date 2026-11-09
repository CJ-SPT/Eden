using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerAttacker(
    long PilotId,
    long ShipId,
    int TypeId,
    SpacePosition Position,
    SpacePosition Velocity,
    double Radius,
    double Signature,
    long AttackedAt,
    bool Tackling,
    bool Npc
);

public sealed record CapsuleerLock(long TargetId, long ReadyAt);

public sealed record CapsuleerModuleCycle(
    long ModuleId,
    int TypeId,
    DogmaEffect Effect,
    long TargetId,
    long StartedAt,
    long ReadyAt,
    int ChargeTypeId = 0
)
{
    public int FlagId { get; init; }
    public bool Unavailable { get; init; }
    public PropulsionModule? Propulsion { get; init; }
    public TackleModule? Tackle { get; init; }
    public Hardener? Hardener { get; init; }
    public CapacitorCycle? Capacitor { get; init; }
    public ElectronicWarfareContact[] ElectronicWarfare { get; init; } = [];
    public SignatureSuppressor? SignatureSuppression { get; init; }
    public SignatureSuppressionSchedule? SuppressionSchedule { get; init; }
    public AssaultDamageControl? AssaultDamageControl { get; init; }
    public AssaultDamageControlSchedule? ControlSchedule { get; init; }
}

public sealed record CapsuleerDefenseState
{
    public long ShipId { get; init; }
    public long TargetId { get; init; }
    public long LastAttackAt { get; init; }
    public bool Retreating { get; init; }
    public long RecallUntil { get; init; }
    public bool DroneLaunchAttempted { get; init; }
    public int MotionStamp { get; init; } = int.MinValue;
    public CapsuleerLock[] Locks { get; init; } = [];
    public CapsuleerModuleCycle[] Cycles { get; init; } = [];
}

public static class CapsuleerDefenseRules
{
    private const double OffensiveCapacitorReserve = .2;
    public const double RepairFraction = .8;
    public const double CapacitorBoosterFraction = .3;
    public const double EmergencyFraction = .4;
    public const double RetreatHealthFraction = .35;
    public const double RetreatStructureFraction = .75;
    public static readonly TimeSpan RecallTimeout = TimeSpan.FromSeconds(10);

    public static CapsuleerAttacker? Select(IReadOnlyList<CapsuleerAttacker> attackers) =>
        attackers
            .OrderByDescending(attacker => attacker.Tackling)
            .ThenByDescending(attacker => attacker.AttackedAt)
            .ThenBy(attacker => attacker.ShipId)
            .FirstOrDefault();

    public static bool CombatShip(HullClass hull, int tech, bool hasDamage) =>
        hasDamage
        && hull != HullClass.Shuttle
        && (HullSupportRules.IsClass(hull, HullCohort.Combat, tech) || HullSupportRules.IsClass(hull, HullCohort.Small, tech));

    public static bool Retreat(ShipHealth health, bool hasDamage)
    {
        double Effective(double shield, double armor, double structure) =>
            shield / Math.Max(.001, health.Defense.ShieldResonance.Total / 4)
            + armor / Math.Max(.001, health.Defense.ArmorResonance.Total / 4)
            + structure / Math.Max(.001, health.Defense.StructureResonance.Total / 4);
        double maximum = Effective(health.Defense.Shield, health.Defense.Armor, health.Defense.Structure);
        double remaining = Effective(health.Remaining.Shield, health.Remaining.Armor, health.Remaining.Structure);
        return !hasDamage
            || maximum <= 0
            || remaining / maximum < RetreatHealthFraction
            || Fraction(health.Remaining.Structure, health.Defense.Structure) < RetreatStructureFraction;
    }

    public static double Fraction(double current, double maximum) => maximum > 0 ? Math.Clamp(current / maximum, 0, 1) : 1;

    public static bool Offensive(bool combatShip, bool retreating, bool warpBlocked) =>
        combatShip && !retreating || !combatShip && warpBlocked;

    public static bool ShouldActivate(
        DogmaEffect effect,
        double cost,
        ShipHealth health,
        double capacitor,
        double capacity,
        bool offensive,
        bool locked,
        bool blocked,
        bool shieldTank
    )
    {
        double fraction = Fraction(capacitor, capacity);
        bool affordable = double.IsFinite(cost) && cost >= 0 && capacitor >= cost;
        bool reserved = affordable && Fraction(capacitor - cost, capacity) >= OffensiveCapacitorReserve;
        double shield = Fraction(health.Remaining.Shield, health.Defense.Shield);
        double armor = Fraction(health.Remaining.Armor, health.Defense.Armor);
        double hull = Fraction(health.Remaining.Structure, health.Defense.Structure);
        return effect switch
        {
            DogmaEffect.CapacitorBoosting => fraction < CapacitorBoosterFraction,
            DogmaEffect.ShieldBoosting or DogmaEffect.AncillaryShieldBoosting => affordable && shield < RepairFraction,
            DogmaEffect.ArmorRepair or DogmaEffect.AncillaryArmorRepair => affordable && armor < RepairFraction,
            DogmaEffect.StructureRepair => affordable && hull < RepairFraction,
            DogmaEffect.ActiveShieldResistance or DogmaEffect.ActiveArmorResistance => affordable,
            DogmaEffect.AssaultDamageControl or DogmaEffect.SignatureSuppression => affordable
                && (shieldTank ? shield : armor) < EmergencyFraction,
            DogmaEffect.WarpCoreStabilizer => affordable && blocked,
            DogmaEffect.Afterburner or DogmaEffect.Microwarpdrive => affordable,
            DogmaEffect.SensorBooster
            or DogmaEffect.TrackingComputer
            or DogmaEffect.MissileGuidanceComputer
            or DogmaEffect.OmnidirectionalTrackingLink => reserved && offensive,
            DogmaEffect.LaserFired
            or DogmaEffect.ProjectileFired
            or DogmaEffect.UseMissiles
            or DogmaEffect.AutoTargetingMissile
            or DogmaEffect.StasisWebifier
            or DogmaEffect.WarpDisruptor
            or DogmaEffect.WarpScrambler
            or DogmaEffect.PlayerEcm
            or DogmaEffect.SensorDampener
            or DogmaEffect.TrackingDisruptor
            or DogmaEffect.GuidanceDisruptor
            or DogmaEffect.TargetPainter
            or DogmaEffect.EnergyNeutralizer
            or DogmaEffect.EnergyNosferatu => reserved && offensive && locked,
            _ => false,
        };
    }

    public static long NextDeadline(CapsuleerDefenseState? state, long now, long elapsed, long ordinary)
    {
        if (state is null)
        {
            return ordinary;
        }
        long next = state
            .Cycles.Select(cycle => cycle.ReadyAt)
            .Concat(state.Locks.Select(scan => scan.ReadyAt))
            .Where(deadline => deadline > now)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        return next == long.MaxValue ? ordinary : Math.Min(ordinary, checked(elapsed + next - now));
    }
}
