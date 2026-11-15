using Eden.Gameplay.Market;

namespace Eden.Gameplay.Inventory;

public sealed record PersonalAssetLocation(long StationId, int SolarSystemId, int TypeId, int ItemCount);

public sealed record PersonalAssetName(long ItemId, string? Name);

public sealed record PersonalAssetQuery(long? StationId = null, bool IncludeContainers = false);

public sealed record PersonalAssetWorth(decimal AssetsIsk, decimal PlexIsk);

public sealed record PersonalAssetHolding(long Id, long LocationId, int TypeId, int Quantity, bool Singleton);

public interface IPilotAssets
{
    IReadOnlyList<PersonalAssetLocation> Locations(long pilotId);
    IReadOnlyList<InventoryItem> Items(long pilotId, PersonalAssetQuery query);
    IReadOnlyList<PersonalAssetName> StationItemNames(long pilotId, IReadOnlySet<long> itemIds);
    PersonalAssetWorth Worth(long pilotId);
}

// Build 3662168 inventorycommon.const and eveAssets.assetSearching.GetSearchResults.
public enum PersonalAssetContainerGroup
{
    Cargo = 12,
    SecureCargo = 340,
    AuditLogSecure = 448,
    Freight = 649,
}

public static class PersonalAssetRules
{
    private const int ContainerContentsFlag = 0;

    public static IReadOnlyList<InventoryItem> Select(
        IReadOnlyList<InventoryItem> items,
        IReadOnlySet<long> stations,
        bool includeContainers
    )
    {
        var selected = items.Where(i => stations.Contains(i.LocationId) && i.FlagId == (int)InventoryFlag.Hangar).ToList();
        if (includeContainers)
        {
            var children = items.ToLookup(i => i.LocationId);
            HashSet<long> visited = selected.Select(i => i.Id).ToHashSet();
            Queue<InventoryItem> pending = new(selected.Where(IsContainer));
            while (pending.TryDequeue(out var container))
            {
                foreach (var child in children[container.Id])
                {
                    if (
                        child.FlagId is not (ContainerContentsFlag or (int)InventoryFlag.Locked or (int)InventoryFlag.Unlocked)
                        || !visited.Add(child.Id)
                    )
                    {
                        continue;
                    }
                    selected.Add(child);
                    if (IsContainer(child))
                    {
                        pending.Enqueue(child);
                    }
                }
            }
        }
        return selected.OrderBy(i => i.Id).ToArray();
    }

    private static bool IsContainer(InventoryItem item) => item.Singleton && Enum.IsDefined((PersonalAssetContainerGroup)item.GroupId);

    public static IReadOnlyDictionary<int, long> DockedQuantities(
        IReadOnlyList<PersonalAssetHolding> holdings,
        IReadOnlySet<long> dockedLocations
    )
    {
        var children = holdings.ToLookup(item => item.LocationId);
        Queue<PersonalAssetHolding> pending = new(dockedLocations.SelectMany(location => children[location]));
        HashSet<long> visited = [];
        Dictionary<int, long> quantities = [];
        while (pending.TryDequeue(out var item))
        {
            if (!visited.Add(item.Id))
            {
                continue;
            }
            quantities[item.TypeId] = quantities.GetValueOrDefault(item.TypeId) + (item.Singleton ? 1 : item.Quantity);
            if (item.Singleton)
            {
                foreach (var child in children[item.Id])
                {
                    pending.Enqueue(child);
                }
            }
        }
        return quantities;
    }

    public static PersonalAssetWorth EstimateWorth(IReadOnlyDictionary<int, long> quantities, IReadOnlyDictionary<int, decimal> prices)
    {
        decimal assets = 0;
        decimal plex = 0;
        foreach (var (typeId, quantity) in quantities)
        {
            decimal value = quantity * prices.GetValueOrDefault(typeId);
            if (typeId == MarketDefinitions.PlexTypeId)
            {
                plex += value;
            }
            else
            {
                assets += value;
            }
        }
        return new(decimal.Round(assets, 2), decimal.Round(plex, 2));
    }
}
