using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public sealed record PilotSighting(
    long PilotId,
    long ShipId,
    int TypeId,
    HullClass Hull,
    int CorporationId,
    int? AllianceId,
    SpacePosition Position,
    SpacePosition Velocity,
    float Radius,
    GateCamp? Camp = null
);

public sealed record BeltMemory(long BeltId, long Until);

public static class PilotAwareness
{
    public static readonly long MemoryTicks = TimeSpan.FromMinutes(12).Ticks;

    public static bool Cautious(long pilot) => TravelerRoutineRules.Mix((ulong)pilot) % 3 != 0;

    public static int LookSeconds(long pilot) => 5 + (int)(TravelerRoutineRules.Mix((ulong)pilot) % 8);

    public static bool ArmedHull(HullClass hull) =>
        hull
            is HullClass.Frigate
                or HullClass.Destroyer
                or HullClass.Cruiser
                or HullClass.Battleship
                or HullClass.CombatBattlecruiser
                or HullClass.AttackBattlecruiser
                or HullClass.Interceptor
                or HullClass.Interdictor
                or HullClass.HeavyInterdictionCruiser
                or HullClass.HeavyAssaultCruiser
                or HullClass.AssaultFrigate
                or HullClass.ForceRecon
                or HullClass.CombatReconShip;

    public static BeltMemory[] Remember(BeltMemory[] memory, long belt, long elapsed) =>
        memory.Where(m => m.Until > elapsed && m.BeltId != belt).Prepend(new(belt, checked(elapsed + MemoryTicks))).Take(6).ToArray();

    public static IEnumerable<long> Circuit(
        IEnumerable<long> belts,
        long pilot,
        long sequence,
        IReadOnlyList<BeltMemory> previous,
        long elapsed
    ) =>
        belts
            .Where(b => !previous.Any(m => m.BeltId == b && m.Until > elapsed))
            .OrderBy(b => TravelerRoutineRules.Mix((ulong)b ^ (ulong)pilot) % 5)
            .ThenBy(b => TravelerRoutineRules.Mix((ulong)b ^ (ulong)sequence ^ (ulong)pilot));

    public static double FightingRange(IReadOnlyList<RattingWeapon> weapons, double lockRange, double fallback)
    {
        double range = double.MaxValue;
        foreach (var weapon in weapons)
        {
            if (weapon.AvailableShots > 0)
            {
                double reach = weapon.Missile is null ? weapon.Weapon.Optimal + .4 * weapon.Weapon.Falloff : weapon.Weapon.Optimal * .8;
                if (reach > 0)
                {
                    range = Math.Min(range, reach);
                }
            }
        }
        return Math.Clamp(Math.Min(lockRange, range == double.MaxValue ? fallback : range), 500, 100_000);
    }
}
