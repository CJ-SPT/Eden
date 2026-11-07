using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CatalogGeographyTests
{
    [Fact]
    public void FixtureEditsInvalidateLocationLookupsAndSystemPagesTogether()
    {
        using TestWorld world = new();
        string path = new WorldDatabase(world.Path).CatalogPath;
        var original = CatalogProvider.Open(path).FindCelestial(40009087)!;
        using (CatalogDbContext fixture = new(path, writable: true))
        {
            fixture.Database.ExecuteSqlRaw("UPDATE Celestial SET X=123 WHERE Id=40009087");
        }
        var provider = CatalogProvider.Open(path);
        var current = provider.FindCelestial(original.Id)!;
        Assert.Equal(123, current.X);
        Assert.Equal(0, original.X);
        Assert.Same(current, provider.CelestialsInSystem(current.SolarSystemId).Single(body => body.Id == current.Id));
    }

    [Fact]
    public void GeographySnapshotsShareIdentityAndCacheMissingLocationsAcrossOperations()
    {
        using TestWorld world = new();
        var provider = CatalogProvider.Open(new WorldDatabase(world.Path).CatalogPath);
        var bodies = provider.CelestialsInSystem(30000142);
        var planet = Assert.Single(bodies, body => body.Id == 40009087);
        Assert.Equal(1620000, planet.Radius);
        Assert.Same(planet, provider.FindCelestial(planet.Id));
        Assert.Null(provider.FindCelestial(int.MaxValue));
        Assert.Null(provider.FindCelestial(long.MaxValue));
        Assert.Equal(30000144, provider.Gates[50001249].DestinationSystemId);
        var station = provider.FindStation(60003760)!;
        Assert.Equal(30000142, station.SolarSystemId);
        Assert.Equal(provider.Systems[station.SolarSystemId].RegionId, station.RegionId);
        Assert.NotEmpty(provider.StationNames[station.Id]);
        using var metrics = SessionPerformance.Begin("warm-geography", _ => { }, force: true)!;
        for (int operation = 0; operation < 3; operation++)
        {
            Assert.Same(planet, provider.FindCelestial(planet.Id));
            Assert.Equal(bodies, provider.CelestialsInSystem(30000142));
            Assert.Null(provider.FindCelestial(int.MaxValue));
            Assert.NotEmpty(provider.StationNames[station.Id]);
        }
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
    }
}
