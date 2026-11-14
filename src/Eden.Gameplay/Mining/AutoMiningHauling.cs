using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Mining;

public interface IPilotAutoMiningHauling
{
    AutoMiningHaulSettings ReadHauling(long pilotId);
    void SaveHauling(long pilotId, int systemId, AutoMiningHaulSettings settings);
    IReadOnlyList<AutoMiningStation> Stations(int systemId);
    AutoMiningHold ReadHold(long pilotId, long shipId);
    IReadOnlyList<InventoryChange> Unload(long pilotId, long shipId, long stationId);
}

public sealed record AutoMiningStation(long Id, string Name);

public sealed record AutoMiningHaulSettings
{
    public const int DefaultThreshold = 95;
    public static AutoMiningHaulSettings Disabled { get; } = new(0, DefaultThreshold);

    public AutoMiningHaulSettings(long stationId, int threshold)
    {
        if (stationId is < 0 or > int.MaxValue || threshold is < 1 or > 100)
        {
            throw new InvalidDataException("Choose a station and a whole hold percentage from 1 to 100.");
        }
        StationId = stationId;
        Threshold = threshold;
    }

    public long StationId { get; }
    public int Threshold { get; }
    public bool Enabled => StationId > 0;
}

public readonly record struct MiningHoldFill(double Capacity, double Used)
{
    public bool Reached(int percent) => Capacity > 0 && Used + InventoryDefinitions.ResourceTolerance >= Capacity * percent / 100;
}

public readonly record struct AutoMiningHold(MiningHoldFill Fill, bool HasOre)
{
    public bool ShouldUnload(int percent, bool unitDoesNotFit) => HasOre && (Fill.Reached(percent) || unitDoesNotFit);

    public static bool IsOre(InventoryItem item) =>
        item is { CategoryId: AsteroidDefinitions.Category, Quantity: > 0 }
        && (item.GroupId == MiningDefinitions.MercoxitGroup || Enum.IsDefined((MiningResourceGroup)item.GroupId));
}
