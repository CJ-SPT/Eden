using Eden.Protocol;
using Eden.Server.Launcher;

namespace Eden.Server;

[Injectable(InjectionType.Singleton)]
internal sealed class PilotGate(LauncherOptions? options = null)
{
    private readonly HashSet<long> _accounts = [];

    public bool TryEnter(long accountId)
    {
        lock (_accounts)
        {
            return _accounts.Count < (options ?? new()).MaximumActiveSessions && _accounts.Add(accountId);
        }
    }

    public void Leave(long accountId)
    {
        lock (_accounts)
        {
            _accounts.Remove(accountId);
        }
    }
}
