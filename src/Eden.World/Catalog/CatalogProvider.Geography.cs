using System.Collections.Frozen;
using System.Collections.Immutable;
using Eden.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogProvider
{
    private const long CelestialEstimatedBytes = 192;
    private readonly Lazy<FrozenDictionary<int, string>> _regions;
    private readonly Lazy<FrozenDictionary<int, CatalogConstellationDefinition>> _constellations;
    private readonly Lazy<FrozenDictionary<int, CatalogGateDefinition>> _gates;
    private readonly Lazy<FrozenDictionary<int, string>> _stationNames;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<CatalogStationDefinition>>> _systemStations;
    private readonly Lazy<FrozenDictionary<int, ImmutableArray<CatalogGateDefinition>>> _systemGates;
    private readonly CatalogMemo<int, CatalogCelestialDefinition?> _celestials;
    private readonly CatalogMemo<int, ImmutableArray<CatalogCelestialDefinition>> _systemCelestials;

    public IReadOnlyDictionary<int, string> Regions => _regions.Value;
    public IReadOnlyDictionary<int, CatalogConstellationDefinition> Constellations => _constellations.Value;
    public IReadOnlyDictionary<int, CatalogGateDefinition> Gates => _gates.Value;
    public IReadOnlyDictionary<int, string> StationNames => _stationNames.Value;

    public ImmutableArray<CatalogStationDefinition> StationsInSystem(int systemId) => _systemStations.Value.GetValueOrDefault(systemId, []);

    public ImmutableArray<CatalogGateDefinition> GatesInSystem(int systemId) => _systemGates.Value.GetValueOrDefault(systemId, []);

    public CatalogCelestialDefinition? FindCelestial(long id) =>
        id is >= int.MinValue and <= int.MaxValue
            ? _celestials
                .Read(
                    (int)id,
                    () =>
                    {
                        using CatalogDbContext catalog = new(Identity.Path);
                        var value = CelestialRows(catalog.Set<Celestial>().Where(body => body.Id == id)).SingleOrDefault();
                        return new(value is not null, value, CelestialEstimatedBytes);
                    }
                )
                .Value
            : null;

    public ImmutableArray<CatalogCelestialDefinition> CelestialsInSystem(int systemId) =>
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
