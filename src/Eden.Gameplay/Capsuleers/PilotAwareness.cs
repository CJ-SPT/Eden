using Eden.Gameplay.Docking;
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
    GateCamp? Camp = null,
    int? RegionalGroupId = null
)
{
    public double SignatureEstimate { get; init; }
    public ObservedPvpWeapon ObservedWeapon { get; init; }
    public bool CapacitorWeaponObserved { get; init; }
}

public sealed record BeltMemory(long BeltId, long Until);

public enum CivilianDanger
{
    Clear,
    Caution,
    Escape,
}

public static class PilotAwareness
{
    public static readonly long MemoryTicks = TimeSpan.FromMinutes(12).Ticks;

    public static bool Cautious(long pilot) => TravelerRoutineRules.Mix((ulong)pilot) % 3 != 0;

    public static int LookSeconds(long pilot) => 5 + (int)(TravelerRoutineRules.Mix((ulong)pilot) % 8);

    // Eden civilian policy in meters. Estimate a close pass over the pilot's next observation interval.
    private const double TackleRange = 24_000;
    private const double MinimumClosingSpeed = 100;
    private const int CautiousCampSize = 2;
    private const int BoldCampSize = 3;

    public static bool ArmedHull(HullClass hull) => PvpTactics.Threat(hull) > PvpTactics.UnratedHullThreat;

    public static CivilianDanger Assess(
        DockedPilot pilot,
        SpacePosition velocity,
        IReadOnlyList<PilotSighting> visible,
        int? regionalGroup,
        StaticSpaceObject? gate = null
    )
    {
        int armed = 0;
        foreach (var contact in visible)
        {
            if (
                contact.ShipId == pilot.ShipId
                || contact.PilotId == pilot.PilotId
                || PatrolRules.Friendly(pilot, contact, regionalGroup)
                || contact.Hull == HullClass.Frigate && contact.TypeId == (int)TechOneHullType.Venture
                || !ArmedHull(contact.Hull)
            )
            {
                continue;
            }
            armed++;
            var separation = contact.Position - pilot.Position!;
            bool tackle = contact.Hull is HullClass.Interceptor or HullClass.Interdictor or HullClass.HeavyInterdictionCruiser;
            double reach = TackleRange + pilot.Flight.ShipRadius + contact.Radius;
            if (
                tackle && separation.Length <= reach
                || tackle && gate is not null && contact.Position.DistanceTo(gate.Position) <= TackleRange + gate.Radius + contact.Radius
            )
            {
                return CivilianDanger.Escape;
            }
            var relative = contact.Velocity - velocity;
            var direction = separation.Unit;
            if (contact.Velocity.Dot(direction) <= -MinimumClosingSpeed && relative.Dot(direction) <= -MinimumClosingSpeed)
            {
                double closest = Math.Clamp(-separation.Dot(relative) / relative.Dot(relative), 0, LookSeconds(pilot.PilotId));
                if ((separation + relative * closest).Length <= reach)
                {
                    return CivilianDanger.Escape;
                }
            }
        }
        if (gate is not null && armed >= (Cautious(pilot.PilotId) ? CautiousCampSize : BoldCampSize))
        {
            return CivilianDanger.Escape;
        }
        return armed > 0 ? CivilianDanger.Caution : CivilianDanger.Clear;
    }

    public static StaticSpaceObject? NextMiningBelt(
        StaticSpaceObject[] fields,
        int rotation,
        BeltMemory[] strippedBelts,
        BeltMemory[] avoidedBelts,
        long elapsed,
        out long retryAt
    )
    {
        if (fields.Length == 0)
        {
            throw new InvalidDataException("Waiting for a known asteroid belt.");
        }
        retryAt = long.MaxValue;
        int start = (int)((uint)rotation % (uint)fields.Length);
        for (int index = 0; index < fields.Length; index++)
        {
            var field = fields[(start + index) % fields.Length];
            var stripped = Array.Find(strippedBelts, b => b.BeltId == field.Id && b.Until > elapsed);
            var danger = Array.Find(avoidedBelts, b => b.BeltId == field.Id && b.Until > elapsed);
            if (stripped is null && danger is null)
            {
                return field;
            }
            retryAt = Math.Min(retryAt, Math.Max(stripped?.Until ?? 0, danger?.Until ?? 0));
        }
        return null;
    }

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
