using Eden.Protocol;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotGate
{
    private int _occupied;
    internal bool IsOccupied => Volatile.Read(ref _occupied) != 0;
    public bool TryEnter() => Interlocked.CompareExchange(ref _occupied, 1, 0) == 0;
    public void Leave() => Interlocked.Exchange(ref _occupied, 0);
}
