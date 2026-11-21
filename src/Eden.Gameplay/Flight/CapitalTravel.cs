using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Flight;

public sealed record CapitalJumpRequest(long EmitterId, long BeaconId, int SystemId);

public sealed record JumpTimers(long ActivationUntil, long FatigueUntil, long UpdatedAt);

public sealed record CynosuralField(
    long PilotId,
    long ShipId,
    long ModuleId,
    long FleetId,
    int SystemId,
    long EndsAt,
    StaticSpaceObject Beacon
);

public sealed record CynoActivation(CynosuralField Field, IReadOnlyList<InventoryChange> FuelChanges, double Capacitor);

public sealed record CapitalJump(DockedPilot Pilot, JumpTimers Timers, double Capacitor, IReadOnlyList<InventoryChange> Changes);

public interface IPilotCapitalTravel
{
    CynoActivation Light(long pilotId, long shipId, long moduleId, double speed);
    void CheckJump(long pilotId, long shipId, CapitalJumpRequest request);
    CapitalJump Jump(long pilotId, long shipId, CapitalJumpRequest request);
    JumpTimers? Timers(long pilotId);
    CynosuralField? Field(long pilotId);
    IReadOnlyList<CynosuralField> Fields(int systemId);
    IReadOnlyList<CynosuralField> FleetFields(long pilotId);
    IReadOnlyDictionary<int, int> BeaconCounts();
}

public static class CapitalTravelRules
{
    // Build 3662168 client/types, client/typedogma and jumpFatigue/const.pyj.
    public const int IndustrialGenerator = 52694;
    public const int IndustrialField = 52696;
    public const int GeneratorGroup = 658;
    public const int FieldGroup = 2020;
    public const int IndustrialCycleMilliseconds = 600_000;
    public const int EconomizerGroup = 1299;
    public const int CynosuralFieldTheory = 21603;
    public const int JumpDriveOperation = 3456;
    public const int JumpDriveCalibration = 21611;
    public const int JumpFuelConservation = 21610;
    public const double LightYearMeters = 9_460_730_472_580_800;

    public static int Isotopes(double distance, double rate)
    {
        double quantity = distance * rate;
        if (!double.IsFinite(quantity) || quantity is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("Invalid jump fuel requirements.");
        }
        return Math.Max(1, (int)quantity);
    }

    public static JumpTimers AfterJump(long now, long previousFatigue, double distance, double multiplier)
    {
        double factor = 1 + distance * multiplier;
        double remaining = Math.Max(0, previousFatigue - now) / (double)TimeSpan.TicksPerSecond;
        long activation = (long)Math.Min(1_800, Math.Max(60 * factor, remaining / 10));
        long fatigue = (long)Math.Min(18_000, Math.Max(600 * factor, remaining * factor));
        return new(now + activation * TimeSpan.TicksPerSecond, now + fatigue * TimeSpan.TicksPerSecond, now);
    }
}
