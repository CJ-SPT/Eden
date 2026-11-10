namespace Eden.Infrastructure.Operations;

// Native scene motion and authoritative mutations must enter the same owner first.
public interface IWorldSynchronization
{
    bool IsEntered { get; }
    int? CurrentSystem => null;
    IDisposable Enter(string operation, int? systemId = null);
    IDisposable? TryEnterShared(string operation) => Enter(operation);
    IDisposable? TryEnterSystems(string operation, IReadOnlyCollection<int> systems) => Enter(operation);

    // A session scope selects a runtime without retaining ownership across hooks,
    // protocol encoding, logging or output. Each authoritative operation owns its work.
    IDisposable? UseSystem(int systemId) => null;

    T Run<T>(string operation, int systemId, Func<T> action)
    {
        using var ownership = Enter(operation, systemId);
        return action();
    }
}
