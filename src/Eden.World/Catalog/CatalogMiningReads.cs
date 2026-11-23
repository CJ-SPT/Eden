using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Mining;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    internal IReadOnlyList<CatalogCelestialDefinition> BeltAnchors(int systemId) =>
        writable
            ? CatalogProvider
                .CelestialRows(
                    Set<Celestial>()
                        .Where(body => body.SolarSystemId == systemId && body.GroupId == AsteroidDefinitions.BeltGroup)
                        .OrderBy(body => body.Id)
                )
                .ToArray()
            : Definitions.BeltAnchors(systemId);

    internal HashSet<int> MiningSystems() =>
        !writable && Definitions.RuntimePrepared ? Definitions.BeltSystems.ToHashSet() : ReadMiningSystems();

    private HashSet<int> ReadMiningSystems() =>
        Set<Celestial>()
            .Where(body => body.GroupId == AsteroidDefinitions.BeltGroup)
            .Select(body => body.SolarSystemId)
            .Distinct()
            .ToHashSet();

    // Read just the distinct populated type IDs. Belt layouts remain in the catalog.
    internal int[] PopulatedBeltOreTypes() =>
        !writable && Definitions.RuntimePrepared
            ? PreparedOreBeltLocations().Select(location => location.OreType).Distinct().ToArray()
            : ReadPopulatedBeltOreTypes();

    private int[] ReadPopulatedBeltOreTypes() =>
        Database
            .SqlQuery<int>(
                $"""
                SELECT DISTINCT CAST(deposit.key AS INTEGER) AS Value
                FROM SourceRecord AS counts
                JOIN Celestial AS belt ON belt.Id=CAST(counts.Key AS INTEGER) AND belt.GroupId={AsteroidDefinitions.BeltGroup}
                JOIN json_each(counts.Data) AS deposit ON deposit.value>0
                WHERE counts.Dataset={CatalogData.Client.Asteroidcountbytypeidbeltid.Name}
                """
            )
            .ToArray();

    internal int[] BeltSystemsWithOreGroup(int oreGroup) =>
        !writable && Definitions.RuntimePrepared
            ? PreparedOreBeltLocations()
                .Where(location => FittingType(location.OreType)?.GroupId == oreGroup)
                .Select(location => location.System)
                .Distinct()
                .ToArray()
            : ReadBeltSystemsWithOreGroup(oreGroup);

    private IEnumerable<(int System, int OreType)> PreparedOreBeltLocations()
    {
        foreach (var (id, entries) in Records(CatalogData.Client.Asteroidcountbytypeidbeltid))
        {
            if (FindCelestial(id) is not { GroupId: AsteroidDefinitions.BeltGroup } belt)
            {
                continue;
            }
            foreach (var (key, count) in entries)
            {
                if (count > 0 && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out int type))
                {
                    yield return (belt.SolarSystemId, type);
                }
            }
        }
    }

    private int[] ReadBeltSystemsWithOreGroup(int oreGroup) =>
        Database
            .SqlQuery<int>(
                $"""
                SELECT DISTINCT belt.SolarSystemId AS Value
                FROM SourceRecord AS counts
                JOIN Celestial AS belt ON belt.Id=CAST(counts.Key AS INTEGER) AND belt.GroupId={AsteroidDefinitions.BeltGroup}
                JOIN json_each(counts.Data) AS deposit ON deposit.type='integer' AND deposit.value>0
                JOIN ItemType AS ore ON ore.Id=CAST(deposit.key AS INTEGER) AND ore.GroupId={oreGroup}
                WHERE counts.Dataset={CatalogData.Client.Asteroidcountbytypeidbeltid.Name}
                  AND deposit.key NOT GLOB '*[^0-9]*'
                """
            )
            .ToArray();
}
