using Eden.Protocol.Docking;

namespace Eden.Protocol.Combat;

public sealed record NpcContact(long Id, int TypeId, int GroupId, int CategoryId, int CorporationId, string Name, int SystemId,
    SpacePosition Position, double Mass, float Radius, float MaxSpeed, float Agility, ShipHealth Health, NpcWeaponContact? Weapon = null,
    NpcNavigationContact? Navigation = null);

public enum NpcNavigationState
{
    Idle = 0,
    Pursuing = 1,
    Returning = 2,
}

public sealed record NpcNavigationContact(SpacePosition Velocity, double SpeedFraction, long TargetId, double FollowRange,
    SpacePosition? Destination = null);

public interface INpcEncounters
{
    IReadOnlyList<NpcContact> List(int systemId, long now);
    bool WasDestroyed(long npcId);
}

public static class NpcEncounterDefinitions
{
    // Eden's first encounter placement policy, rather than catalog spawn data.
    // Catalog Guristas Rookies guard Perimeter's belt anchors. Detection and
    // pursuit boundaries are Eden encounter policy, not catalog AI parameters.
    public const int PerimeterSystem = 30000144;
    public const int GuristasRookieType = 30380;
    public const int GuristasCorporation = 1000127;
    public const long FirstNpcId = 4_000_000_000_000;
    public const double AnchorOffset = 3500;
    public const double VisibilityRange = 250_000;
    public const int RespawnSeconds = 600;
    public const double ChaseLeash = 50_000;
    public const double DetectionRange = 20_000;
    public const double ArrivalTolerance = 25;
    public const double ArrivalSpeed = 1;
    public const double InertiaMassScale = 1_000_000;
    public const double MaximumMovementStepSeconds = 1;
    // This type has no positive agility override. Eden supplies a positive
    // free-ball inertia instead of dividing by the catalog's zero default.
    public const float DefaultAgility = 1;
}
