using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Structures;

public sealed record GasDecompressionEfficiency(decimal Structure, decimal Character)
{
    public decimal Total => Math.Min(1, Structure + Character);
}

public sealed record StructureCompressionResult(
    long SourceItemId,
    int SourceTypeId,
    int SourceQuantity,
    long? OutputItemId,
    int OutputTypeId,
    int OutputQuantity,
    InventoryChange Change
);

public interface IStructureCompression
{
    GasDecompressionEfficiency Efficiency(long pilotId);
    StructureCompressionResult Compress(long pilotId, long itemId);
    StructureCompressionResult DecompressGas(long pilotId, long itemId);
}

public static class StructureCompressionRules
{
    // Pinned Gas Decompression Efficiency skill and client type list 336.
    public const int GasDecompressionSkill = 62452;
    public const int StructureTypeList = 336;

    public static GasDecompressionEfficiency Efficiency(decimal moduleBase, decimal hullBonus, decimal skillBonusPercent, int level)
    {
        if (moduleBase is < 0 or > 1 || hullBonus is < 0 or > 1 || skillBonusPercent < 0 || level is < 0 or > 5)
        {
            throw new InvalidDataException("Invalid catalog gas-decompression efficiency inputs.");
        }
        return new(Math.Min(1, moduleBase + hullBonus), Math.Min(1, level * skillBonusPercent / 100));
    }

    public static int Decompress(int quantity, GasDecompressionEfficiency efficiency)
    {
        if (quantity <= 0 || efficiency.Structure is < 0 or > 1 || efficiency.Character is < 0 or > 1)
        {
            throw new InvalidDataException("Choose a positive compressed-gas stack and valid decompression efficiency.");
        }
        return checked((int)decimal.Floor(quantity * efficiency.Total));
    }
}
