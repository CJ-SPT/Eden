using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Mining;

public interface IMiningPollutionClouds
{
    void Advance(long now);
}

public static class MiningPollutionRules
{
    public const int TypeId = 11546;
    public const int GroupId = 335;
    public const int CategoryId = 11;
    public const long FirstCloudId = 14_000_000_000_000;

    public static double Chance(double oreChance, double skillBonusPerLevel, int level) =>
        Math.Clamp(oreChance * (1 + level * skillBonusPerLevel / 100), 0, 1);

    public static bool Contains(SpacePosition center, double radius, SpacePosition target) => center.DistanceTo(target) <= radius;

    public static long NextPulse(long started, long interval, long now) => checked(started + ((now - started) / interval + 1) * interval);
}

public sealed record MiningPollutionProfile(float Radius, long Interval, long Lifetime, DamageVector Damage);
