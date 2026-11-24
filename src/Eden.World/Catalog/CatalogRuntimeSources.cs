using System.Buffers;
using System.Collections.Frozen;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

internal sealed class CatalogRuntimeSources
{
    // tools/catalog/runtime_policy.py checks this set against the generated types used by World and Server.
    private static readonly string[] Names =
    [
        "client/accountingentrytypes",
        "client/achievementevents",
        "client/achievements",
        "client/agentsinspace",
        "client/ancestries",
        "client/asteroidcountbytypeidbeltid",
        "client/billtypes",
        "client/bloodlines",
        "client/blueprints",
        "client/clonegrades",
        "client/compressibletypes",
        "client/dbuffcollections",
        "client/dogmaattributes",
        "client/dogmaeffects",
        "client/dogmaunits",
        "client/dungeons",
        "client/dynamicitemattributes",
        "client/epicarcs",
        "client/expertsystems",
        "client/factions",
        "client/fighterabilitiesbytype",
        "client/graphicids",
        "client/graphiclocations",
        "client/grouprewards",
        "client/groups",
        "client/iconids",
        "client/localization_fsd_en-us",
        "client/marketgroups",
        "client/missions",
        "client/npccharacters",
        "client/npccorporations",
        "client/paperdolls",
        "client/races",
        "client/regions",
        "client/requiredskillsfortypes",
        "client/schematics",
        "client/schoolmap",
        "client/schools",
        "client/skillplans",
        "client/skinlicenses",
        "client/skins",
        "client/solarsystemcontent",
        "client/spacecomponentsbytype",
        "client/stationoperations",
        "client/stationservices",
        "client/systems",
        "client/typedogma",
        "client/typelist",
        "client/typematerials",
        "client/types",
        "sde/blueprints",
        "sde/dungeons",
        "sde/factions",
        "sde/mapConstellations",
        "sde/mapPlanets",
        "sde/mapRegions",
        "sde/mapSecondarySuns",
        "sde/mapSolarSystems",
        "sde/mapStargates",
        "sde/mapStars",
        "sde/missions",
        "sde/npcCharacters",
        "sde/npcCorporations",
        "sde/npcStations",
        "sde/planetResources",
        "sde/sovereigntyUpgrades",
    ];

    private readonly FrozenDictionary<string, FrozenDictionary<string, Payload>> _datasets;

    private CatalogRuntimeSources(FrozenDictionary<string, FrozenDictionary<string, Payload>> datasets) => _datasets = datasets;

    internal static CatalogRuntimeSources Load(CatalogDbContext catalog, string pinnedSource)
    {
        var datasets = Names
            .Where(name => name != pinnedSource)
            .ToDictionary(name => name, _ => new Dictionary<string, Payload>(StringComparer.Ordinal), StringComparer.Ordinal);
        string names = JsonSerializer.Serialize(datasets.Keys);
        var records = catalog.Database.SqlQuery<SourcePayload>(
            $"""
            SELECT Dataset, Key, CAST(Data AS BLOB) AS Data FROM SourceRecord
            WHERE Dataset IN (SELECT value FROM json_each({names}))
            """
        );
        PayloadPages pages = new();
        foreach (var record in records)
        {
            // Planet definitions are numerous and contain similar fields even
            // below the general threshold. Gameplay reads one system's planets.
            bool planet = record.Dataset == CatalogData.Sde.MapPlanets.Name;
            datasets[record.Dataset].Add(record.Key, Payload.Store(record.Data, pages, planet));
        }
        return new(datasets.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenDictionary(StringComparer.Ordinal)));
    }

    internal IReadOnlyDictionary<string, Payload> Records(string dataset) =>
        _datasets.TryGetValue(dataset, out var records)
            ? records
            : throw new InvalidOperationException($"Catalog source {dataset} is missing from the prepared runtime.");

    private sealed record SourcePayload(string Dataset, string Key, byte[] Data);

    // Most rows are small. Pack their bytes into pages so the source index does
    // not retain a separate managed array for each record.
    internal sealed class PayloadPages
    {
        private const int PageSize = 64 * 1024;
        private byte[] _page = [];
        private int _used;

        internal ReadOnlyMemory<byte> Store(ReadOnlySpan<byte> data)
        {
            if (data.Length > PageSize)
            {
                return data.ToArray();
            }
            if (data.Length > _page.Length - _used)
            {
                _page = new byte[PageSize];
                _used = 0;
            }
            var stored = _page.AsMemory(_used, data.Length);
            data.CopyTo(stored.Span);
            _used += data.Length;
            return stored;
        }
    }

    internal readonly record struct Payload(ReadOnlyMemory<byte> Data, int UncompressedLength = 0)
    {
        private const int CompressionThreshold = 2048;
        private const int PlanetCompressionThreshold = 512;
        internal int Length => UncompressedLength == 0 ? Data.Length : UncompressedLength;

        internal static Payload Store(byte[] data, PayloadPages pages, bool planet)
        {
            if (data.Length < (planet ? PlanetCompressionThreshold : CompressionThreshold))
            {
                return new(pages.Store(data));
            }
            byte[] compressed = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                return
                    BrotliEncoder.TryCompress(data, compressed.AsSpan(0, data.Length), out int written, quality: 1, window: 20)
                    && written < data.Length * 3 / 4
                    ? new(pages.Store(compressed.AsSpan(0, written)), data.Length)
                    : new(pages.Store(data));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }
        }

        internal T Read<T>(string dataset, string key)
        {
            if (UncompressedLength == 0)
            {
                return Parse(Data.Span);
            }
            byte[] decoded = ArrayPool<byte>.Shared.Rent(UncompressedLength);
            try
            {
                if (!BrotliDecoder.TryDecompress(Data.Span, decoded, out int written) || written != UncompressedLength)
                {
                    throw new InvalidDataException($"Invalid prepared {dataset} record {key}.");
                }
                return Parse(decoded.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(decoded);
            }

            T Parse(ReadOnlySpan<byte> bytes) =>
                JsonSerializer.Deserialize<T>(bytes, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException($"Missing {dataset} record {key}.");
        }
    }
}
