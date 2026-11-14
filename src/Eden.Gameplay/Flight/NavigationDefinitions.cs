namespace Eden.Gameplay.Flight;

public enum NavigationMode : byte
{
    Goto = 0,
    Follow = 1,
    Stop = 2,
    Warp = 3,
    Orbit = 4,
    Missile = 5,
    Rigid = 11,
}

public static class NavigationDefinitions
{
    public const int AutopilotLandingMeters = 15_000;
    public const int MinimumWarpDistance = 150_000;
    public const double MaximumTargetDistance = 8_000_000;
    public const double MaximumFollowRange = 1_000_000;
    public const int ReconciliationTicks = 10;
}
