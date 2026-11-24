using Eden.Data.World.Inventory;
using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Exploration;

[Injectable(InjectionType.Singleton)]
internal sealed class IncursionSystemEffectStore(WorldDatabase database) : IIncursionSystemEffects
{
    public IncursionSystemEffect? Read(int systemId, long now) => At(LiveWorld.Find(database.Path), systemId, now);

    internal static IncursionSystemEffect? At(LiveWorld? live, int systemId, long now) => live?.IncursionEffectForSystem(systemId, now);

    internal static IncursionSystemEffect? At(WorldAction world, int systemId, long now) =>
        At(LiveWorld.Find(world.WorldPath), systemId, now);

    internal static double WeaponDamage(WorldAction world, int systemId, long now) => At(world, systemId, now)?.WeaponDamageMultiplier ?? 1;

    internal static decimal Bounties(LiveWorld? live, int systemId, long now) => At(live, systemId, now)?.BountyMultiplier ?? 1;

    internal static IncursionSystemEffect? ForShip(WorldAction world, CatalogDbContext catalog, Ship ship, long? now)
    {
        if (ship.Position is not { } position || LiveWorld.Find(world.WorldPath) is not { } live)
        {
            return null;
        }
        int systemId = position.SystemId ?? catalog.FindStation(ship.StationId)?.SolarSystemId ?? 0;
        return At(live, systemId, now ?? live.SimulationTime);
    }
}
