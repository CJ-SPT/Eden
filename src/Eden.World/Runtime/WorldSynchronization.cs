using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Runtime;

[Injectable(InjectionType.Singleton)]
internal sealed class WorldSynchronization(WorldDatabase database) : IWorldSynchronization
{
    private readonly object _standalone = new();
    public object Gate => LiveWorld.Find(database.Path)?.SynchronizationGate ?? _standalone;
}
