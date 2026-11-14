using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Flight;

internal sealed record StampedDestinyUpdate(int Stamp, DestinyCommand Command) : BlueModel
{
    public override object ToBlue() => new object?[] { Stamp, Command };
}

internal sealed record DestinyUpdatesNotification(IReadOnlyList<StampedDestinyUpdate> Updates) : PilotNotification("DoDestinyUpdate")
{
    public override object?[] Arguments => [new BlueList<StampedDestinyUpdate>(Updates), false, null];
}

internal abstract record DestinyCommand(string Name) : BlueModel
{
    internal sealed record DroneStateChanged(Eden.Protocol.Drones.DroneState State) : DestinyCommand("OnDroneStateChange")
    {
        protected override object?[] Arguments => State.Arguments;
    }

    internal sealed record DroneActivityChanged(long DroneId, DroneDelegation? Delegation) : DestinyCommand("OnDroneActivityChange")
    {
        // Pinned drone entry callback: droneID, activityID (character), activity.
        protected override object?[] Arguments =>
            [
                DroneId,
                Delegation?.PilotId,
                Delegation?.Mode switch
                {
                    DroneDelegationMode.Assist => "assist",
                    DroneDelegationMode.Guard => "guard",
                    _ => null,
                },
            ];
    }

    protected abstract object?[] Arguments { get; }

    public override object ToBlue() => new object?[] { Name, Arguments };

    internal sealed record RemoveBall(long Id, bool Explode) : DestinyCommand(Explode ? "RemoveBall" : "RemoveBalls")
    {
        // Park.RealFlushState appends the explosion map before calling RemoveBalls.
        // Send only the ID list. Supplying optional local-handler arguments here
        // makes the native dispatch exceed its arity and leaves stale space objects.
        protected override object?[] Arguments => Explode ? [Id, true] : [new BlueList<long>([Id])];
    }

    internal sealed record AddBalls(BallparkAddition Addition) : DestinyCommand("AddBalls")
    {
        protected override object?[] Arguments => [Addition];
    }

    internal sealed record OnSlimItemChange(long Id, SlimItem Item) : DestinyCommand("OnSlimItemChange")
    {
        protected override object?[] Arguments => [Id, Item];
    }

    internal sealed record OnDamageStateChange(long Id, ShipDamageState Damage) : DestinyCommand("OnDamageStateChange")
    {
        protected override object?[] Arguments => [Id, Damage];
    }

    internal sealed record OnFleetDamageStateChange(long Id, ShipDamageState Damage) : DestinyCommand("OnFleetDamageStateChange")
    {
        protected override object?[] Arguments => [Id, Damage];
    }

    internal sealed record CloakBall(long Id) : DestinyCommand("CloakBall")
    {
        // Pinned mass-sector cloak mode 1 is an ordinary module cloak.
        private const int NormalCloakMode = 1;
        protected override object?[] Arguments => [Id, NormalCloakMode, Eden.Gameplay.Propulsion.SpatialModuleRules.DecloakDistance];
    }

    internal sealed record UncloakBall(long Id) : DestinyCommand("UncloakBall")
    {
        protected override object?[] Arguments => [Id];
    }

    internal sealed record MicroJumpVisual(long Id, SpacePosition Position) : DestinyCommand("OnSpecialFX")
    {
        private const double FlashMilliseconds = 1000;
        protected override object?[] Arguments =>
            [
                Id,
                null,
                null,
                null,
                null,
                "effects.MicroJumpDriveJump",
                false,
                true,
                false,
                FlashMilliseconds,
                0,
                null,
                0,
                new object[] { Position.X, Position.Y, Position.Z },
            ];
    }

    internal sealed record SetBallPosition(long Id, double X, double Y, double Z) : DestinyCommand("SetBallPosition")
    {
        protected override object?[] Arguments => [Id, X, Y, Z];
    }

    internal sealed record GotoPoint(long Id, double X, double Y, double Z) : DestinyCommand("GotoPoint")
    {
        protected override object?[] Arguments => [Id, X, Y, Z];
    }

    internal sealed record SetBallVelocity(long Id, double X, double Y, double Z) : DestinyCommand("SetBallVelocity")
    {
        protected override object?[] Arguments => [Id, X, Y, Z];
    }

    internal sealed record GotoDirection(long Id, double X, double Y, double Z) : DestinyCommand("GotoDirection")
    {
        protected override object?[] Arguments => [Id, X, Y, Z];
    }

    internal sealed record SetSpeedFraction(long Id, double Fraction) : DestinyCommand("SetSpeedFraction")
    {
        protected override object?[] Arguments => [Id, Fraction];
    }

    internal sealed record Stop(long Id) : DestinyCommand("Stop")
    {
        protected override object?[] Arguments => [Id];
    }

    internal sealed record SetBallMass(long Id, double Mass) : DestinyCommand("SetBallMass")
    {
        protected override object?[] Arguments => [Id, Mass];
    }

    internal sealed record SetMaxSpeed(long Id, double Speed) : DestinyCommand("SetMaxSpeed")
    {
        protected override object?[] Arguments => [Id, Speed];
    }

    internal sealed record WarpTo(long Id, double X, double Y, double Z, double MinimumRange, int Factor) : DestinyCommand("WarpTo")
    {
        protected override object?[] Arguments => [Id, X, Y, Z, MinimumRange, Factor];
    }

    internal sealed record SetState(BallparkSnapshot Snapshot) : DestinyCommand("SetState")
    {
        protected override object?[] Arguments => [Snapshot];
    }

    internal sealed record Follow(NavigationMode Mode, long Id, long TargetId, double Range)
        : DestinyCommand(Mode == NavigationMode.Orbit ? "Orbit" : "FollowBall")
    {
        protected override object?[] Arguments => [Id, TargetId, Range];
    }

    internal sealed record SpecialEffect(
        long Id,
        long? ModuleId,
        int? TypeId,
        long? TargetId,
        int? ChargeTypeId,
        string Graphic,
        bool Offensive,
        bool Start,
        bool Active
    ) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments => [Id, ModuleId, TypeId, TargetId, ChargeTypeId, Graphic, Offensive, Start, Active];
    }

    internal sealed record DbuffUpdated(long Id, IReadOnlyList<CommandBurstBoost> Boosts) : DestinyCommand("OnDbuffUpdated")
    {
        protected override object?[] Arguments => [Id, DbuffEntry.List(Boosts)];
    }

    internal sealed record CommandBurstVisual(
        long Id,
        long ModuleId,
        int TypeId,
        long? TargetId,
        int ChargeTypeId,
        string Graphic,
        Dictionary<string, object?>? GraphicInfo
    ) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [Id, ModuleId, TypeId, TargetId, ChargeTypeId, Graphic, false, true, false, -1, 0, null, 0, GraphicInfo];
    }

    internal sealed record TimedSpecialEffect(
        long Id,
        long? ModuleId,
        int? TypeId,
        long? TargetId,
        int? ChargeTypeId,
        string Graphic,
        bool Offensive,
        bool Start,
        bool Active,
        double Duration,
        long StartedAt,
        Dictionary<string, object?>? GraphicInfo = null
    ) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [Id, ModuleId, TypeId, TargetId, ChargeTypeId, Graphic, Offensive, Start, Active, Duration, 0, StartedAt, 0, GraphicInfo];
    }

    internal sealed record JumpOut(long Id, long GateId, int SystemId, int Duration) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [Id, null, null, GateId, null, "effects.JumpOut", false, true, false, Duration, 0, null, 0, new object?[] { SystemId }];
    }

    internal sealed record WormholeJumpOut(long Id, long Entrance, int DestinationClass, int SystemId, int Duration)
        : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [
                Id,
                null,
                null,
                Entrance,
                DestinationClass,
                "effects.JumpOutWormhole",
                false,
                true,
                false,
                Duration,
                0,
                null,
                0,
                new object?[] { SystemId },
            ];
    }

    internal sealed record GateActivity(long GateId, bool Arriving) : DestinyCommand("OnClientControllerEvent")
    {
        // Build 3662168 Park.OnClientControllerEvent forwards these five fields
        // to SpaceMgr, then the gate model's named controller event handler.
        protected override object?[] Arguments => [GateId, null, Arriving ? "Arrival" : "Departure", 1d, null];
    }

    internal sealed record TranslocationOut(long Id, int SystemId, int Duration) : DestinyCommand("OnSpecialFX")
    {
        // JumpDriveOut accepts a None gate and preloads the destination from graphicInfo[0].
        protected override object?[] Arguments =>
            [Id, null, null, null, null, "effects.JumpDriveOut", false, true, false, Duration, 0, null, 0, new object?[] { SystemId }];
    }
}

internal sealed record DbuffEntry(CommandBurstBoost Boost) : BlueModel
{
    internal static BlueList<DbuffEntry> List(IReadOnlyList<CommandBurstBoost> boosts) =>
        new(boosts.Select(boost => new DbuffEntry(boost)).ToArray());

    public override object ToBlue() => new object?[] { Boost.CollectionId, new object?[] { Boost.Value, Boost.ExpiresAt } };
}
