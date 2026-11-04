using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Contracts;

public static class BulkContractRules
{
    // Build 3662168 globalConfig.const.BULK_CONTRACT_SHIPS_DEFAULT and
    // CreateContractWnd.FinishStep2_FittedShip's public minimum per ship.
    public const int MaximumShips = 30;
    public const long MinimumPublicPriceCents = 1000 * MarketDefinitions.CentsPerIsk;

    public static void Check(ContractDraft draft)
    {
        if (!draft.MultiContract)
        {
            return;
        }
        if (
            draft.Type != ContractType.ItemExchange
            || draft.Items.Count is < 1 or > MaximumShips
            || draft.Items.Any(i => i.Quantity != 1)
            || draft.RewardCents != 0
            || draft.RequestedItems.Count != 0
            || (draft.AssigneeId is null && draft.PriceCents < MinimumPublicPriceCents)
        )
        {
            throw new InvalidDataException("Choose up to 30 assembled ships and a price per ship; public sales start at 1,000 ISK.");
        }
    }

    public static void CheckShip(InventoryItem item)
    {
        if (
            item.CategoryId != (int)InventoryCategory.Ship
            || !item.Singleton
            || item.Quantity != 1
            || item.GroupId == FleetDefinitions.CapsuleGroup
        )
        {
            throw new InvalidDataException("Bulk contracts require assembled ships other than capsules.");
        }
    }
}
