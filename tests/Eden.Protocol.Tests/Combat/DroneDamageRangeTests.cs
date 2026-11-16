using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed class DroneDamageRangeTests
{
    [Fact]
    public void DroneAtOptimalFromTargetSurfaceAppliesFullHitQuality()
    {
        DamageVector resonance = new(1, 1, 1, 1);
        HullDefense defense = new(100, 100, 100, 100000, resonance, resonance, resonance);
        DroneProfile profile = new(2486, 5, 5, 1000000, 10, 1, 3000, 500, 1000, 1000, 1000, 1, 40, 4000, new(0, 0, 10, 0), defense);
        DroneContact drone = new(
            10,
            1,
            2,
            1000044,
            30000144,
            profile,
            DroneActivity.Fighting,
            20,
            SpacePosition.Zero,
            SpacePosition.Zero,
            new(10, defense, defense.Undamaged)
        );
        const double targetRadius = 1000;
        const double roll = .75;
        var center = new SpacePosition(profile.Radius + profile.Optimal + targetRadius, 0, 0);
        double surface = center.Length - targetRadius - profile.Radius;
        Assert.Equal(
            1,
            CombatDamage.HitChance(surface, 0, 100, profile.Optimal, profile.Falloff, profile.Tracking, profile.SignatureReference)
        );
        Assert.True(CombatDamage.ShotQuality(1, roll) > 0);
        Assert.Equal(
            profile.Damage.Total * CombatDamage.ShotQuality(1, roll),
            DroneRules.Shot(drone, center, SpacePosition.Zero, 100, roll, targetRadius).Total
        );
    }
}
