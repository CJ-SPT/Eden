namespace Eden.Gameplay.Inventory;

public sealed record PersonalAssetLocation(long StationId, int SolarSystemId, int TypeId, int ItemCount);

public sealed record PersonalAssetQuery(long? StationId = null, bool IncludeContainers = false);

public interface IPilotAssets
{
    IReadOnlyList<PersonalAssetLocation> Locations(long pilotId);
    IReadOnlyList<InventoryItem> Items(long pilotId, PersonalAssetQuery query);
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
}
