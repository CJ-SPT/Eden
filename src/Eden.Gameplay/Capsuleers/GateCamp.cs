namespace Eden.Gameplay.Capsuleers;

public enum CampRole
{
    Scout,
    Tackle,
    Damage,
}

public sealed record GateCamp(long GateId, long Until, CampRole Role, long LauncherId = 0)
{
    public long ProbeReadyAt { get; init; }
}

public static class GateCampRules
{
    // One shift on, one shift off. Ships already in space never materialize at the rendezvous.
    public static long End(long elapsed, int seconds)
    {
        long duration = TimeSpan.FromSeconds(seconds).Ticks;
        long window = elapsed / duration;
        return window % 2 == 0 ? checked((window + 1) * duration) : 0;
    }

    public static double StandOff(CampRole role) =>
        role switch
        {
            CampRole.Scout => 22_000,
            CampRole.Tackle => 3_000,
            _ => 9_000,
        };
}
