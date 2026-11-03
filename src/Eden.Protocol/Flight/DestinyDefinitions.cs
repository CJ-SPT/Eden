namespace Eden.Protocol.Flight;

internal enum DestinyBallMode : byte
{
    Goto = 0,
    Follow = 1,
    Stop = 2,
    Warp = 3,
    Orbit = 4,
    Rigid = 11,
}

internal static class NavigationDefinitions
{
    public const double MaximumTargetDistance = 8_000_000;
    public const double MaximumFollowRange = 1_000_000;
    public const int ReconciliationTicks = 10;
}

[Flags]
internal enum DestinyBallFlags : byte
{
    None = 0,
    Free = 1,
    Global = 2,
    Massive = 4,
    Interactive = 8,
}
