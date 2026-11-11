namespace Eden.Gameplay.Flight;

public abstract record FlightCommand
{
    public sealed record Undock(long ShipId) : FlightCommand;

    public sealed record Jump(long SourceId, long ExitId, long ShipId) : FlightCommand;

    public sealed record WarpToScan(string TargetId, double Range) : FlightCommand;

    public sealed record WarpTo(long TargetId, double Range) : FlightCommand;

    public sealed record WarpToBookmark(long BookmarkId, double Range) : FlightCommand;

    public sealed record WarpToFleetMember(long PilotId, double Range) : FlightCommand;

    public sealed record WarpToPoint(long OrderId, SpacePosition Position, double Range) : FlightCommand;

    public sealed record ActivateAccelerationGate(long GateId) : FlightCommand;

    public sealed record RefreshState : FlightCommand;

    public sealed record Heading(double X, double Y, double Z) : FlightCommand;

    public sealed record AlignTo(long TargetId) : FlightCommand;

    public sealed record AlignToBookmark(long BookmarkId) : FlightCommand;

    public sealed record AlignToPoint(SpacePosition Position) : FlightCommand;

    public sealed record AutopilotWarp(long TargetId) : FlightCommand;

    public sealed record Follow(long TargetId, double Range, bool Orbit) : FlightCommand;

    public sealed record Speed(double Fraction) : FlightCommand;

    public sealed record Stop : FlightCommand;

    public sealed record Dock(long StationId, long ShipId) : FlightCommand;
}
