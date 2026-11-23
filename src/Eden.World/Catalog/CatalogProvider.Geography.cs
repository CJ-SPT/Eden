using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.Catalog;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

internal readonly record struct CatalogSystemPolitics(double? Security, long? FactionId);

public sealed partial class CatalogProvider
{
    private const long CelestialEstimatedBytes = 192;
    private const int StargateGroup = 10;
    private readonly Lazy<FrozenDictionary<int, string>> _regions;
    private readonly Lazy<FrozenDictionary<int, CatalogConstellationDefinition>> _constellations;
    private readonly Lazy<FrozenDictionary<int, CatalogGateDefinition>> _gates;
    private readonly Lazy<FrozenDictionary<int, CatalogCelestialDefinition>> _gateBodies;
    private readonly Lazy<FrozenDictionary<int, CatalogSystemPolitics>> _systemPolitics;
    private readonly Lazy<SystemSecurityIndex> _systemSecurity;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<CatalogCelestialDefinition>>> _systemBelts;
    private readonly Lazy<FrozenDictionary<int, string>> _stationNames;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<CatalogStationDefinition>>> _systemStations;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<CatalogGateDefinition>>> _systemGates;
    private readonly CatalogMemo<int, CatalogCelestialDefinition?> _celestials;
    private readonly CatalogMemo<int, ImmutableArray<CatalogCelestialDefinition>> _systemCelestials;

    public IReadOnlyDictionary<int, string> Regions => _regions.Value;
    public IReadOnlyDictionary<int, CatalogConstellationDefinition> Constellations => _constellations.Value;
    public IReadOnlyDictionary<int, CatalogGateDefinition> Gates => _gates.Value;
    internal IReadOnlyDictionary<int, double> SystemSecurity => _systemSecurity.Value.Values;
    internal IReadOnlyDictionary<int, CatalogSystemPolitics> SystemPolitics => _systemPolitics.Value;

    internal double? SystemContentSecurity(int systemId)
    {
        var security = _systemSecurity.Value;
        return security.Values.TryGetValue(systemId, out double value) && !security.Missing.Contains(systemId) ? value : null;
    }

    internal IEnumerable<int> BeltSystems => _systemBelts.Value.Keys;

    internal ImmutableArray<CatalogCelestialDefinition> BeltAnchors(int systemId) => _systemBelts.Value.GetValueOrDefault(systemId, []);

    internal void WarmGateBodies() => _ = _gateBodies.Value;

    internal CatalogCelestialDefinition? FindGateBody(long id) =>
        id is >= int.MinValue and <= int.MaxValue ? _gateBodies.Value.GetValueOrDefault((int)id) : null;

    public IReadOnlyDictionary<int, string> StationNames => _stationNames.Value;

    public ImmutableArray<CatalogStationDefinition> StationsInSystem(int systemId) => _systemStations.Value.GetValueOrDefault(systemId, []);

    public ImmutableArray<CatalogGateDefinition> GatesInSystem(int systemId) => _systemGates.Value.GetValueOrDefault(systemId, []);

    internal IReadOnlyDictionary<int, RegionalGateStyle> RegionalGateStyles()
    {
        RequireCurrent();
        return GateStyleRows()
            .Select(body => new { body.SolarSystemId, body.TypeId })
            .Where(body => RegionalGateRules.Style(body.TypeId) is not null)
            .GroupBy(body => Systems[body.SolarSystemId].RegionId)
            .ToDictionary(
                region => region.Key,
                region =>
                    region
                        .GroupBy(body => RegionalGateRules.Style(body.TypeId)!.Value)
                        .OrderByDescending(group => group.Count())
                        .ThenBy(group => group.Key)
                        .First()
                        .Key
            );
    }

    private IEnumerable<CatalogCelestialDefinition> GateStyleRows()
    {
        if (Runtime is { } runtime)
        {
            return runtime.Bodies.Values.Where(body => body.GroupId == StargateGroup);
        }
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        return CelestialRows(catalog.Set<Celestial>().Where(body => body.GroupId == StargateGroup)).ToArray();
    }

    internal IReadOnlyDictionary<int, CatalogCelestialDefinition> FindCelestials(IEnumerable<int> ids)
    {
        if (Runtime is { } runtime)
        {
            return ids.Distinct().Where(runtime.Bodies.ContainsKey).ToDictionary(id => id, id => runtime.Bodies[id]);
        }
        Dictionary<int, CatalogCelestialDefinition> result = [];
        List<int> missing = [];
        foreach (int id in ids.Distinct())
        {
            if (!_celestials.TryRead(id, out var cached))
            {
                missing.Add(id);
            }
            else if (cached.Value is { } body)
            {
                result.Add(id, body);
            }
        }
        if (missing.Count > 0)
        {
            RequireCurrent();
            using CatalogDbContext catalog = new(Identity.Path);
            Interlocked.Increment(ref _loads);
            var rows = CelestialRows(catalog.Set<Celestial>().Where(body => missing.Contains(body.Id))).ToDictionary(body => body.Id);
            foreach (int id in missing)
            {
                var value = _celestials.Read(id, () => new(rows.ContainsKey(id), rows.GetValueOrDefault(id), CelestialEstimatedBytes));
                if (value.Value is { } body)
                {
                    result.Add(id, body);
                }
            }
        }
        return result;
    }

    public CatalogCelestialDefinition? FindCelestial(long id) =>
        id is < int.MinValue or > int.MaxValue ? null
        : Runtime is { } runtime ? runtime.Bodies.GetValueOrDefault((int)id)
        : ReadCelestial(id);

    private CatalogCelestialDefinition? ReadCelestial(long id) =>
        _celestials
            .Read(
                (int)id,
                () =>
                {
                    using CatalogDbContext catalog = new(Identity.Path);
                    var value = CelestialRows(catalog.Set<Celestial>().Where(body => body.Id == id)).SingleOrDefault();
                    return new(value is not null, value, CelestialEstimatedBytes);
                }
            )
            .Value;

    public ImmutableArray<CatalogCelestialDefinition> CelestialsInSystem(int systemId) =>
        Runtime is { } runtime ? runtime.SystemBodies.GetValueOrDefault(systemId, []) : ReadSystemCelestials(systemId);

    private ImmutableArray<CatalogCelestialDefinition> ReadSystemCelestials(int systemId) =>
        _systemCelestials
            .Read(
                systemId,
                () =>
                {
                    using CatalogDbContext catalog = new(Identity.Path);
                    var rows = CelestialRows(
                            catalog.Set<Celestial>().Where(body => body.SolarSystemId == systemId).OrderBy(body => body.Id)
                        )
                        .ToArray();
                    var values = rows.Select(body => _celestials.Read(body.Id, () => new(true, body, CelestialEstimatedBytes)).Value!)
                        .ToImmutableArray();
                    return new(true, values, EntryOverhead + values.Length * CelestialEstimatedBytes);
                }
            )
            .Value;

    internal static IQueryable<CatalogCelestialDefinition> CelestialRows(IQueryable<Celestial> source) =>
        source.Select(body => new CatalogCelestialDefinition(
            body.Id,
            body.SolarSystemId,
            body.TypeId,
            body.GroupId,
            body.Radius,
            body.OrbitId,
            body.X,
            body.Y,
            body.Z
        ));

    private FrozenDictionary<int, string> LoadRegions()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        var rows = catalog.Set<Region>().ToFrozenDictionary(region => region.Id, region => region.Name);
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }

    private FrozenDictionary<int, CatalogConstellationDefinition> LoadConstellations()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        var rows = catalog
            .Set<Constellation>()
            .Select(value => new CatalogConstellationDefinition(value.Id, value.Name, value.RegionId))
            .AsEnumerable()
            .ToFrozenDictionary(value => value.Id);
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }

    private FrozenDictionary<int, CatalogGateDefinition> LoadGates()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        var rows = catalog
            .Set<Stargate>()
            .Select(gate => new CatalogGateDefinition(gate.Id, gate.SolarSystemId, gate.DestinationId, gate.Destination.SolarSystemId))
            .AsEnumerable()
            .ToFrozenDictionary(value => value.Id);
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }

    private FrozenDictionary<int, CatalogCelestialDefinition> LoadGateBodies()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        var bodies = from gate in catalog.Set<Stargate>() join body in catalog.Set<Celestial>() on gate.Id equals body.Id select body;
        var rows = CelestialRows(bodies).AsEnumerable().ToFrozenDictionary(body => body.Id);
        Interlocked.Increment(ref _loads);
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }

    private sealed record SystemPoliticsRow(int Id, double? Security, long? FactionId);

    private FrozenDictionary<int, CatalogSystemPolitics> LoadSystemPolitics()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        // Population and Crimewatch need two scalar fields from the pinned system dataset.
        var rows = catalog
            .Database.SqlQuery<SystemPoliticsRow>(
                $"""
                SELECT CAST(Key AS INTEGER) AS Id, json_extract(Data, '$.securityStatus') AS Security,
                       json_extract(Data, '$.factionID') AS FactionId
                FROM SourceRecord WHERE Dataset={CatalogData.Client.Systems.Name}
                """
            )
            .AsEnumerable()
            .ToFrozenDictionary(row => row.Id, row => new CatalogSystemPolitics(row.Security, row.FactionId));
        Interlocked.Increment(ref _loads);
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }

    private FrozenDictionary<int, ImmutableArray<CatalogCelestialDefinition>> LoadBeltAnchors()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        var belts = CelestialRows(
                catalog.Set<Celestial>().Where(body => body.GroupId == AsteroidDefinitions.BeltGroup).OrderBy(body => body.Id)
            )
            .AsEnumerable()
            .GroupBy(body => body.SolarSystemId)
            .ToFrozenDictionary(group => group.Key, group => group.ToImmutableArray());
        Interlocked.Increment(ref _loads);
        Interlocked.Add(ref _pinnedRecords, belts.Values.Sum(group => group.Length));
        return belts;
    }

    private sealed record SystemSecurityIndex(FrozenDictionary<int, double> Values, FrozenSet<int> Missing);

    private sealed record SystemSecurityRow(int Id, double? Security);

    private SystemSecurityIndex LoadSystemSecurity()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        // Route searches only need security. Leave planet, moon and belt payloads in SQLite.
        var rows = catalog
            .Database.SqlQuery<SystemSecurityRow>(
                $"""
                SELECT CAST(Key AS INTEGER) AS Id, json_extract(Data, '$.security') AS Security
                FROM SourceRecord WHERE Dataset={CatalogData.Client.Solarsystemcontent.Name}
                """
            )
            .ToArray();
        Interlocked.Increment(ref _loads);
        Interlocked.Add(ref _pinnedRecords, rows.Length);
        return new(
            rows.ToFrozenDictionary(row => row.Id, row => row.Security ?? 0),
            rows.Where(row => row.Security is null).Select(row => row.Id).ToFrozenSet()
        );
    }

    private FrozenDictionary<int, string> LoadStationNames()
    {
        RequireCurrent();
        using CatalogDbContext catalog = new(Identity.Path);
        Interlocked.Increment(ref _loads);
        var rows = catalog
            .Set<StationDesignation>()
            .ToArray()
            .ToFrozenDictionary(
                value => value.StationId,
                value =>
                {
                    var station = Stations[value.StationId];
                    return value.Format(Systems[station.SolarSystemId].Name, NpcCorporations[station.OwnerId]);
                }
            );
        Interlocked.Add(ref _pinnedRecords, rows.Count);
        return rows;
    }
}
