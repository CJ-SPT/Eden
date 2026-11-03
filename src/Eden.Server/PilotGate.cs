using System.Collections.Concurrent;
using Eden.Protocol;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotGate
{
    private readonly ConcurrentDictionary<long, byte> _accounts = new();

    public bool TryEnter(long accountId) => _accounts.TryAdd(accountId, 0);

    public void Leave(long accountId) => _accounts.TryRemove(accountId, out _);
}
