using Eden.Data.World.Agents;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    private static void RequireResidentReward(WorldAction world, SavedMission row, MissionProfile profile)
    {
        if (profile.Kind != MissionKind.Trade && profile.Definition.ItemReward is null && row.ItemRewardTypeId == 0)
        {
            return;
        }
        if (world is not EconomyTransaction)
        {
            throw new InvalidDataException("Trade missions and item rewards require the resident world.");
        }
        if (profile.Definition.ItemReward is not null && (row.ItemRewardTypeId <= 0 || row.ItemRewardQuantity <= 0))
        {
            throw new InvalidDataException("This mission has no valid saved item reward quote.");
        }
    }

    private static InventoryChange? GrantItemReward(WorldAction world, CatalogDbContext catalog, SavedMission row, int station)
    {
        if (row.ItemRewardTypeId == 0 && row.ItemRewardQuantity == 0)
        {
            return null;
        }
        if (world is not EconomyTransaction || row.ItemRewardTypeId <= 0 || row.ItemRewardQuantity <= 0)
        {
            throw new InvalidDataException("Invalid resident mission item reward quote.");
        }
        return InventoryPersistence.MergeOutput(
            world,
            catalog,
            row.PilotId,
            station,
            (int)InventoryFlag.Hangar,
            row.ItemRewardTypeId,
            row.ItemRewardQuantity
        );
    }
}
