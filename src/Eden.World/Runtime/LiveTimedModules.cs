using Eden.Data.World.Fittings;
using Eden.Data.World.Fleets;

namespace Eden.World.Runtime;

internal sealed class LiveTimedModules(LiveWorld owner, int? system = null)
{
    internal void Expire(long now)
    {
        var economy = owner.Economy;
        var bursts = economy.DueCommandBursts(system, now);
        var modules = economy.DueModules(system, now);
        if (bursts.Length == 0 && modules.Length == 0)
        {
            return;
        }
        var ships = economy.Inspect(world =>
            bursts
                .Select(id => world.Set<WorldCommandBurst>().Find(id)?.ShipId)
                .Concat(modules.Select(id => world.Set<ActiveFittedModule>().Find(id)?.ShipId))
                .OfType<long>()
                .Distinct()
                .ToArray()
        );
        using var action = economy.Begin(new(ships: ships));
        foreach (long id in bursts)
        {
            if (action.Set<WorldCommandBurst>().Find(id) is { } burst && burst.ExpiresAt <= now)
            {
                action.Remove(burst);
            }
        }
        foreach (long id in modules)
        {
            if (action.Set<ActiveFittedModule>().Find(id) is { ExpiresAt: > 0 } module && module.ExpiresAt <= now)
            {
                action.Remove(module);
            }
        }
        action.Commit();
    }
}
