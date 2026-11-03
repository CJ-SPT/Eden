namespace Eden.Gameplay.Inventory;

public sealed record InventoryMoveRequest(IReadOnlyList<long> ItemIds, long SourceId, int FlagId, int? Quantity);
