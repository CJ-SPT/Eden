using Eden.Gameplay.Flight;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
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

    protected abstract object?[] Arguments { get; }

    public override object ToBlue() => new object?[] { Name, Arguments };

    internal sealed record RemoveBall(long Id, bool Explode) : DestinyCommand("RemoveBall")
    {
        protected override object?[] Arguments => [Id, Explode];
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
        long StartedAt
    ) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [Id, ModuleId, TypeId, TargetId, ChargeTypeId, Graphic, Offensive, Start, Active, Duration, 0, StartedAt, 0, null];
    }

    internal sealed record JumpOut(long Id, long GateId, int SystemId, int Duration) : DestinyCommand("OnSpecialFX")
    {
        protected override object?[] Arguments =>
            [Id, null, null, GateId, null, "effects.JumpOut", false, true, false, Duration, 0, null, 0, new object?[] { SystemId }];
    }

    internal sealed record TranslocationOut(long Id, int SystemId, int Duration) : DestinyCommand("OnSpecialFX")
    {
        // JumpDriveOut accepts a None gate and preloads the destination from graphicInfo[0].
        protected override object?[] Arguments =>
            [Id, null, null, null, null, "effects.JumpDriveOut", false, true, false, Duration, 0, null, 0, new object?[] { SystemId }];
    }
}
