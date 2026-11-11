using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;

namespace Eden.World.Runtime.Economy;

internal static class EconomyOwnership
{
    internal static long Pilot<T>(T row)
        where T : class =>
        row switch
        {
            Pilot pilot => pilot.Id,
            OwnedItem item => item.OwnerId,
            Ship ship => ship.OwnerId,
            PilotWallet wallet => wallet.PilotId,
            SavedPersonalOrder order => order.PilotId,
            MarketExecution trade => trade.PilotId,
            SavedMarketPayment payment => payment.PilotId,
            SavedMarketChange change => change.PilotId,
            SavedTaskCounter counter => counter.PilotId,
            SavedCompletedTask task => task.PilotId,
            _ => 0,
        };
}
