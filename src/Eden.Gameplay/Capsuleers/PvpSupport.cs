using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public enum ObservedPvpWeapon
{
    Unknown,
    Turret,
    Missile,
    Drone,
}

public sealed record PvpSupportModule(DogmaEffect Effect, double Optimal, double Falloff);

public static class PvpSupport
{
    private const double PrimaryPenalty = .25;
    private const double AssignedPenalty = .25;
    private const double Retention = .75;
    private const double UnknownWeaponPenalty = .5;
    private const double CapacitorWeaponBonus = 2;
    private const double LogisticsPriority = 12;
    private const double ElectronicPriority = 8;
    private const double LogisticsFrigatePriority = 6;
    public const double MaximumFalloffs = 3;

    public static bool Independent(DogmaEffect effect) =>
        effect
            is DogmaEffect.PlayerEcm
                or DogmaEffect.SensorDampener
                or DogmaEffect.TrackingDisruptor
                or DogmaEffect.GuidanceDisruptor
                or DogmaEffect.EnergyNeutralizer;

    public static bool Supported(DogmaEffect effect) =>
        Independent(effect) || effect is DogmaEffect.TargetPainter or DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler;

    public static bool RemoteRepair(DogmaEffect effect) => effect is DogmaEffect.RemoteArmorRepair or DogmaEffect.RemoteShieldBoost;

    public static double Priority(HullClass hull) =>
        hull switch
        {
            HullClass.Logistics or HullClass.ForceAuxiliary => LogisticsPriority,
            HullClass.LogisticsFrigate => LogisticsFrigatePriority,
            HullClass.ElectronicAttack or HullClass.ForceRecon or HullClass.CombatReconShip => ElectronicPriority,
            _ => PvpTactics.Threat(hull),
        };

    public static bool InRange(PvpSupportModule module, double distance) =>
        module.Optimal > 0 && distance <= module.Optimal + module.Falloff * MaximumFalloffs;

    public static long Select(
        PvpSupportModule module,
        SpacePosition position,
        double radius,
        IReadOnlyList<PilotSighting> opponents,
        long primary,
        long current,
        IReadOnlySet<long> assigned
    )
    {
        double Score(PilotSighting target)
        {
            double distance = Math.Max(0, position.DistanceTo(target.Position) - radius - target.Radius);
            if (!InRange(module, distance))
            {
                return 0;
            }
            if (
                module.Effect == DogmaEffect.TrackingDisruptor
                    && target.ObservedWeapon is ObservedPvpWeapon.Missile or ObservedPvpWeapon.Drone
                || module.Effect == DogmaEffect.GuidanceDisruptor
                    && target.ObservedWeapon is ObservedPvpWeapon.Turret or ObservedPvpWeapon.Drone
            )
            {
                return 0;
            }
            double application = distance <= module.Optimal ? 1 : Math.Pow(.5, Math.Pow((distance - module.Optimal) / module.Falloff, 2));
            double score = Priority(target.Hull) * application;
            if (
                module.Effect is DogmaEffect.TrackingDisruptor or DogmaEffect.GuidanceDisruptor
                && target.ObservedWeapon == ObservedPvpWeapon.Unknown
            )
            {
                score *= UnknownWeaponPenalty;
            }
            if (module.Effect == DogmaEffect.EnergyNeutralizer && target.CapacitorWeaponObserved)
            {
                score *= CapacitorWeaponBonus;
            }
            return score * (target.ShipId == primary ? PrimaryPenalty : 1) * (assigned.Contains(target.ShipId) ? AssignedPenalty : 1);
        }
        var scored = opponents
            .Select(target => new PvpDecision(target.ShipId, Score(target), "Supporting the gang."))
            .Where(choice => choice.Score > 0)
            .OrderByDescending(choice => choice.Score)
            .ThenBy(choice => choice.TargetId)
            .ToArray();
        var best = scored.FirstOrDefault();
        var retained = scored.FirstOrDefault(choice => choice.TargetId == current);
        return retained is not null && retained.Score >= (best?.Score ?? 0) * Retention ? retained.TargetId : best?.TargetId ?? 0;
    }
}
