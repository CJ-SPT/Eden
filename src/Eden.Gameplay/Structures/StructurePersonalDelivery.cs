using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Structures;

public interface IStructurePersonalDelivery
{
    IReadOnlyList<InventoryChange> Deliver(long senderId, long recipientId, long structureId, IReadOnlyList<long> itemIds);
}
