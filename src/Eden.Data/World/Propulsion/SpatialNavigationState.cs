namespace Eden.Data.World.Propulsion;

// Paid spool state is durable for exactly-once settlement; restart cancels the jump.
public sealed class SpatialNavigationState
{
    public long ShipId { get; set; }
    public long TargetingReadyAt { get; set; }
    public long CloakModuleId { get; set; }
    public double CloakTargetingMilliseconds { get; set; }
    public double CloakReactivationMilliseconds { get; set; }
    public long JumpModuleId { get; set; }
    public long JumpStartedAt { get; set; }
    public long JumpEndsAt { get; set; }
    public double HeadingX { get; set; }
    public double HeadingY { get; set; }
    public double HeadingZ { get; set; }
    public double VelocityX { get; set; }
    public double VelocityY { get; set; }
    public double VelocityZ { get; set; }
}
