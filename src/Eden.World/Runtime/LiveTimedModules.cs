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
        var structureTackles = economy.DueStructureTackles(system, now);
        var independentEwar = economy.DueIndependentEwar(system, now);
        if (bursts.Length != 0 || modules.Length != 0 || independentEwar.Length != 0 || structureTackles.Length != 0)
        {
            ExpireDue(now, bursts, modules, structureTackles, independentEwar);
        }
    }

    private void ExpireDue(long now, long[] bursts, long[] modules, long[] structureTackles, (long Module, long Target)[] independentEwar)
    {
        var economy = owner.Economy;
        var ships = economy.Inspect(world =>
            bursts
                .Select(id => world.Set<WorldCommandBurst>().Find(id)?.ShipId)
                .Concat(modules.Select(id => world.Set<ActiveFittedModule>().Find(id)?.ShipId))
                .Concat(
                    modules
                        .Concat(structureTackles)
                        .Select(id => world.Set<Eden.Data.World.Propulsion.WorldTackle>().Find(id))
                        .Where(row => row?.TargetKind == Eden.Gameplay.Propulsion.TackleTargetKind.Ship)
                        .Select(row => (long?)row!.TargetId)
                )
                .Concat(
                    independentEwar
                        .Select(contact => world.Set<Eden.Data.World.Combat.WorldPlayerEwar>().Find(contact.Module, contact.Target))
                        .Where(row => row?.TargetKind == Eden.Gameplay.Propulsion.TackleTargetKind.Ship)
                        .Select(row => (long?)row!.TargetId)
                )
                .OfType<long>()
                .Distinct()
                .ToArray()
        );
        using var action = economy.Begin(new(ships: ships));
        foreach (long id in structureTackles)
        {
            if (action.Set<Eden.Data.World.Propulsion.WorldTackle>().Find(id) is { } tackle && tackle.ExpiresAt <= now)
            {
                action.Remove(tackle);
            }
        }
        foreach (var contact in independentEwar)
        {
            if (action.Set<Eden.Data.World.Combat.WorldPlayerEwar>().Find(contact.Module, contact.Target) is { } row)
            {
                action.Remove(row);
            }
        }
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
                if (
                    action.Set<Eden.Data.World.Sovereignty.SavedEntosisCycle>().Find(id) is { Stopping: true } entosis
                    && (
                        !entosis.Contributing
                        || action.Set<Eden.Data.World.Sovereignty.SavedSovereigntyClaim>().Find(entosis.SystemId) is not null
                    )
                )
                {
                    action.Remove(entosis);
                }
                if (
                    action.Set<Eden.Data.World.Propulsion.WorldTackle>().Find(id) is { } tackle
                    && Eden.Gameplay.Propulsion.InterdictionRules.Focused(tackle.Effect)
                )
                {
                    action.Remove(tackle);
                }
                action.RemoveRange(
                    action.Filter<Eden.Data.World.Propulsion.SavedInterdictionField>(field =>
                        field.ModuleId == id && field.Kind == Eden.Gameplay.Propulsion.InterdictionKind.HeavyField
                    )
                );
            }
        }
        action.Commit();
    }
}
