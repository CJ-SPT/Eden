using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public sealed record PvpDecision(long TargetId, double Score, string Reason);

public sealed record PvpRangeBand(double Minimum, double Desired, double Maximum);

public static class PvpTactics
{
    private sealed record Affiliation(int? Alliance, int? Corporation, int? RegionalGroup, long CampGate, long CampUntil, long SoloShip);

    private static Affiliation Formation(PilotSighting contact) =>
        contact.AllianceId is { } alliance ? new(alliance, null, null, 0, 0, 0)
        : contact.CorporationId >= CorporationRules.FirstCorporationId ? new(null, contact.CorporationId, null, 0, 0, 0)
        : contact.RegionalGroupId is { } group ? new(null, null, group, 0, 0, 0)
        : contact.Camp is { } camp ? new(null, null, null, camp.GateId, camp.Until, 0)
        : new(null, null, null, 0, 0, contact.ShipId);

    public const double MinimumRange = 500;
    public const double KitingRange = 10_000;
    public const double TackleRangeMargin = .8;
    private const double TargetRetention = .75;
    private const double MaximumThreatRatio = 1.5;
    private const double MinimumApplication = .05;
    private const double EmergencyCapacitorFraction = .1;
    private const double PropulsionReserveFraction = .25;
    private const double RangeToleranceFraction = .15;
    public const double PursuitSeconds = 20;
    private const double AccelerationAllowance = .5;
    public static readonly long CommitmentTicks = TimeSpan.FromSeconds(8).Ticks;

    // Hull classes are uncertain threat estimates, not access to an opponent's fit or resources.
    public static double Threat(HullClass hull) =>
        hull switch
        {
            HullClass.Battleship => 8,
            HullClass.CombatBattlecruiser or HullClass.AttackBattlecruiser => 5,
            HullClass.HeavyAssaultCruiser or HullClass.HeavyInterdictionCruiser => 5,
            HullClass.Cruiser or HullClass.ForceRecon or HullClass.CombatReconShip => 3,
            HullClass.Destroyer or HullClass.Interdictor or HullClass.AssaultFrigate => 2,
            HullClass.Frigate or HullClass.Interceptor => 1,
            _ => .5,
        };

    public static PvpRole Role(double weaponRange, double tackleRange, bool electronicWarfare = false) =>
        electronicWarfare ? PvpRole.ElectronicWarfare
        : weaponRange >= KitingRange ? PvpRole.Kiter
        : tackleRange > 0 ? PvpRole.Tackle
        : PvpRole.Brawler;

    public static double Range(double weaponRange, double lockRange, double tackleRange, PvpRole role, bool alliedTackle = false) =>
        Band(weaponRange, lockRange, tackleRange, role, alliedTackle).Desired;

    public static PvpRangeBand Band(double weaponRange, double lockRange, double tackleRange, PvpRole role, bool alliedTackle = false)
    {
        double range = Math.Min(weaponRange, lockRange);
        bool maintainTackle = tackleRange > 0 && (role is PvpRole.Tackle or PvpRole.Brawler || !alliedTackle);
        if (maintainTackle)
        {
            range = Math.Min(range, tackleRange * TackleRangeMargin);
        }
        range = Math.Clamp(range, MinimumRange, 100_000);
        return new(range * (1 - RangeToleranceFraction), range, range * (1 + RangeToleranceFraction));
    }

    public static double PursuitDistance(SpacePosition separation, SpacePosition targetVelocity, double approachSpeed)
    {
        double current = separation.Length;
        if (!double.IsFinite(approachSpeed) || approachSpeed <= 0)
        {
            return current;
        }
        // A bounded horizon and acceleration allowance avoid treating a faster hull as an instant intercept.
        double projected = (separation + targetVelocity * PursuitSeconds).Length;
        return Math.Min(current, Math.Max(0, projected - approachSpeed * PursuitSeconds * AccelerationAllowance));
    }

    public static bool Exhausted(double capacitorFraction, IReadOnlyList<RattingWeapon> weapons) =>
        !weapons.Any(source => source.AvailableShots > 0)
        || capacitorFraction < EmergencyCapacitorFraction
            && weapons.Where(source => source.AvailableShots > 0).All(source => source.Weapon.CapacitorNeed > 0);

    public static bool Propel(double range, double desiredRange, double capacitorFraction, bool retreating) =>
        retreating
        || capacitorFraction >= PropulsionReserveFraction && Math.Abs(range - desiredRange) > desiredRange * RangeToleranceFraction;

    public static double Application(RattingWeapon source, double distance, double angular, double signature, double speed) =>
        source.AvailableShots <= 0 || source.Weapon.CycleMilliseconds <= 0 ? 0
        : source.Missile is { } missile
            ? distance <= source.Weapon.Optimal ? MissileRules.Application(missile, signature, speed)
                : 0
        : CombatDamage.HitChance(
            distance,
            angular,
            signature,
            source.Weapon.Optimal,
            source.Weapon.Falloff,
            source.Weapon.Tracking,
            source.Weapon.SignatureResolution
        );

    public static PvpDecision Select(
        SpacePosition position,
        SpacePosition velocity,
        IReadOnlyList<RattingWeapon> weapons,
        IReadOnlyList<PilotSighting> opponents,
        double friendlyStrength,
        long currentTarget,
        long committedAt,
        long elapsed,
        long reported = 0,
        bool cautious = true,
        double approachSpeed = 0
    )
    {
        if (!double.IsFinite(friendlyStrength) || friendlyStrength <= 0)
        {
            return new(0, 0, "Visible opposition exceeds the patrol's strength.");
        }
        // Nearby neutrals are not automatically one hostile fleet. Use only visible affiliations.
        var threats = opponents.GroupBy(Formation).ToDictionary(group => group.Key, group => group.Sum(contact => Threat(contact.Hull)));
        double maximumThreat = friendlyStrength * (cautious ? MaximumThreatRatio : MaximumThreatRatio * 1.5);
        double fullDamage = weapons
            .Where(source => source.AvailableShots > 0 && source.Weapon.CycleMilliseconds > 0)
            .Sum(source => source.Weapon.Damage.Total * 1000 / source.Weapon.CycleMilliseconds);
        if (fullDamage <= 0 || !double.IsFinite(fullDamage))
        {
            return new(0, 0, "No usable damage source remains.");
        }
        PvpDecision Score(PilotSighting contact)
        {
            if (threats[Formation(contact)] > maximumThreat)
            {
                return new(contact.ShipId, 0, "The target's visible group exceeds the patrol's strength.");
            }
            double distance = position.DistanceTo(contact.Position);
            double reachable = PursuitDistance(contact.Position - position, contact.Velocity, approachSpeed);
            double angular = CombatDamage.AngularVelocity(contact.Position - position, contact.Velocity - velocity);
            // Visible hull size is a conservative signature estimate; hidden fitted attributes are not inspected.
            double signature = Math.Max(1, contact.Radius);
            double damage = weapons.Sum(source =>
                Application(
                    source,
                    Math.Min(distance, Math.Max(reachable, source.Weapon.Optimal + source.Weapon.Falloff * .4)),
                    angular,
                    signature,
                    contact.Velocity.Length
                )
                * source.Weapon.Damage.Total
                * 1000
                / Math.Max(1, source.Weapon.CycleMilliseconds)
            );
            return new(
                contact.ShipId,
                damage / Math.Max(1, Threat(contact.Hull)) * (contact.ShipId == reported ? 2 : 1),
                "Engaging a visible target with usable damage application."
            );
        }
        var scored = opponents
            .Select(Score)
            .Where(choice => choice.Score >= fullDamage * MinimumApplication)
            .OrderByDescending(choice => choice.Score)
            .ThenBy(choice => choice.TargetId)
            .ToArray();
        var best = scored.FirstOrDefault();
        if (best is null)
        {
            return new(0, 0, "No visible target has useful damage application.");
        }
        var primary = scored.FirstOrDefault(choice => choice.TargetId == reported);
        if (primary is not null)
        {
            return primary;
        }
        var retained = scored.FirstOrDefault(choice => choice.TargetId == currentTarget);
        return retained is not null && (elapsed - committedAt < CommitmentTicks || retained.Score >= best.Score * TargetRetention)
            ? retained
            : best;
    }
}
