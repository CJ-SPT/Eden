using Eden.Data.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Corporations;

internal sealed partial class CorporationWarStore
{
    internal static bool IsAttackingJoiningCorporation(WorldAction world, int allianceId, int corporationId, long now) =>
        world
            .Filter<SavedCorporationWar>(war => war.AttackerId == allianceId && war.DefenderId == corporationId)
            .AsEnumerable()
            .Any(war => Finish(war) is not long finish || finish > now);

    internal static void AdoptDefensiveWars(WorldAction world, int corporationId, int allianceId, long now)
    {
        foreach (var war in world.Filter<SavedCorporationWar>(war => war.DefenderId == corporationId && !war.Mutual).ToArray())
        {
            if (Finish(war) is not long finish || finish > now)
            {
                war.DefenderId = allianceId;
            }
        }
    }

    internal static bool PreventsRejoining(WorldAction world, long pilotId, int corporationId, long now)
    {
        if (world is SqlWorldAction)
        {
            return false;
        }
        var active = ForOrganization(world, Organization(world, corporationId)).Where(war => Active(war, now)).ToArray();
        if (active.Length == 0)
        {
            return false;
        }
        var employment = world.Filter<SavedCorporationEmployment>(row => row.PilotId == pilotId);
        var latest = employment.Where(row => row.CorporationId == corporationId).OrderByDescending(row => row.Id).FirstOrDefault();
        if (latest is null)
        {
            return false;
        }
        long? left = employment
            .Where(row => row.Id > latest.Id)
            .OrderBy(row => row.Id)
            .Select(row => (long?)row.StartedAt)
            .FirstOrDefault();
        return left is long departed && departed > now - TimeSpan.TicksPerDay * 7 && active.Any(war => Active(war, departed));
    }
}
