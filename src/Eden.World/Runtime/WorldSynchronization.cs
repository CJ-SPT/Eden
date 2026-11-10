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
        LiveWorld.Find(database.Path)?.Enter(operation, systemId)
        ?? LazyInitializer.EnsureInitialized(ref _ownership, () => new("standalone world", _standalone)).Enter(operation, systemId);

    public bool IsEntered => LiveWorld.Find(database.Path)?.IsEntered ?? Monitor.IsEntered(_standalone);
}
