using Eden.Data.World.Mining;
using Eden.Gameplay.Mining;
using Eden.World.Runtime.Economy;

namespace Eden.World.Mining;

internal sealed partial class MiningSiteStore
{
    internal static void Collected(WorldAction world, long depositId, long now)
    {
        if (!MiningSiteRules.IsDeposit(depositId) || world.Set<SavedMiningDeposit>().Find(depositId) is not { } member)
        {
            return;
        }
        var members = world.Filter<SavedMiningDeposit>(d => d.SiteId == member.SiteId).ToArray();
        if (!members.Any(d => world.Set<WorldAsteroid>().Find(d.Id)?.Quantity > 0))
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
        var members = world.Filter<SavedMiningDeposit>(d => d.SiteId == site.Id).ToArray();
        world.RemoveRange(members.Select(d => world.Set<WorldAsteroid>().Find(d.Id)).OfType<WorldAsteroid>());
        world.RemoveRange(members);
    }
}
