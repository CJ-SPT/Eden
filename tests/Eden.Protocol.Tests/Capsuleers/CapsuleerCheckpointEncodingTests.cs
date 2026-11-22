using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerCheckpointEncodingTests
{
    private const string WaitingReason = "Minería 矿石 🚀";

    [Theory]
    [InlineData("UTF-8")]
    [InlineData("UTF-16le")]
    [InlineData("UTF-16be")]
    public void BatchedProgressPreservesUnicodeNullCollectionsAndCheckpointOptions(string encoding)
    {
        using var database = OpenDatabase(encoding);
        using var world = new WorldDbContext(database);
        var (hauler, miner) = Progress();
        HaulerCheckpoint haulerCheckpoint = new(1, hauler);
        MinerCheckpoint minerCheckpoint = new(1, miner);
        haulerCheckpoint.Apply(world);
        minerCheckpoint.Apply(world);
        string expectedHauler = world.Database.SqlQueryRaw<string>("SELECT Progress AS Value FROM WorldCapsuleerHauler").Single();
        string expectedMiner = world.Database.SqlQueryRaw<string>("SELECT Progress AS Value FROM WorldCapsuleerCommunity").Single();
        world.Database.ExecuteSqlRaw("UPDATE WorldCapsuleerHauler SET Progress=NULL; UPDATE WorldCapsuleerCommunity SET Progress=NULL;");

        HaulerCheckpoint.Apply(world, [haulerCheckpoint]);
        MinerCheckpoint.Apply(world, [minerCheckpoint]);

        string actualHauler = world.Database.SqlQueryRaw<string>("SELECT Progress AS Value FROM WorldCapsuleerHauler").Single();
        string actualMiner = world.Database.SqlQueryRaw<string>("SELECT Progress AS Value FROM WorldCapsuleerCommunity").Single();
        Assert.Equal(expectedHauler, actualHauler);
        Assert.Equal(expectedMiner, actualMiner);
        var restoredHauler = Assert.IsType<HaulerProgress>(HaulerCheckpoint.Read(actualHauler));
        var restoredMiner = Assert.IsType<MinerProgress>(MinerCheckpoint.Read(actualMiner));
        Assert.Equal(WaitingReason, restoredHauler.WaitingReason);
        Assert.Equal(WaitingReason, restoredMiner.WaitingReason);
        Assert.Equal(WaitingReason, restoredHauler.Navigation.FollowTarget?.Name);
        Assert.Equal(WaitingReason, restoredMiner.Navigation.FollowTarget?.Name);
        Assert.Null(restoredHauler.ReplacementOrderIds);
        Assert.Null(restoredMiner.ReplacementOrderIds);
        Assert.DoesNotContain("\"Collision\"", actualHauler);
        Assert.DoesNotContain("\"Collision\"", actualMiner);
    }

    private static (HaulerProgress, MinerProgress) Progress()
    {
        var point = new SpacePosition(1000, 2000, 3000);
        var target = new StaticSpaceObject(1, 2, 3, 4, 5, point, 10)
        {
            Name = WaitingReason,
            Collision = new([new StaticCollisionPrimitive(point, point, 10)]),
        };
        var navigation = new NavigationState().Capture() with { FollowTarget = target };
        var hauler = new HaulerProgress(HaulerActivity.Flying, 1, 2, 3, 4, 5, navigation, WaitingReason);
        var miner = new MinerProgress(MinerActivity.Mining, 1, 2, 3, 4, 5, 6, navigation, WaitingReason: WaitingReason);
        return (hauler, miner);
    }

    private static SqliteConnection OpenDatabase(string encoding)
    {
        var database = new SqliteConnection("Data Source=:memory:;Pooling=False");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = $"""
            PRAGMA encoding='{encoding}';
            CREATE TABLE WorldCapsuleerHauler(PilotId INTEGER PRIMARY KEY, Progress TEXT);
            CREATE TABLE WorldCapsuleerCommunity(PilotId INTEGER PRIMARY KEY, Progress TEXT);
            INSERT INTO WorldCapsuleerHauler VALUES(1,NULL);
            INSERT INTO WorldCapsuleerCommunity VALUES(1,NULL);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA encoding";
        Assert.Equal(encoding, command.ExecuteScalar());
        return database;
    }
}
