using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal static class AbyssalEnvironmentStore
{
    internal static AbyssalEnvironment? ForShip(WorldAction world, Ship ship) =>
        world is EconomyTransaction ? At(world, ship.Position) : null;

    internal static AbyssalEnvironment? At(WorldAction world, FlightPosition? position)
    {
        if (position?.SystemId is not int system || !AbyssalRules.IsSystem(system))
        {
            return null;
        }
        var run = world.Filter<SavedAbyssalRun>(r => r.SystemId == system && r.Status == (int)AbyssalStatus.Active).SingleOrDefault();
        return run is null ? null : At(run, new(position.X, position.Y, position.Z));
    }

    internal static AbyssalEnvironment At(SavedAbyssalRun run, SpacePosition position) =>
        new(
            (AbyssalWeather)run.Weather,
            AbyssalEnvironmentRules.Penalty(run.Tier, run.Seed),
            AbyssalEnvironmentRules.Fields(run.Room, run.Seed, position) & ~(AbyssalFieldMask)run.DisabledFields
        );

    internal static IReadOnlyDictionary<DogmaAttributeId, double> NpcAttributes(
        WorldAction world,
        long id,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes
    )
    {
        if (world is not EconomyTransaction)
        {
            return attributes;
        }
        var npc = world.Set<WorldNpc>().Find(id);
        return
            npc is { AnchorKind: Eden.Gameplay.Combat.NpcAnchorKind.Abyssal }
            && world.Set<SavedAbyssalRun>().Find(npc.EncounterId) is { } run
            ? AbyssalEnvironmentRules.Apply(attributes, At(run, new(npc.X, npc.Y, npc.Z)))
            : attributes;
    }
}
