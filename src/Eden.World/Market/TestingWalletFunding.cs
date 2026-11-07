using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.World.Storage;

namespace Eden.World.Market;

internal static class TestingWalletFunding
{
    // The caller owns the migration/character-creation transaction. The grant's
    // primary key makes funding durable and unique for the lifetime of a pilot.
    internal static void Grant(WorldDbContext world, long pilotId, long time)
    {
        if (world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingGrants: false })
        {
            return;
        }
        if (world.Set<TestingWalletGrant>().Find(pilotId) is not null)
        {
            return;
        }
        var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;
        long grant = world.Settings.DevelopmentSupply.GrantCents;
        wallet.Cents = checked(wallet.Cents + grant);
        world.Add(
            new TestingWalletGrant
            {
                PilotId = pilotId,
                AmountCents = grant,
                BalanceCents = wallet.Cents,
                Time = time,
            }
        );
        world.SaveChanges();
    }
}
