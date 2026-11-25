using Eden.Data.World.Corporations;
using Eden.Gameplay.Corporations;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    internal static bool ActiveAlly(WorldAction world, SavedCorporationWarAlly ally, long now) =>
        ally.StartsAt <= now && now < ally.EndsAt && world.Set<SavedCorporationWar>().Find(ally.WarId) is { } war && Active(war, now);

    private static IEnumerable<SavedCorporationWar> WarsInvolving(WorldAction world, int owner, long now) =>
        ForOrganization(world, owner)
            .Concat(
                world
                    .Filter<SavedCorporationWarAlly>(ally => ally.OwnerId == owner && ally.EndsAt > now)
                    .AsEnumerable()
                    .Select(ally => world.Set<SavedCorporationWar>().Find(ally.WarId))
                    .OfType<SavedCorporationWar>()
                    .Where(war => Finish(war) > now)
            )
            .DistinctBy(war => war.Id);

    private static IReadOnlyList<CorporationWarAlly> WarAllyViews(WorldAction world, SavedCorporationWar war, long now)
    {
        if (war.Mutual || Finish(war) is not long finish || finish <= now)
        {
            return [];
        }
        return AlliesForWar(world, war.Id, now)
            .Where(ally => ally.StartsAt < finish)
            .OrderBy(ally => ally.OwnerId)
            .Select(ally => new CorporationWarAlly(ally.OwnerId, ally.StartsAt, Math.Min(ally.EndsAt, war.EndsAt ?? long.MaxValue)))
            .ToArray();
    }

    private static IEnumerable<SavedCorporationWarAlly> AlliesForWar(WorldAction world, long warId, long now) =>
        world.Filter<SavedCorporationWarAlly>(ally => ally.WarId == warId && ally.EndsAt > now).AsEnumerable();

    private static void DropWarAllies(WorldAction world, long warId, long now)
    {
        foreach (var ally in AlliesForWar(world, warId, now).ToArray())
        {
            ally.EndsAt = now;
        }
    }

    private static IEnumerable<SavedCorporationWarNegotiation> SurrenderTreaties(WorldAction world, int owner, long now)
    {
        long since = now - CorporationWarRules.SurrenderTruce;
        return world
            .Filter<SavedCorporationWarNegotiation>(offer =>
                (offer.FromOwnerId == owner || offer.ToOwnerId == owner)
                && offer.Kind == CorporationWarNegotiationKind.Surrender
                && offer.AcceptedAt > since
            )
            .AsEnumerable();
    }
}
