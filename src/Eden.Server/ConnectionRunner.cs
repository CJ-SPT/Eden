using Eden.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Server;

internal sealed class ConnectionAdmission(PilotGate gate, Action? authenticated = null) : IDisposable
{
    private long? _accountId;

    public void Enter(long accountId)
    {
        if (_accountId.HasValue || !gate.TryEnter(accountId))
        {
            throw new InvalidDataException("This account is already connected or the server is full.");
        }
        _accountId = accountId;
        authenticated?.Invoke();
    }

    public void Dispose()
    {
        if (_accountId is long accountId)
        {
            _accountId = null;
            gate.Leave(accountId);
        }
    }
}

[Injectable(InjectionType.Singleton)]
internal sealed class ConnectionRunner(IServiceScopeFactory scopes, PilotGate gate)
{
    public async Task RunAsync(Func<IServiceProvider, ConnectionAdmission, Task> serve, Action? authenticated = null)
    {
        using ConnectionAdmission admission = new(gate, authenticated);
        // Dispose feature controllers before releasing the authenticated account.
        using var scope = scopes.CreateScope();
        await serve(scope.ServiceProvider, admission);
    }
}
