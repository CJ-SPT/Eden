namespace Eden.Data.World.Inventory;

public sealed class SavedMutatedItem
{
    public long ItemId { get; set; }
    public string Payload { get; set; } = "";
}
