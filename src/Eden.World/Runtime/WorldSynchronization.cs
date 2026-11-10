using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Runtime;

[Injectable(InjectionType.Singleton)]
internal sealed class WorldSynchronization(WorldDatabase database) : IWorldSynchronization
{
    private readonly object _standalone = new();

    private OwnershipMonitor? _ownership;

    public IDisposable Enter(string operation, int? systemId = null) =>
        (
            LiveWorld.Find(database.Path) is { } live
                ? systemId is null
                    ? live.EnterShared(operation)
                    : live.Enter(operation, systemId)
                : null
        ) ?? LazyInitializer.EnsureInitialized(ref _ownership, () => new("standalone world", _standalone)).Enter(operation, systemId);

    public bool IsEntered => LiveWorld.Find(database.Path)?.IsEntered ?? Monitor.IsEntered(_standalone);
    public int? CurrentSystem => LiveWorld.Find(database.Path)?.CurrentSystem;

    public IDisposable? TryEnterShared(string operation) =>
        LiveWorld.Find(database.Path) is { } live ? live.TryEnterShared(operation) : Enter(operation);

    public IDisposable? UseSystem(int systemId) => LiveWorld.Find(database.Path)?.UseSystem(systemId);

    public T Run<T>(string operation, int systemId, Func<T> action)
    {
        if (LiveWorld.Find(database.Path) is { } live)
        {
            using var context = live.UseSystem(systemId);
            return live.Run(action, operation);
        }
        using var ownership = Enter(operation, systemId);
        return action();
    }
}
