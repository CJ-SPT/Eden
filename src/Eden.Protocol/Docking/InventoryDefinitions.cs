namespace Eden.Protocol.Docking;

public enum InventoryColumn
{
    Owner = 2,
    Location = 3,
    Flag = 4,
    Quantity = 5,
    StackSize = 9,
    Singleton = 10,
}

internal static class InventoryWireDefinitions
{
    public const string OnlineEffectName = "online";
    public const int AssembledQuantity = -1;
    public const int InfiniteEffectDuration = -1;
}
