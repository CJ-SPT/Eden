using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

namespace Eden.World.Exploration;

internal sealed partial class CombatAnomalyStore
{
    internal static (WorldCombatAnomaly Site, StaticSpaceObject Body) AccessIncursionArray(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long id,
        double range,
        long now
    )
    {
        if (
            world is not EconomyTransaction
            || world.Set<WorldCombatAnomaly>().Find(IncursionObjectIds.Site(id))
                is not {
                    CompletedAt: null,
                    IncursionTaleId: long tale,
                    DungeonId: IncursionSiteCatalog.OverrideTransferArray,
                    Wave: >= 0
                } site
            || now < IncursionRules.OpensAt(tale)
            || now >= IncursionRules.EndsAt(tale)
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != site.SystemId
        )
        {
            throw new InvalidDataException("This Incursion logistics array is unavailable in your current system.");
        }

        var body = IncursionSiteCatalog.ArrayBody(catalog, site);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(body.Position)
                - (catalog.RequireItem(pilot.ActiveShip.TypeId).Radius ?? 0)
                - body.Radius
            > range
        )
        {
            throw new InvalidDataException("Move within the logistics array's activation range.");
        }
        return (site, body);
    }

    internal static void CompleteIncursionArrayHack(WorldAction world, CatalogDbContext catalog, WorldCombatAnomaly site, long now)
    {
        if (!MayOverrideArray(site, now))
        {
            return;
        }

        // Defuse the reinforcement timer. Pilots still have to defeat both groups.
        site.NextWaveAt = null;
        if (!world.Filter<WorldNpc>(npc => npc.EncounterId == site.Id && npc.DestroyedAt == null && npc.RetiredAt == null).Any())
        {
            NextWave(world, catalog, site, now);
        }
        Invalidate(world, site.SystemId);
    }

    internal static bool MayOverrideArray(WorldCombatAnomaly site, long now) => site.Wave == 0 && site.NextWaveAt > now;

    private static void RetireIncursionArray(WorldAction world, WorldCombatAnomaly site)
    {
        if (
            site.DungeonId == IncursionSiteCatalog.OverrideTransferArray
            && world.Set<SavedHackingContainer>().Find(IncursionObjectIds.Object(site.Id, IncursionObjectIds.LogisticsArray)) is { } array
        )
        {
            world.Remove(array);
        }
    }
}
