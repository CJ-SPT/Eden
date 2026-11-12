namespace Eden.Gameplay.Capsuleers;

public sealed record CapsuleerCoarseWork(int SystemId, long Generation, long Cursor, long ThroughTicks, long NextTicks)
{
    public IReadOnlyList<int> Systems { get; init; } = [];
    public long SettledTicks { get; init; } = ThroughTicks;
}
