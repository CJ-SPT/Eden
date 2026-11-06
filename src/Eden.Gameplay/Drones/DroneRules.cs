using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Drones;

public static class DroneRules
{
    public const int CombatDroneGroup = 100;
    public const int InitialHullType = 16229; // Brutix, build 3662168 client/types.
    public const int MaximumDrones = 5;
    public const double LightBandwidth = 5;
    public const double MediumBandwidth = 10;
    public const double BaseControlRange = 20_000;
    public const double RecallRange = 2500;
    public const double LaunchOffset = 100;

    // Pinned client skill attributes: 292 (damage), 294 (optimal), 459 (control
    // range), 2603 (navigation), and 327/335/337 (hull/armor/shield durability).
    private const double OperationDamagePerLevel = .05;
    private const double InterfacingDamagePerLevel = .1;
    private const double SpecializationDamagePerLevel = .02;
    private const double NavigationSpeedPerLevel = .05;
    private const double DurabilityPerLevel = .05;
    private const double SharpshootingOptimalPerLevel = .05;
    private const double AvionicsRangePerLevel = 5000;
    private const double AdvancedAvionicsRangePerLevel = 3000;

    // Initial Eden policy; Tranquility's private NPC target-selection AI is not available.
    public const double NpcRetaliationChance = .25;

    public static double OwnerAccuracyRoll(double roll) => (roll - NpcRetaliationChance) / (1 - NpcRetaliationChance);

    public static DroneRetaliation Retaliation(
        DroneContact drone,
        SpacePosition source,
        SpacePosition velocity,
        double signature,
        DroneNpcWeapon weapon,
        double roll
    )
    {
        double chance = CombatDamage.HitChance(
            drone.Position.DistanceTo(source),
            CombatDamage.AngularVelocity(drone.Position - source, drone.Velocity - velocity),
            signature,
            weapon.Optimal,
            weapon.Falloff,
            weapon.Tracking,
            weapon.SignatureReference
        );
        double quality = CombatDamage.ShotQuality(chance, roll / NpcRetaliationChance);
        return new(CombatDamage.Apply(drone.Health.Remaining, drone.Profile.Defense, weapon.Damage.Scale(quality)), quality);
    }

    public static double ControlRange(IReadOnlyDictionary<DroneSkill, int> skills) =>
        BaseControlRange
        + AvionicsRangePerLevel * skills.GetValueOrDefault(DroneSkill.DroneAvionics)
        + AdvancedAvionicsRangePerLevel * skills.GetValueOrDefault(DroneSkill.AdvancedDroneAvionics);

    public static void CheckLaunch(int active, double usedBandwidth, int count, double bandwidth, int skill, double availableBandwidth)
    {
        if (count <= 0 || active + count > Math.Min(MaximumDrones, skill))
        {
            throw new InvalidDataException("The Drones skill and five-drone control limit do not allow this launch.");
        }
        if (!double.IsFinite(availableBandwidth) || usedBandwidth + bandwidth > availableBandwidth)
        {
            throw new InvalidDataException("The ship does not have enough drone bandwidth.");
        }
    }

    public static DroneProfile ApplySkills(
        DroneProfile profile,
        IReadOnlyDictionary<DroneSkill, int> skills,
        IReadOnlySet<int> requiredSkills
    )
    {
        int operation = skills.GetValueOrDefault(
            profile.Bandwidth == LightBandwidth ? DroneSkill.LightDroneOperation : DroneSkill.MediumDroneOperation
        );
        double damage =
            (1 + operation * OperationDamagePerLevel)
            * (1 + skills.GetValueOrDefault(DroneSkill.DroneInterfacing) * InterfacingDamagePerLevel);
        foreach (
            var skill in new[]
            {
                DroneSkill.AmarrDroneSpecialization,
                DroneSkill.MinmatarDroneSpecialization,
                DroneSkill.GallenteDroneSpecialization,
                DroneSkill.CaldariDroneSpecialization,
            }
        )
        {
            if (requiredSkills.Contains((int)skill))
            {
                damage *= 1 + skills.GetValueOrDefault(skill) * SpecializationDamagePerLevel;
            }
        }
        double durability = 1 + skills.GetValueOrDefault(DroneSkill.DroneDurability) * DurabilityPerLevel;
        return profile with
        {
            Damage = profile.Damage.Scale(damage),
            MaximumSpeed =
                profile.MaximumSpeed * (float)(1 + skills.GetValueOrDefault(DroneSkill.DroneNavigation) * NavigationSpeedPerLevel),
            Optimal = profile.Optimal * (1 + skills.GetValueOrDefault(DroneSkill.DroneSharpshooting) * SharpshootingOptimalPerLevel),
            Defense = profile.Defense with
            {
                Shield = profile.Defense.Shield * durability,
                Armor = profile.Defense.Armor * durability,
                Structure = profile.Defense.Structure * durability,
            },
        };
    }

    public static DroneContact Move(DroneContact drone, SpacePosition target, float targetRadius, bool orbit, int stamp, double seconds)
    {
        var profile = drone.Profile;
        double range = orbit ? profile.OrbitRange : 0;
        double centerRange = range + targetRadius + profile.Radius;
        double distance = drone.Position.DistanceTo(target);
        bool orbiting = orbit && distance <= centerRange + profile.Optimal;
        float speed = orbiting ? profile.OrbitSpeed : profile.MaximumSpeed;
        var direction = orbiting
            ? OrbitCourse.Direction(drone.Id, stamp, drone.Position, target, centerRange)
            : (target - drone.Position).Unit;
        // Free-ball inertial integration uses Carbon's mass/agility time constant.
        double inertia = profile.Mass * profile.Agility / NpcEncounterDefinitions.InertiaMassScale;
        double decay = Math.Exp(-seconds / inertia);
        var desired = direction * speed;
        var velocity = desired + (drone.Velocity - desired) * decay;
        var position = drone.Position + desired * seconds + (drone.Velocity - desired) * (inertia * (1 - decay));
        return drone with { Position = position, Velocity = velocity, SpeedLimit = speed, Orbiting = orbiting };
    }

    public static DamageVector Shot(DroneContact drone, SpacePosition target, SpacePosition velocity, double signature, double roll)
    {
        var profile = drone.Profile;
        double chance = CombatDamage.HitChance(
            Math.Max(0, drone.Position.DistanceTo(target) - profile.Radius),
            CombatDamage.AngularVelocity(target - drone.Position, velocity - drone.Velocity),
            signature,
            profile.Optimal,
            profile.Falloff,
            profile.Tracking,
            profile.SignatureReference
        );
        return profile.Damage.Scale(CombatDamage.ShotQuality(chance, roll));
    }
}
