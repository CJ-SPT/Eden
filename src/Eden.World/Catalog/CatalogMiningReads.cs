using Eden.Data.Catalog;
using Eden.Gameplay.Mining;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    internal HashSet<int> MiningSystems() =>
        Set<Celestial>()
            .Where(body => body.GroupId == AsteroidDefinitions.BeltGroup)
            .Select(body => body.SolarSystemId)
            .Distinct()
            .ToHashSet();

    // Read just the distinct populated type IDs. Belt layouts remain in the catalog.
    internal int[] PopulatedBeltOreTypes() =>
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
