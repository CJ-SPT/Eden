namespace Eden.Protocol.Docking;

public enum InventoryColumn
{
    Type = 1,
    Owner = 2,
    Location = 3,
    Flag = 4,
    Quantity = 5,
    Group = 6,
    Category = 7,
    StackSize = 9,
    Singleton = 10,
}

// Build 3662168 inventorycommon.const; invCache rewrites the hangar container in an Upwell session.
internal enum InventoryContainerId : long
{
    Hangar = 10004,
    Structure = 10014,
}

internal static class InventoryWireDefinitions
{
    public const string OnlineEffectName = "online";
    public const int AssembledQuantity = -1;
    public const int BlueprintCopyQuantity = -2;
    public const int InfiniteEffectDuration = -1;
}
