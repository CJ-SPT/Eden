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
        "client/factions",
        "client/fighterabilitiesbytype",
        "client/graphicids",
        "client/graphiclocations",
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

    internal static CatalogRuntimeSources Load(CatalogDbContext catalog)
    {
        var datasets = Names.ToDictionary(
            name => name,
            _ => new Dictionary<string, Payload>(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
        string names = JsonSerializer.Serialize(Names);
        var records = catalog.Database.SqlQuery<SourcePayload>(
            $"""
            SELECT Dataset, Key, CAST(Data AS BLOB) AS Data FROM SourceRecord
            WHERE Dataset IN (SELECT value FROM json_each({names}))
            """
        );
        foreach (var record in records)
        {
            datasets[record.Dataset].Add(record.Key, Payload.Store(record.Data));
        }
        return new(datasets.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenDictionary(StringComparer.Ordinal)));
    }

    internal IReadOnlyDictionary<string, Payload> Records(string dataset) =>
        _datasets.TryGetValue(dataset, out var records)
            ? records
            : throw new InvalidOperationException($"Catalog source {dataset} is missing from the prepared runtime.");

    private sealed record SourcePayload(string Dataset, string Key, byte[] Data);

    internal readonly record struct Payload(byte[] Data, int UncompressedLength = 0)
    {
        private const int CompressionThreshold = 2048;
        internal int Length => UncompressedLength == 0 ? Data.Length : UncompressedLength;

        internal static Payload Store(byte[] data)
        {
            if (data.Length < CompressionThreshold)
            {
                return new(data);
            }
            byte[] compressed = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                return
                    BrotliEncoder.TryCompress(data, compressed.AsSpan(0, data.Length), out int written, quality: 1, window: 20)
                    && written < data.Length * 3 / 4
                    ? new(compressed.AsSpan(0, written).ToArray(), data.Length)
                    : new(data);
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
                return Parse(Data);
            }
            byte[] decoded = ArrayPool<byte>.Shared.Rent(UncompressedLength);
            try
            {
                if (!BrotliDecoder.TryDecompress(Data, decoded, out int written) || written != UncompressedLength)
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
