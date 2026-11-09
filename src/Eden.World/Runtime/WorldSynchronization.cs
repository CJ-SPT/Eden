using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Runtime;

[Injectable(InjectionType.Singleton)]
internal sealed class WorldSynchronization(WorldDatabase database) : IWorldSynchronization
{
    private readonly object _standalone = new();

    public IDisposable Enter(string operation) => LiveWorld.Find(database.Path)?.Enter(operation) ?? new Lease(_standalone);

    private sealed class Lease : IDisposable
    {
        private readonly object _gate;

        public Lease(object gate)
        {
            _gate = gate;
            Monitor.Enter(_gate);
        }

        public void Dispose() => Monitor.Exit(_gate);
    }

    public object Gate => LiveWorld.Find(database.Path)?.SynchronizationGate ?? _standalone;
}
