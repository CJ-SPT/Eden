using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.Catalog.Market;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Storage;

namespace Eden.World.Market;

public sealed class LiveMarketSnapshotCatalog(WorldDatabase database)
{
    private readonly ConcurrentDictionary<int, Lazy<IReadOnlyDictionary<int, LiveMarketOrder[]>>> _regions = new();
    private LiveMarketSnapshotManifest? _manifest;
    internal string Root => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database.Path))!, "market-orders");

    public LiveMarketSnapshotManifest ReadManifest() => ReadManifest(Root);

    internal LiveMarketSnapshotManifest ReadManifest(string root)
    {
        using FileStream stream = new(
            Path.Combine(root, "manifest.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete
        );
        var manifest =
            JsonSerializer.Deserialize<LiveMarketSnapshotManifest>(stream, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid live market manifest.");
        if (
            manifest.FormatVersion != 1
            || !Guid.TryParseExact(manifest.Generation, "N", out _)
            || manifest.CatalogFingerprint != database.Catalog.Identity.SourceFingerprint
            || manifest.ImportedOrders <= 0
            || manifest.Regions is null
            || manifest.ExcludedOrders is null
            || manifest.Regions.Length == 0
            || manifest.Regions.Any(region => region is null || region.RegionId <= 0 || region.Orders <= 0 || region.Sha256?.Length != 64)
            || manifest.Regions.Select(region => region.RegionId).Distinct().Count() != manifest.Regions.Length
            || manifest.Regions.Sum(region => (long)region.Orders) != manifest.ImportedOrders
            || manifest.CapturedAt == default
        )
        {
            throw new InvalidDataException("Live market snapshot is invalid or belongs to a different catalog.");
        }
        foreach (var region in manifest.Regions)
        {
            if (!File.Exists(RegionPath(root, manifest, region.RegionId)))
            {
                throw new InvalidDataException("The live market snapshot is missing a region file.");
            }
        }
        return manifest;
    }

    internal LiveMarketSnapshotManifest Manifest => _manifest ??= ReadManifest();

    internal IReadOnlyDictionary<int, LiveMarketOrder[]> RegionOrdersByType(int regionId) =>
        _regions.GetOrAdd(regionId, id => new(() => LoadRegion(id))).Value;

    private static string RegionPath(string root, LiveMarketSnapshotManifest manifest, int regionId) =>
        Path.Combine(root, manifest.Generation, $"{regionId}.json.gz");

    private IReadOnlyDictionary<int, LiveMarketOrder[]> LoadRegion(int regionId)
    {
        var descriptor = Manifest.Regions.SingleOrDefault(region => region.RegionId == regionId);
        if (descriptor is null)
        {
            return new Dictionary<int, LiveMarketOrder[]>();
        }
        using var file = File.OpenRead(RegionPath(Root, Manifest, regionId));
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Live market region checksum does not match its manifest.");
        }
        file.Position = 0;
        using GZipStream zipped = new(file, CompressionMode.Decompress);
        var orders =
            JsonSerializer.Deserialize<LiveMarketOrder[]>(zipped, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Invalid live market region.");
        if (orders.Length != descriptor.Orders || orders.Select(order => order.SourceOrderId).Distinct().Count() != orders.Length)
        {
            throw new InvalidDataException("Live market region has an invalid order count or duplicate identity.");
        }
        var catalog = database.Catalog;
        var types = catalog.FindMany(CatalogData.Client.Types, orders.Select(order => (long)order.TypeId).Distinct());
        var stations = catalog.Stations;
        var items = catalog.Items;
        foreach (var order in orders)
        {
            var station = stations.GetValueOrDefault(order.StationId);
            var type = types.GetValueOrDefault(order.TypeId);
            if (
                order.SourceOrderId <= 0
                || station is null
                || station.SolarSystemId != order.SystemId
                || station.RegionId != regionId
                || order.RegionId != regionId
                || type is not { Published: 1, MarketGroupID: > 0 }
                || !items.ContainsKey(order.TypeId)
                || order.PriceCents <= 0
                || order.PriceCents > MarketDefinitions.MaximumOrderPrice * MarketDefinitions.CentsPerIsk
                || order.Remaining <= 0
                || order.Entered < order.Remaining
                || order.MinimumVolume < 1
                || order.MinimumVolume > order.Entered
                || order.DurationDays is < 1 or > MarketDefinitions.NpcOrderDays
                || order.Range is < MarketDefinitions.StationRange or > short.MaxValue
                || order.Issued <= 0
                || order.Issued > DateTime.MaxValue.ToFileTimeUtc() - TimeSpan.FromDays(order.DurationDays).Ticks
            )
            {
                throw new InvalidDataException("Live market order is invalid or incompatible with the installed catalog.");
            }
        }
        return orders.GroupBy(order => order.TypeId).ToDictionary(group => group.Key, group => group.ToArray());
    }
}
