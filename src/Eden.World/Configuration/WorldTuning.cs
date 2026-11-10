using Eden.Gameplay.Configuration;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Configuration;

[Injectable(InjectionType.Singleton)]
internal sealed class WorldTuning(WorldDatabase database) : IWorldTuning
{
    public WorldSettings Settings => database.Settings;
}
