using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Combat.Npcs;

internal static class NpcSentryOffenses
{
    internal static void Record(WorldAction world, CatalogDbContext catalog, long sourceShipId, int systemId, long now)
    {
        if (
            world is not EconomyTransaction
            || !world.Settings.NpcPopulation.Guards
            || CrimewatchPersistence.Security(catalog, systemId, required: false) is not (> 0 and < CrimewatchRules.HighSecurity)
            || world.Ships.Find(sourceShipId) is not { Position: { } position } ship
            || catalog.RequireItem(ship.TypeId).GroupId == FleetDefinitions.CapsuleGroup
        )
        {
            return;
        }

        SpacePosition location = new(position.X, position.Y, position.Z);
        var live = LiveWorld.Find(world.WorldPath);
        foreach (var sentry in NpcGuardSeeder.LocalSentries(world, catalog, systemId, location, now))
        {
            var current = world.Set<NpcAggression>().Find(sentry.Id);
            if (current is not null && InRange(current.TargetShipId, sentry.Position, sentry.Range))
            {
                continue;
            }
            var response = NpcAggression.Start(sentry.Id, sourceShipId, NpcGuardRules.ReactionMilliseconds, now);
            if (current is null)
            {
                world.Add(response);
                continue;
            }
            current.TargetShipId = response.TargetShipId;
            current.ReadyAt = response.ReadyAt;
            current.MissileReadyAt = response.MissileReadyAt;
            current.LastShotAt = null;
            current.LastTargetId = null;
        }

        bool InRange(long target, SpacePosition sentry, double range) =>
            target == sourceShipId
            || world.Ships.Find(target) is { Position: { } at } previous
                && CrimewatchPersistence.System(catalog, previous) == systemId
                && sentry.DistanceTo(live?.PublishedPosition(systemId, target) ?? new(at.X, at.Y, at.Z)) <= range;
    }
}
