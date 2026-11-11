using Eden.Data.World.Combat;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Docking;

public sealed class MovementPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameSystemCoordinatesAvoidMembershipReadsAndRespectTransactionOutcome(bool commit)
    {
        using TestWorld fixture = new();
        const long ship = 1000000000001;
        const int system = 30000144;
        SpacePosition original = new(1000, 2000, 3000);
        SpacePosition final = new(4000, 5000, 6000);
        fixture.Store.SetPosition(ship, original, system);
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(
                new SavedCrimewatch
                {
                    PilotId = 90000001,
                    SystemId = system,
                    WeaponsUntil = 123456789,
                }
            );
            world.SaveChanges();
        }
        using (WorldDbContext world = new(fixture.Path))
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath))
        using (var transaction = world.Database.BeginTransaction())
        {
            List<string> commands = [];
            using (var metrics = SessionPerformance.Begin("same-system movement", _ => { }, force: true, command: commands.Add)!)
            {
                Assert.False(SqliteWorld.SetPosition(world, catalog, ship, new(2000, 3000, 4000), system, 0));
                Assert.False(SqliteWorld.SetPosition(world, catalog, ship, final, system, 0));
                Assert.Equal(3, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
                Assert.DoesNotContain(commands, sql => sql.Contains("WorldPilot", StringComparison.Ordinal));
                Assert.DoesNotContain(commands, sql => sql.Contains("WorldCrimewatch", StringComparison.Ordinal));
            }
            var position = world.Positions.Find(ship)!;
            Assert.Equal(final, new SpacePosition(position.X, position.Y, position.Z));
            if (commit)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }
        }
        using WorldDbContext reopened = new(fixture.Path);
        var saved = reopened.Positions.Find(ship)!;
        Assert.Equal(commit ? final : original, new SpacePosition(saved.X, saved.Y, saved.Z));
        Assert.Equal(system, saved.SystemId);
        var crimewatch = reopened.Set<SavedCrimewatch>().Find(90000001L)!;
        Assert.Equal(system, crimewatch.SystemId);
        Assert.Equal(123456789, crimewatch.WeaponsUntil);
    }
}
