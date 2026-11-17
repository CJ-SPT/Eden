using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;

namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerModuleBudget(long ModuleId, int HeatedCycles = 0, long AmmoCheckAt = 0);

public sealed record PvpAmmunitionOption(long ItemId, int TypeId, int Quantity, CombatWeapon Weapon);

public static class PvpResources
{
    public const int MaximumHeatedCycles = 3;
    public const double MaximumRackFraction = .45;
    public const double MaximumModuleDamageFraction = .35;
    public const double AmmunitionImprovement = 1.25;
    public const double AmmunitionHorizonSeconds = 30;
    public static readonly long AmmunitionCheckTicks = TimeSpan.FromSeconds(AmmunitionHorizonSeconds).Ticks;

    public static long Ammunition(
        CombatWeapon current,
        bool loaded,
        IReadOnlyList<PvpAmmunitionOption> choices,
        double distance,
        double angular,
        double signature,
        double reloadMilliseconds
    )
    {
        double Score(CombatWeapon weapon) =>
            weapon.CycleMilliseconds <= 0
                ? 0
                : weapon.Damage.Total
                    * CombatDamage.HitChance(
                        distance,
                        angular,
                        signature,
                        weapon.Optimal,
                        weapon.Falloff,
                        weapon.Tracking,
                        weapon.SignatureResolution
                    )
                    / weapon.CycleMilliseconds;
        double remaining = Math.Clamp(1 - reloadMilliseconds / (AmmunitionHorizonSeconds * 1000), 0, 1);
        var best = choices
            .Where(choice => choice.Quantity > 0 && (!loaded || choice.TypeId != current.ChargeTypeId))
            .Select(choice => new PvpDecision(
                choice.ItemId,
                Score(choice.Weapon) * (loaded ? remaining : 1),
                "Selecting owned ammunition."
            ))
            .Where(choice => double.IsFinite(choice.Score) && choice.Score > 0)
            .OrderByDescending(choice => choice.Score)
            .ThenBy(choice => choice.TargetId)
            .FirstOrDefault();
        return best is not null && (!loaded || best.Score > Score(current) * AmmunitionImprovement) ? best.TargetId : 0;
    }

    public static bool Heat(
        DogmaEffect effect,
        int cycles,
        ShipHeatSnapshot heat,
        long module,
        bool emergency,
        double distance,
        double normalRange,
        double heatedRange
    )
    {
        if (cycles >= MaximumHeatedCycles || heat.Modules.SingleOrDefault(item => item.ItemId == module) is not { Online: true } item)
        {
            return false;
        }
        var rack = heat.Racks.SingleOrDefault(value => value.Profile.Rack == item.Rack);
        if (
            rack is null
            || rack.Profile.Capacity <= 0
            || rack.Heat / rack.Profile.Capacity >= MaximumRackFraction
            || heat.Modules.Where(value => value.Rack == item.Rack)
                .Any(value =>
                    value.Condition.BurnedOut || value.Condition.Damage / value.Condition.HitPoints >= MaximumModuleDamageFraction
                )
            || item.Condition.Damage + item.Damage * 2 >= item.Condition.HitPoints
        )
        {
            return false;
        }
        return effect switch
        {
            DogmaEffect.WarpDisruptor or DogmaEffect.WarpScrambler => distance > normalRange && distance <= heatedRange,
            DogmaEffect.LaserFired or DogmaEffect.ProjectileFired or DogmaEffect.UseMissiles => emergency,
            _ => false,
        };
    }
}
