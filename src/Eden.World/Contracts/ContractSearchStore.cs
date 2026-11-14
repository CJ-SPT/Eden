using System.Diagnostics;
using System.Linq.Expressions;
using Eden.Data.Catalog;
using Eden.Data.World.Contracts;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

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
                bool known =
                    LiveWorld.Find(database.Path)?.OwnerDirectory.Snapshot.Pilots.ContainsKey(pilotId)
                    ?? world.Pilots.Any(pilot => pilot.Id == pilotId);
                if (!known)
                {
                    throw new InvalidDataException("Unknown pilot.");
                }
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
                    || search.Hint == 2
                )
                {
                    return new([], 0, 0);
                }
                var rows = world.Filter<SavedContract>(c => c.Status == ContractStatus.Outstanding && c.Expires > now);
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
                rows = FilterItems(world, catalog, rows, search);
                bool managedFilter = search.Description is { Length: > 0 } || search.SecurityClasses is not null;
                IEnumerable<SavedContract> candidates =
                    search.Sort == ContractSort.Volume ? rows
                    : managedFilter ? Order(rows, search)
                    : Order(rows, search).Take(ContractSearchRules.MaximumResults);
                if (search.Description is { Length: > 0 } description)
                {
                    // SQLite's built-in case folding is ASCII-only. Preserve the existing Unicode substring behavior.
                    candidates = candidates.Where(c =>
                        c.Title.Contains(description, StringComparison.OrdinalIgnoreCase)
                        || c.Description.Contains(description, StringComparison.OrdinalIgnoreCase)
                    );
                }
                if (search.SecurityClasses is { } security)
                {
                    Dictionary<int, bool> systems = [];
                    candidates = candidates.Where(c =>
                    {
                        if (!systems.TryGetValue(c.SystemId, out bool allowed))
                        {
                            double level = catalog.Read(CatalogData.Client.Systems, (long)c.SystemId)?.SecurityStatus ?? 0;
                            allowed = security.Contains((int)ContractSearchRules.SecurityClass(level));
                            systems.Add(c.SystemId, allowed);
                        }
                        return allowed;
                    });
                }
                if (search.Sort == ContractSort.Volume)
                {
                    // Retain decimal rounding and ID ties from the existing gameplay sort key.
                    candidates = (
                        search.Descending
                            ? candidates.OrderByDescending(c => (decimal)c.Volume)
                            : candidates.OrderBy(c => (decimal)c.Volume)
                    ).ThenBy(c => c.Id);
                }
                var found = candidates.Take(ContractSearchRules.MaximumResults).ToArray();
                var page = found.Skip(search.Offset).Take(ContractSearchRules.PageSize).ToArray();
                return new(PageDetails(world, page), found.Length, 0);
            }
        );
        return result with { Seconds = timer.Elapsed.TotalSeconds };
    }

    private static IQueryable<SavedContract> FilterItems(
        WorldAction world,
        CatalogDbContext catalog,
        IQueryable<SavedContract> rows,
        ContractSearch search
    )
    {
        if (search.ExcludeNoBuyout)
        {
            rows = rows.Where(c => c.Type != ContractType.Auction || c.BuyoutCents != 0);
        }
        var items = world.Filter<SavedContractItem>(_ => true);
        if (search.ExcludeTrade)
        {
            rows = rows.Where(c =>
                (c.Type != ContractType.ItemExchange && c.Type != ContractType.Auction)
                || !items.Any(i => i.ContractId == c.Id && !i.Offered)
            );
        }
        if (search.ExcludeMultiple)
        {
            rows = rows.Where(c =>
                (c.Type != ContractType.ItemExchange && c.Type != ContractType.Auction)
                || items.Count(i => i.ContractId == c.Id && i.Offered && i.ParentId == null) <= 1
            );
        }
        bool filtered =
            search.ItemTypes is not null
            || search.ItemTypeName is { Length: > 0 }
            || search.CategoryId is not null
            || search.GroupId is not null
            || search.Hint == 1;
        if (!filtered)
        {
            return rows;
        }
        var matching = items.Where(i => i.Offered);
        if (search.ItemTypes is { } types)
        {
            matching = matching.Where(i => types.Contains(i.TypeId));
        }
        if (search.CategoryId is { } category)
        {
            matching = matching.Where(i => i.CategoryId == category);
        }
        if (search.GroupId is { } group)
        {
            matching = matching.Where(i => i.GroupId == group);
        }
        if (search.Hint == 1)
        {
            matching = matching.Where(i => i.CategoryId == (int)InventoryCategory.Blueprint);
        }
        if (search.ItemTypeName is { Length: > 0 } name)
        {
            var matchingTypes = catalog
                .Definitions.Items.Values.Where(type => type.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .Select(type => type.Id)
                .ToArray();
            matching = matching.Where(i => Enumerable.Contains(matchingTypes, i.TypeId));
        }
        return rows.Where(c => c.Type != ContractType.Courier && matching.Any(i => i.ContractId == c.Id));
    }

    private static IOrderedQueryable<SavedContract> Order(IQueryable<SavedContract> rows, ContractSearch search)
    {
        Expression<Func<SavedContract, long>> key = search.Sort switch
        {
            ContractSort.Id => c => c.Id,
            ContractSort.Price => c => c.Type == ContractType.Auction && c.CurrentBidCents > 0 ? c.CurrentBidCents : c.PriceCents,
            ContractSort.Expires => c => c.Expires,
            ContractSort.Station => c => c.StationId,
            ContractSort.System => c => c.SystemId,
            ContractSort.Region => c => c.RegionId,
            ContractSort.Constellation => c => c.ConstellationId,
            ContractSort.Type => c => (long)c.Type,
            ContractSort.Reward => c => c.RewardCents,
            ContractSort.Collateral => c =>
                c.Type == ContractType.Auction ? c.BuyoutCents
                : c.Type == ContractType.Courier ? c.CollateralCents
                : 0,
            ContractSort.Assignee => c => c.AssigneeId ?? 0,
            _ => throw new InvalidDataException("Unknown contract sort order."),
        };
        return (search.Descending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ThenBy(c => c.Id);
    }

    private static IReadOnlyList<ContractDetails> PageDetails(WorldAction world, IReadOnlyList<SavedContract> page)
    {
        if (page.Count == 0)
        {
            return [];
        }
        var ids = page.Select(c => c.Id).ToArray();
        var items = world
            .Set<SavedContractItem>()
            .Where(i => Enumerable.Contains(ids, i.ContractId))
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .ToLookup(i => i.ContractId, i => i.Snapshot());
        var bids = world
            .Set<SavedContractBid>()
            .Where(b => Enumerable.Contains(ids, b.ContractId))
            .OrderByDescending(b => b.AmountCents)
            .ThenByDescending(b => b.Id)
            .AsEnumerable()
            .ToLookup(b => b.ContractId, b => b.Snapshot());
        return page.Select(c => new ContractDetails(c.Snapshot(), items[c.Id].ToArray(), bids[c.Id].ToArray())).ToArray();
    }
}
