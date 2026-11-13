using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;

namespace Eden.World.Structures;

internal static class MoonMiningCatalog
{
    private const int Pochven = 10000070;

    private static readonly CatalogDefinition<ImmutableArray<MoonMiningPoint>> s_points = new("moon-mining-points", Load);

    internal static ImmutableArray<MoonMiningPoint> Points(CatalogDbContext catalog, int systemId) =>
        catalog.Definition(s_points, systemId);

    internal static long BeaconId(int moonId) => 1_000_000_000L + moonId;

    internal static int? SurveyTarget(
        CatalogDbContext catalog,
        int systemId,
        SpacePosition origin,
        SpacePosition heading,
        double maximumRange
    )
    {
        var points = Points(catalog, systemId).Select(point => point.MoonId).ToHashSet();
        var direction = heading.Unit;
        return catalog
            .CelestialsInSystem(systemId)
            .Where(body => points.Contains(body.Id))
            .Select(body => (Body: body, Offset: new SpacePosition(body.X, body.Y, body.Z) - origin))
            .Where(target =>
            {
                double distance = target.Offset.Dot(direction);
                return distance > 0
                    && distance <= maximumRange
                    && (target.Offset - direction * distance).Length <= target.Body.Radius.GetValueOrDefault();
            })
            .OrderBy(target => target.Offset.Length)
            .Select(target => (int?)target.Body.Id)
            .FirstOrDefault();
    }

    private static ImmutableArray<MoonMiningPoint> Load(CatalogDbContext catalog, int systemId)
    {
        if (
            catalog.FindSystem(systemId) is not { RegionId: not Pochven }
            || catalog.Read(CatalogData.Client.Solarsystemcontent, (long)systemId)
                is not { Planets: { } planets, Security: double security }
            || !double.IsFinite(security)
            || security >= MoonMiningRules.MiningSecurityLimit
        )
        {
            return [];
        }

        return planets
            .Values.SelectMany(planet => planet.Moons ?? [])
            .Where(moon => moon.Value.MiningBeacon?.Position is { X: not null, Y: not null, Z: not null })
            .Select(moon =>
            {
                var position = moon.Value.MiningBeacon!.Position!;
                return new MoonMiningPoint(
                    int.Parse(moon.Key, CultureInfo.InvariantCulture),
                    new SpacePosition(position.X!.Value, position.Y!.Value, position.Z!.Value)
                );
            })
            .OrderBy(moon => moon.MoonId)
            .ToImmutableArray();
    }
}
