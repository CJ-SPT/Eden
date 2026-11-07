using System.Diagnostics;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.Contracts;
using Eden.Gameplay.Contracts;
using Eden.World.Catalog;

namespace Eden.World.Contracts;

internal sealed partial class PilotContractStore
{
    public ContractSearchResult Search(long pilotId, ContractSearch search)
    {
        ContractSearchRules.Check(search);
        var timer = Stopwatch.StartNew();
        var result = Read<ContractSearchResult>(
            (world, catalog) =>
            {
                Pilot(world, pilotId);
                long now = Now;
                if (
                    search.Type
                        is not (
                            (int)ContractType.ItemExchange
                            or (int)ContractType.Auction
                            or (int)ContractType.Courier
                            or (int)ContractType.AuctionAndItemExchange
                            or (int)ContractType.All
                        )
                    || search.Availability > 1
                )
                {
                    return new([], 0, 0);
                }
                var rows = world.Set<SavedContract>().Where(c => c.Status == ContractStatus.Outstanding && c.Expires > now);
                if (search.Type is (int)ContractType.ItemExchange or (int)ContractType.Auction or (int)ContractType.Courier)
                {
                    rows = rows.Where(c => (int)c.Type == search.Type);
                }
                else if (search.Type == (int)ContractType.AuctionAndItemExchange)
                {
                    rows = rows.Where(c => c.Type == ContractType.ItemExchange || c.Type == ContractType.Auction);
                }
                rows = search.Availability == 0 ? rows.Where(c => c.AssigneeId == null) : rows.Where(c => c.AssigneeId == pilotId);
                if (search.IssuerId is { } issuer)
                {
                    rows = rows.Where(c => c.IssuerId == issuer);
                }
                if (search.LocationId is { } location)
                {
                    rows = rows.Where(c =>
                        c.StationId == location || c.SystemId == location || c.ConstellationId == location || c.RegionId == location
                    );
                }
                if (search.MinPrice is { } minPrice)
                {
                    rows = rows.Where(c =>
                        (c.Type == ContractType.Auction && c.CurrentBidCents > 0 ? c.CurrentBidCents : c.PriceCents) >= minPrice
                    );
                }
                if (search.EndLocationId is { } end)
                {
                    rows = rows.Where(c =>
                        c.EndStationId == end || c.EndSystemId == end || c.EndConstellationId == end || c.EndRegionId == end
                    );
                }
                if (search.MinCollateral is { } minCollateral)
                {
                    rows = rows.Where(c => (c.Type == ContractType.Auction ? c.BuyoutCents : c.CollateralCents) >= minCollateral);
                }
                if (search.MaxCollateral is { } maxCollateral)
                {
                    rows = rows.Where(c => (c.Type == ContractType.Auction ? c.BuyoutCents : c.CollateralCents) <= maxCollateral);
                }
                if (search.MaxPrice is { } maxPrice)
                {
                    rows = rows.Where(c =>
                        (c.Type == ContractType.Auction && c.CurrentBidCents > 0 ? c.CurrentBidCents : c.PriceCents) <= maxPrice
                    );
                }
                if (search.MinReward is { } minReward)
                {
                    rows = rows.Where(c => c.RewardCents >= minReward);
                }
                if (search.MaxReward is { } maxReward)
                {
                    rows = rows.Where(c => c.RewardCents <= maxReward);
                }
                if (search.MinVolume is { } minVolume)
                {
                    rows = rows.Where(c => c.Volume >= minVolume);
                }
                if (search.MaxVolume is { } maxVolume)
                {
                    rows = rows.Where(c => c.Volume <= maxVolume);
                }
                var candidates = rows.AsEnumerable().Select(c => Details(world, c)).Where(c => Matches(catalog, c, search));
                var ordered = search.Descending
                    ? candidates.OrderByDescending(c => ContractSearchRules.SortKey(c, search.Sort))
                    : candidates.OrderBy(c => ContractSearchRules.SortKey(c, search.Sort));
                var found = ordered.ThenBy(c => c.Contract.Id).Take(ContractSearchRules.MaximumResults).ToArray();
                return new(found.Skip(search.Offset).Take(ContractSearchRules.PageSize).ToArray(), found.Length, 0);
            }
        );
        return result with { Seconds = timer.Elapsed.TotalSeconds };
    }

    private static bool Matches(CatalogDbContext catalog, ContractDetails details, ContractSearch search)
    {
        if (
            search.ExcludeNoBuyout && details.Contract.Type == ContractType.Auction && details.Contract.Auction!.BuyoutCents == 0
            || details.Contract.Type is ContractType.ItemExchange or ContractType.Auction
                && (
                    search.ExcludeTrade && details.Items.Any(i => !i.Offered)
                    || search.ExcludeMultiple && details.Items.Count(i => i.Offered && i.ParentId == null) > 1
                )
            || search.Hint == 2
            || search.Description is { Length: > 0 } description
                && !details.Contract.Title.Contains(description, StringComparison.OrdinalIgnoreCase)
                && !details.Contract.Description.Contains(description, StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }
        if (search.SecurityClasses is { } security)
        {
            double value =
                catalog
                    .Set<SourceRecord>()
                    .Find("client/systems", details.Contract.SystemId.ToString(CultureInfo.InvariantCulture))
                    ?.Read<ClientSystems>()
                    .SecurityStatus
                ?? 0;
            if (!security.Contains((int)ContractSearchRules.SecurityClass(value)))
            {
                return false;
            }
        }
        bool filtered =
            search.ItemTypes is not null
            || search.ItemTypeName is { Length: > 0 }
            || search.CategoryId is not null
            || search.GroupId is not null
            || search.Hint == 1;
        if (details.Contract.Type == ContractType.Courier && filtered)
        {
            return false;
        }
        return !filtered
            || details.Items.Any(i =>
                i.Offered
                && (search.ItemTypes is null || search.ItemTypes.Contains(i.TypeId))
                && (search.CategoryId is null || i.CategoryId == search.CategoryId)
                && (search.GroupId is null || i.GroupId == search.GroupId)
                && (search.Hint == 0 || i.CategoryId == (int)Eden.Gameplay.Inventory.InventoryCategory.Blueprint)
                && (
                    search.ItemTypeName is not { Length: > 0 } name
                    || catalog.Set<ItemType>().Find(i.TypeId)!.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                )
            );
    }
}
