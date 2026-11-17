using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public sealed record PvpDecision(long TargetId, double Score, string Reason);

public sealed record PvpRangeBand(double Minimum, double Desired, double Maximum);

public enum PvpMovementKind
{
    Follow,
    Orbit,
    Spiral,
    MatchVelocity,
    Evade,
}

public sealed record PvpMovement(PvpMovementKind Kind, SpacePosition? Direction = null, double SpeedFraction = 1);

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
    private const double TrackingApplicationThreshold = .5;
    private const double SpiralClosingFraction = .8;
    private const double SpiralTransverseFraction = .6;
    private const double EvasionHorizonSeconds = 2;
    private const double DroneRangeMargin = .75;
    private const double MissileRangeMargin = .8;
    private const double FalloffRangeFraction = .4;
    private const double RangeDamageFraction = .5;
    public static readonly long CommitmentTicks = TimeSpan.FromSeconds(8).Ticks;

    public const double UnratedHullThreat = .5;

    // Hull classes are uncertain threat estimates, not access to an opponent's fit or resources.
    public static double Threat(HullClass hull) =>
        hull switch
        {
            HullClass.Battleship => 8,
            HullClass.CombatBattlecruiser or HullClass.AttackBattlecruiser => 5,
            HullClass.HeavyAssaultCruiser or HullClass.HeavyInterdictionCruiser => 5,
            HullClass.StrategicCruiser or HullClass.CommandShip => 6,
            HullClass.Marauder or HullClass.BlackOps or HullClass.EliteBattleship => 10,
            HullClass.Dreadnought
            or HullClass.Carrier
            or HullClass.ForceAuxiliary
            or HullClass.LancerDreadnought
            or HullClass.CommandCarrier => 32,
            HullClass.Supercarrier => 64,
            HullClass.Titan => 128,
            HullClass.Cruiser or HullClass.ForceRecon or HullClass.CombatReconShip or HullClass.Logistics => 3,
            HullClass.TacticalDestroyer or HullClass.CommandDestroyer => 3,
            HullClass.ElectronicAttack => 2,
            HullClass.Destroyer or HullClass.Interdictor or HullClass.AssaultFrigate => 2,
            HullClass.Frigate or HullClass.Interceptor or HullClass.LogisticsFrigate or HullClass.StealthBomber => 1,
            _ => UnratedHullThreat,
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
        bool maintainTackle =
            tackleRange > 0 && (role is PvpRole.Tackle or PvpRole.Brawler || !alliedTackle && role != PvpRole.ElectronicWarfare);
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

    public static double WeaponRange(IReadOnlyList<RattingWeapon> weapons, double lockRange, double fallback)
    {
        var guns = weapons
            .Where(source => source.AvailableShots > 0 && source.Weapon.CycleMilliseconds > 0 && source.DroneControlRange <= 0)
            .ToArray();
        if (guns.Length == 0)
        {
            double control = weapons
                .Where(source => source.AvailableShots > 0)
                .Select(source => source.DroneControlRange)
                .DefaultIfEmpty(0)
                .Max();
            return Math.Clamp(Math.Min(lockRange, control > 0 ? control * DroneRangeMargin : fallback), MinimumRange, 100_000);
        }
        double total = guns.Sum(Dps);
        double accumulated = 0;
        foreach (var source in guns.OrderBy(Reach))
        {
            accumulated += Dps(source);
            if (accumulated >= total * RangeDamageFraction)
            {
                double range = Reach(source);
                return Math.Clamp(Math.Min(lockRange, range), MinimumRange, 100_000);
            }
        }
        return Math.Clamp(Math.Min(lockRange, fallback), MinimumRange, 100_000);
    }

    private static double Dps(RattingWeapon source) =>
        source.AvailableShots > 0 && source.Weapon.CycleMilliseconds > 0
            ? source.Weapon.Damage.Total * 1000 / source.Weapon.CycleMilliseconds
            : 0;

    private static double Reach(RattingWeapon source) =>
        source.Missile is null
            ? source.Weapon.Optimal + source.Weapon.Falloff * FalloffRangeFraction
            : source.Weapon.Optimal * MissileRangeMargin;

    public static PvpMovement Movement(
        IReadOnlyList<RattingWeapon> weapons,
        SpacePosition separation,
        SpacePosition velocity,
        SpacePosition targetVelocity,
        double signature,
        double maximumSpeed,
        PvpRangeBand band,
        PvpRole role,
        double radii = 0,
        ObservedPvpWeapon opponentWeapon = ObservedPvpWeapon.Unknown
    )
    {
        double distance = separation.Length;
        double surfaceDistance = Math.Max(0, distance - radii);
        if (role == PvpRole.Kiter && (separation + (targetVelocity - velocity) * EvasionHorizonSeconds).Length - radii < band.Minimum)
        {
            return new(PvpMovementKind.Evade, distance > 0 ? separation.Unit * -1 : new(1, 0, 0));
        }
        if (role == PvpRole.Tackle && surfaceDistance > band.Maximum)
        {
            if (opponentWeapon is ObservedPvpWeapon.Missile or ObservedPvpWeapon.Drone)
            {
                return new(PvpMovementKind.Follow);
            }
            var radial = separation.Unit;
            var transverse = radial.Cross(new(0, 1, 0));
            if (transverse.Length == 0)
            {
                transverse = new(1, 0, 0);
            }
            return new(PvpMovementKind.Spiral, (radial * SpiralClosingFraction + transverse.Unit * SpiralTransverseFraction).Unit);
        }
        bool inBand = surfaceDistance >= band.Minimum && surfaceDistance <= band.Maximum;
        var turrets = weapons.Where(source => source.Missile is null && source.DroneControlRange <= 0).ToArray();
        double angular = CombatDamage.AngularVelocity(separation, targetVelocity - velocity);
        double stationary = turrets.Sum(source => Dps(source) * Application(source, surfaceDistance, 0, signature, targetVelocity.Length));
        double moving = turrets.Sum(source =>
            Dps(source) * Application(source, surfaceDistance, angular, signature, targetVelocity.Length)
        );
        if (inBand && role != PvpRole.Tackle && stationary > 0 && moving < stationary * TrackingApplicationThreshold)
        {
            return new(
                PvpMovementKind.MatchVelocity,
                targetVelocity.Unit,
                Math.Clamp(targetVelocity.Length / Math.Max(1, maximumSpeed), 0, 1)
            );
        }
        return new(
            surfaceDistance < KitingRange && role is PvpRole.Brawler or PvpRole.Tackle ? PvpMovementKind.Orbit : PvpMovementKind.Follow
        );
    }

    public static bool Propel(double range, double desiredRange, double capacitorFraction, bool retreating) =>
        retreating
        || capacitorFraction >= PropulsionReserveFraction && Math.Abs(range - desiredRange) > desiredRange * RangeToleranceFraction;

    public static double Application(RattingWeapon source, double distance, double angular, double signature, double speed) =>
        source.AvailableShots <= 0 || source.Weapon.CycleMilliseconds <= 0 ? 0
        : source.DroneControlRange > 0
            ? distance <= source.DroneControlRange && speed < source.DroneSpeed
                    ? CombatDamage.HitChance(
                        source.Weapon.Optimal,
                        Math.Max(speed, source.DroneOrbitSpeed) / Math.Max(1, source.Weapon.Optimal),
                        signature,
                        source.Weapon.Optimal,
                        source.Weapon.Falloff,
                        source.Weapon.Tracking,
                        source.Weapon.SignatureResolution
                    )
                : 0
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
        double approachSpeed = 0,
        double sourceRadius = 0,
        double supportRange = 0
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
        bool supportOnly = fullDamage <= 0 && supportRange > 0;
        if (!double.IsFinite(fullDamage) || fullDamage <= 0 && !supportOnly)
        {
            return new(0, 0, "No usable damage source remains.");
        }
        PvpDecision Score(PilotSighting contact)
        {
            if (threats[Formation(contact)] > maximumThreat)
            {
                return new(contact.ShipId, 0, "The target's visible group exceeds the patrol's strength.");
            }
            double radii = sourceRadius + contact.Radius;
            double distance = Math.Max(0, position.DistanceTo(contact.Position) - radii);
            double reachable = Math.Max(0, PursuitDistance(contact.Position - position, contact.Velocity, approachSpeed) - radii);
            if (supportOnly)
            {
                return new(
                    contact.ShipId,
                    reachable <= supportRange ? PvpSupport.Priority(contact.Hull) : 0,
                    "Supporting a visible engagement."
                );
            }
            double angular = CombatDamage.AngularVelocity(contact.Position - position, contact.Velocity - velocity);
            // Visible hull size is a conservative signature estimate; hidden fitted attributes are not inspected.
            double signature = Math.Max(1, contact.SignatureEstimate > 0 ? contact.SignatureEstimate : contact.Radius);
            double damage = weapons.Sum(source =>
                Application(
                    source,
                    Math.Min(distance, Math.Max(reachable, source.Weapon.Optimal + source.Weapon.Falloff * FalloffRangeFraction)),
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
            .Where(choice => choice.Score > 0 && choice.Score >= fullDamage * MinimumApplication)
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
