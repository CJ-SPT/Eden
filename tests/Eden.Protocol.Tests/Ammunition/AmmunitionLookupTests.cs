using Eden.Data.World.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Ammunition;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Ammunition;

public sealed class AmmunitionLookupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MagazineLookupUsesLocationIndexAndMigrationPreservesLoadedRounds(bool migrate)
    {
        using AmmunitionTests.Fixture f = new();
        f.Load(quantity: 50);
        long pilot = f.Pilot.PilotId;
        long station = f.Pilot.StationId;
        const int unrelatedItems = 25000;
        long firstUnrelatedItem;
        using (WorldDbContext world = new(f.Db.Path))
        {
            firstUnrelatedItem = InventoryPersistence.NextId(world) - 1;
            world.Database.ExecuteSql(
                $"""
                WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM numbers WHERE value<{unrelatedItems})
                INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity)
                SELECT {firstUnrelatedItem}+value,{pilot},222,{station},4,1 FROM numbers
                """
            );
            if (migrate)
            {
                world.Database.ExecuteSqlRaw("DROP INDEX IX_WorldItem_LocationId; PRAGMA user_version=134");
                Assert.Contains(Plan(world, f.Gun), line => line.Contains("SCAN", StringComparison.Ordinal));
            }
        }
        if (migrate)
        {
            WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        }
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Contains(Plan(reopened, f.Gun), line => line.Contains("USING INDEX IX_WorldItem_LocationId", StringComparison.Ordinal));
        Assert.Equal(unrelatedItems, reopened.Set<OwnedItem>().Count(item => item.Id > firstUnrelatedItem));
        using var metrics = SessionPerformance.Begin("indexed magazine lookups", _ => { }, force: true)!;
        for (int read = 0; read < 100; read++)
        {
            var loaded = Assert.IsType<OwnedItem>(PilotAmmunitionStore.Contents(reopened, f.Gun));
            Assert.Equal(222, loaded.TypeId);
            Assert.Equal(50, loaded.Quantity);
            Assert.Equal(pilot, loaded.OwnerId);
        }
        Assert.Equal(100, metrics.Commands);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    private static IReadOnlyList<string> Plan(WorldDbContext world, long module)
    {
        world.Database.OpenConnection();
        using var command = world.Database.GetDbConnection().CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT * FROM WorldItem WHERE LocationId=$module LIMIT 2";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$module";
        parameter.Value = module;
        command.Parameters.Add(parameter);
        using var reader = command.ExecuteReader();
        List<string> result = [];
        while (reader.Read())
        {
            result.Add(reader.GetString(3));
        }
        return result;
    }
}
