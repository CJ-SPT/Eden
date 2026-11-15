using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Exploration;

public enum AbyssalStatus
{
    Deployed,
    Active,
    Completed,
    Failed,
}

public enum AbyssalWeather
{
    Dark,
    Electrical,
    Exotic,
    Firestorm,
    Gamma,
}

// Build 3662168 abyss.game_mode.GameModeID, distinct from abyss.const content IDs.
public enum AbyssalMode
{
    Cruiser = 1,
    Frigates = 2,
    Destroyers = 3,
}

public sealed record AbyssalRun(
    long Id,
    int SystemId,
    int Room,
    int Tier,
    AbyssalWeather Weather,
    AbyssalStatus Status,
    long ExpiresAt,
    long ConduitId,
    int NebulaGraphicId
);

public sealed record AbyssalTransition(DockedPilot Pilot, AbyssalRun Run, IReadOnlyList<InventoryChange> Items);

public sealed record AbyssalScene(int DungeonId, int RoomId, int NebulaGraphicId);

public sealed record AbyssalContact(long RunId, int FilamentTypeId, AbyssalMode Mode, bool Open, bool Trace, bool Environment = false);

public interface IAbyssalDeadspace
{
    AbyssalRun? Read(long pilotId);
    AbyssalTransition Deploy(long pilotId, long shipId, long filamentId, AbyssalMode mode);
    AbyssalTransition Activate(long pilotId, long shipId, long gateId);
    IReadOnlyList<StaticSpaceObject> Objects(int systemId);
    void Advance();
}

public static class AbyssalRules
{
    // Build 3662168 client/dungeons and client/graphicids; original environment tracker consumes these identities.
    public static AbyssalScene Scene(int room) =>
        room switch
        {
            0 => new(6204, 24754, 22209),
            1 => new(6207, 24753, 22210),
            2 => new(6205, 24751, 22211),
            _ => throw new ArgumentOutOfRangeException(nameof(room)),
        };

    public const int RoomCount = 3;
    public const long Duration = 20 * TimeSpan.TicksPerMinute;
    public const long TraceDuration = 30 * TimeSpan.TicksPerMinute;
    public const double ActivationRange = 2500;
    public const double DeploymentClearance = 1_000_000;
    public const double BoundaryRadius = 75_000;
    public const int FirstSystem = 32_000_000;
    public const int LastSystem = 32_999_999;
    public const int TranquilDarkFilament = 56132;
    public const int TransferConduit = 47685;
    public const int OriginConduit = 47686;
    public const int TraceType = 47458;
    public const int CooperativeTraceType = 48964;
    public const int CooperativeCacheType = 49689;
    public const int CooperativeCacheWreckType = 49692;
    public const int CacheType = 47951;
    public const int CacheWreckType = 48120;
    public const int CruiserGroup = 26;
    public const long FirstRunId = 980_000_000;
    public const long FirstObjectId = 981_000_000;
    public const int ObjectsPerRun = 16;
    public const double RoomSeparation = 10_000_000;

    public static bool IsSystem(int systemId) => systemId is >= FirstSystem and <= LastSystem;

    public static SpacePosition Center(int room) => new(room * RoomSeparation, 0, 0);

    public static SpacePosition ConduitPosition(int room) => Center(room) + new SpacePosition(40_000, 0, 0);

    public static long Conduit(long runId, int room) => checked(FirstObjectId + (runId - FirstRunId) * ObjectsPerRun + room);

    public static long Trace(long runId) => Conduit(runId, ObjectsPerRun - 1);

    public static void RequireActive(AbyssalStatus status, long deadline, long now)
    {
        if (status != AbyssalStatus.Active || now >= deadline)
        {
            throw new InvalidDataException("This Abyssal run is no longer active.");
        }
    }

    public static void RequireConduit(SpacePosition position, int room, bool hostilesRemain)
    {
        if (position.DistanceTo(ConduitPosition(room)) > ActivationRange)
        {
            throw new InvalidDataException("Approach the Abyssal conduit before activating it.");
        }
        if (hostilesRemain)
        {
            throw new InvalidDataException("Destroy the remaining hostiles before using the Abyssal conduit.");
        }
    }
}
