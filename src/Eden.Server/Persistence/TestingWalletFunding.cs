using Eden.Gameplay.Market;

namespace Eden.Server.Persistence;

internal static class TestingWalletFunding
{
    // The caller owns the migration/character-creation transaction. The grant's
    // primary key makes funding durable and unique for the lifetime of a pilot.
    internal static void Grant(WorldDbContext world, long pilotId, long time)
    {
        if (world.Set<TestingWalletGrant>().Find(pilotId) is not null)
        {
            return;
        }
        var wallet = world.Set<PilotWallet>().Find(pilotId) ?? world.Add(new PilotWallet { PilotId = pilotId }).Entity;
        wallet.Cents = TestingMarketRules.GrantBalance(wallet.Cents);
        world.Add(
            new TestingWalletGrant
            {
                PilotId = pilotId,
                AmountCents = TestingMarketRules.GrantCents,
                BalanceCents = wallet.Cents,
                Time = time,
            }
        );
        world.SaveChanges();
    }
}
