using Eden.Data.World.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal static class CapsuleerRefitFunding
{
    internal static bool Applies(WorldAction world, long pilot) =>
        world is EconomyTransaction
        && world.Settings.Capsuleers is { Enabled: true, FundRefits: true }
        && world.Set<SavedCapsuleerAgent>().Find(pilot) is not null;
}
