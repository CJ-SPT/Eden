using Eden.Data.Catalog.Mining;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal sealed partial class MiningSiteStore
{
    internal static void Collected(WorldAction world, long depositId, long now)
    {
        if (!MiningSiteRules.IsDeposit(depositId) || world.Set<SavedMiningDeposit>().Find(depositId) is not { } member)
        {
            return;
        }
        var ids = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == member.SiteId).Select(d => d.Id);
        if (!world.Set<WorldAsteroid>().Any(a => Enumerable.Contains(ids, a.Id) && a.Quantity > 0))
        {
            var site = world.Set<SavedMiningSite>().Find(member.SiteId)!;
            if (site.ReplaceAt is null)
            {
                Retire(world, site, now);
            }
        }
    }

    private static void Retire(WorldAction world, SavedMiningSite site, long now)
    {
        site.ReplaceAt = checked(
            now + (site.Family == MiningFamily.Ice ? MiningSiteRules.IceReplacementDelay : MiningSiteRules.ReplacementDelay)
        );
        var members = world.Set<SavedMiningDeposit>().Where(d => d.SiteId == site.Id).ToArray();
        var ids = members.Select(d => d.Id).ToArray();
        world.RemoveRange(world.Set<WorldAsteroid>().Where(a => Enumerable.Contains(ids, a.Id)).ToArray());
        world.RemoveRange(members);
    }
}
