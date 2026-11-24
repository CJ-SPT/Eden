using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Flight;

public sealed record StaticSpaceObject(
    long Id,
    int TypeId,
    int GroupId,
    int CategoryId,
    int OwnerId,
    SpacePosition Position,
    float Radius,
    int? DestinationGateId = null,
    int? DestinationSystemId = null
)
{
    // Asteroid and celestial snapshots rarely need feature-specific metadata.
    // Keep it immutable so record copies can share the same details.
    private static readonly OptionalDetails s_emptyDetails = new();
    private readonly OptionalDetails? _details;
    private OptionalDetails Details => _details ?? s_emptyDetails;

    public IncursionBeacon? IncursionBeacon
    {
        get => _details?.IncursionBeacon;
        init =>
            _details = value is null && _details?.IncursionBeacon is null ? _details : (Details with { IncursionBeacon = value }).OrNull();
    }

    public Eden.Gameplay.Exploration.WormholeContact? Wormhole
    {
        get => _details?.Wormhole;
        init => _details = value is null && _details?.Wormhole is null ? _details : (Details with { Wormhole = value }).OrNull();
    }

    public Eden.Gameplay.Exploration.AbyssalContact? Abyssal
    {
        get => _details?.Abyssal;
        init => _details = value is null && _details?.Abyssal is null ? _details : (Details with { Abyssal = value }).OrNull();
    }

    public SpacePosition? AccelerationGateDirection
    {
        get => _details?.AccelerationGateDirection;
        init =>
            _details =
                value is null && _details?.AccelerationGateDirection is null
                    ? _details
                    : (Details with { AccelerationGateDirection = value }).OrNull();
    }

    public int? HackingState
    {
        get => _details?.HackingState;
        init => _details = value is null && _details?.HackingState is null ? _details : (Details with { HackingState = value }).OrNull();
    }

    public double? InterdictionRadius
    {
        get => _details?.InterdictionRadius;
        init =>
            _details =
                value is null && _details?.InterdictionRadius is null ? _details : (Details with { InterdictionRadius = value }).OrNull();
    }

    public CynoDeployableActivation? CynoDeployable
    {
        get => _details?.CynoDeployable;
        init =>
            _details = value is null && _details?.CynoDeployable is null ? _details : (Details with { CynoDeployable = value }).OrNull();
    }

    public Structures.JumpBridgeContact? JumpBridge
    {
        get => _details?.JumpBridge;
        init => _details = value is null && _details?.JumpBridge is null ? _details : (Details with { JumpBridge = value }).OrNull();
    }

    public bool Solid { get; init; } = true;
    public StaticCollisionGeometry? Collision { get; init; }
    public bool IsGlobal { get; init; } = true;
    public string? Name { get; init; }
    public int? AsteroidBeltId { get; init; }
    public NpcStandingThresholds? StandingThresholds
    {
        get => _details?.StandingThresholds;
        init =>
            _details =
                value is null && _details?.StandingThresholds is null ? _details : (Details with { StandingThresholds = value }).OrNull();
    }

    public long? AgentId
    {
        get => _details?.AgentId;
        init => _details = value is null && _details?.AgentId is null ? _details : (Details with { AgentId = value }).OrNull();
    }

    public SpaceContainerState? Container
    {
        get => _details?.Container;
        init => _details = value is null && _details?.Container is null ? _details : (Details with { Container = value }).OrNull();
    }

    public Structures.StructureContact? Structure
    {
        get => _details?.Structure;
        init => _details = value is null && _details?.Structure is null ? _details : (Details with { Structure = value }).OrNull();
    }

    public Structures.MoonMiningPoint? MoonMiningPoint
    {
        get => _details?.MoonMiningPoint;
        init =>
            _details = value is null && _details?.MoonMiningPoint is null ? _details : (Details with { MoonMiningPoint = value }).OrNull();
    }

    public FactionWarfare.MilitiaCaptureBracket? MilitiaCapture
    {
        get => _details?.MilitiaCapture;
        init =>
            _details = value is null && _details?.MilitiaCapture is null ? _details : (Details with { MilitiaCapture = value }).OrNull();
    }

    public FactionWarfare.MilitiaBattlefieldCapture? BattlefieldCapture
    {
        get => _details?.BattlefieldCapture;
        init =>
            _details =
                value is null && _details?.BattlefieldCapture is null ? _details : (Details with { BattlefieldCapture = value }).OrNull();
    }

    public int? BattlefieldScore
    {
        get => _details?.BattlefieldScore;
        init =>
            _details =
                value is null && _details?.BattlefieldScore is null ? _details : (Details with { BattlefieldScore = value }).OrNull();
    }

    public Sovereignty.SovereigntyHub? Sovereignty
    {
        get => _details?.Sovereignty;
        init => _details = value is null && _details?.Sovereignty is null ? _details : (Details with { Sovereignty = value }).OrNull();
    }

    public Sovereignty.SovereigntyCommandNode? CommandNode
    {
        get => _details?.CommandNode;
        init => _details = value is null && _details?.CommandNode is null ? _details : (Details with { CommandNode = value }).OrNull();
    }

    public Sovereignty.SovereigntySkyhook? Skyhook
    {
        get => _details?.Skyhook;
        init => _details = value is null && _details?.Skyhook is null ? _details : (Details with { Skyhook = value }).OrNull();
    }

    private sealed record OptionalDetails(
        IncursionBeacon? IncursionBeacon = null,
        Eden.Gameplay.Exploration.WormholeContact? Wormhole = null,
        Eden.Gameplay.Exploration.AbyssalContact? Abyssal = null,
        SpacePosition? AccelerationGateDirection = null,
        int? HackingState = null,
        double? InterdictionRadius = null,
        CynoDeployableActivation? CynoDeployable = null,
        Structures.JumpBridgeContact? JumpBridge = null,
        NpcStandingThresholds? StandingThresholds = null,
        long? AgentId = null,
        SpaceContainerState? Container = null,
        Structures.StructureContact? Structure = null,
        Structures.MoonMiningPoint? MoonMiningPoint = null,
        FactionWarfare.MilitiaCaptureBracket? MilitiaCapture = null,
        FactionWarfare.MilitiaBattlefieldCapture? BattlefieldCapture = null,
        int? BattlefieldScore = null,
        Sovereignty.SovereigntyHub? Sovereignty = null,
        Sovereignty.SovereigntyCommandNode? CommandNode = null,
        Sovereignty.SovereigntySkyhook? Skyhook = null
    )
    {
        internal OptionalDetails? OrNull() => this == s_emptyDetails ? null : this;
    }
}

public sealed record IncursionBeacon(int DungeonId, int DungeonNameId, int ArchetypeId);

public interface ISystemObjects
{
    FlightSystem GetSystem(int systemId);
    IReadOnlyList<StaticSpaceObject> Load(int systemId);
}
