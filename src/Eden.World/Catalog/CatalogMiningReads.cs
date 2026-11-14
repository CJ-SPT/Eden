using Eden.Gameplay.Mining;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
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
}
