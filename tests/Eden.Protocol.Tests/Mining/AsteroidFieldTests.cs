using System.Security.Cryptography;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Mining;

public sealed class AsteroidFieldTests
{
    private static void Prepare(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (9,'Asteroid Belt',2);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (15,'Asteroid Belt',9,1);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (40009232,30000144,15,9,10001000000,20000000000,30000000000),
                (40009245,30000144,15,9,10002000000,20000000000,30000000000);
            INSERT INTO ItemCategory(Id,Name) VALUES (25,'Asteroid');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (462,'Veldspar',25),(460,'Scordite',25);
            INSERT INTO ItemType(Id,Name,GroupId,Radius) VALUES (1230,'Veldspar',462,1),(1228,'Scordite',460,1);
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES
                ('client/types','1230','{{"published":1,"marketGroupID":1,"volume":0.1}}'),
                    ('client/types','1228','{{"published":1,"marketGroupID":1,"volume":0.15}}'),
                ('client/asteroidcountbytypeidbeltid','40009232','{{"1230":3,"1228":2}}'),
                ('client/asteroidcountbytypeidbeltid','40009245','{{"1230":3}}'),
                ('client/dogmaattributes','552','{{"defaultValue":100}}');
            """
        );
    }

    [Fact]
    public async Task CatalogCountsCreateOnePersistentFieldAcrossConcurrentLoadsAndRestarts()
    {
        using TestWorld db = new(liveWorld: true);
        Prepare(db);
        var field = db.Services.GetRequiredService<IAsteroidField>();
        var loads = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => field.List(30000144))));
        Assert.Equal(loads[0], loads[1]);
        Assert.Empty(field.List(30000142)); // Jita has no belts in this fixture or the pinned catalog.
        Assert.Equal(8, loads[0].Count);
        Assert.Equal(6, loads[0].Count(a => a.TypeId == 1230));
        Assert.Equal(2, loads[0].Count(a => a.TypeId == 1228));
        Assert.All(loads[0], a => Assert.True(a.Id > int.MaxValue));
        using WorldDbContext world = new(db.Path);
        var saved = world.Set<WorldAsteroid>().OrderBy(a => a.Id).ToArray();
        Assert.Equal(8, saved.Length);
        foreach (var asteroid in saved)
        {
            double volume = asteroid.TypeId == 1230 ? 0.1 : 0.15;
            Assert.InRange(asteroid.Quantity * volume, AsteroidDefinitions.MinimumOreVolume - volume, AsteroidDefinitions.MaximumOreVolume);
            var anchor =
                asteroid.BeltId == 40009232
                    ? new SpacePosition(10001000000, 20000000000, 30000000000)
                    : new SpacePosition(10002000000, 20000000000, 30000000000);
            Assert.InRange(
                new SpacePosition(asteroid.X, asteroid.Y, asteroid.Z).DistanceTo(anchor),
                AsteroidDefinitions.InnerFieldRadius,
                AsteroidDefinitions.OuterFieldRadius + AsteroidDefinitions.FieldThickness
            );
        }
        await LiveWorld.Find(db.Path)!.FlushAsync();
        string checkpoint = Path.Combine(Path.GetDirectoryName(db.Path)!, "asteroid-restart.sqlite");
        File.Copy(db.Path, checkpoint);
        _ = WorldInitialization.Open(checkpoint);
        Assert.Equal(loads[0], new AsteroidFieldStore(new(checkpoint)).List(30000144));
    }

    [Fact]
    public void DepletedRowsDoNotRegenerateBeforeScheduledRenewalAndOreUsesTheCatalogSignatureDefault()
    {
        using TestWorld db = new();
        Prepare(db);
        var field = db.Services.GetRequiredService<IAsteroidField>();
        Assert.Equal(8, field.List(30000144).Count);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldAsteroid SET Quantity=0 WHERE BeltId=40009232");
        }
        Assert.Equal(3, field.List(30000144).Count);
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(3, new AsteroidFieldStore(new(db.Path)).List(30000144).Count);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(8, saved.Set<WorldAsteroid>().Count());
        Assert.Equal(5, saved.Set<WorldAsteroid>().Count(a => a.Quantity == 0));
        Assert.Equal(100, db.Services.GetRequiredService<IPilotFitting>().Attributes(1230)[DogmaAttributeId.SignatureRadius]);
    }

    [Fact]
    public void FlightStreamsNearbyRocksWithoutResettingTheParkAndUsesRigidLocalBalls()
    {
        using TestWorld db = new();
        Prepare(db);
        var systems = db.Services.GetRequiredService<ISystemObjects>();
        var field = db.Services.GetRequiredService<IAsteroidField>();
        var objects = systems.Load(30000144);
        var belts = objects.Where(o => o.GroupId == AsteroidDefinitions.BeltGroup).OrderBy(o => o.Id).ToArray();
        db.Store.SetPosition(1000000000001, belts[0].Position, 30000144);
        Clock clock = new();
        using FlightSession flight = new(db.Store, db.Store.FindPilot(1000001)!, clock, new(), objects);
        flight.Snapshot();
        var rocks = field.List(30000144);
        var nearby = rocks.Where(r => r.Position.DistanceTo(belts[0].Position) < AsteroidDefinitions.VisibilityRange).ToArray();
        Assert.Equal(5, nearby.Length);
        Assert.All(nearby, r => Assert.NotNull(flight.FindTarget(r.Id)));
        Assert.All(rocks.Except(nearby), r => Assert.Null(flight.FindTarget(r.Id)));
        flight.Updates.Clear();
        flight.RefreshAsteroids(rocks.Where(r => r.Id != nearby[0].Id).ToArray());
        var removed = Assert.IsType<object?[]>(Assert.IsType<object?[]>(Assert.Single(flight.WireUpdates()))[1]);
        Assert.Equal("RemoveBalls", removed[0]);
        Assert.Equal(BlueMarshal.Encode(new object?[] { new List<object?> { nearby[0].Id } }), BlueMarshal.Encode(removed[1]));
        flight.Updates.Clear();
        flight.RefreshAsteroids(rocks);
        var add = Assert.IsType<object?[]>(Assert.IsType<object?[]>(Assert.Single(flight.WireUpdates()))[1]);
        Assert.Equal("AddBalls", add[0]);
        var chunk = Assert.IsType<object?[]>(Assert.Single(Assert.IsType<object?[]>(add[1])));
        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(chunk[0])));
        Assert.Equal(0, reader.ReadByte());
        _ = reader.ReadInt32();
        Assert.Equal(nearby[0].Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
        Assert.Equal(nearby[0].Radius, reader.ReadSingle());
        Assert.Equal(nearby[0].Position.X, reader.ReadDouble());
        Assert.Equal(nearby[0].Position.Y, reader.ReadDouble());
        Assert.Equal(nearby[0].Position.Z, reader.ReadDouble());
        Assert.Equal((byte)DestinyBallFlags.Massive, reader.ReadByte());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Assert.NotEmpty(BlueMarshal.Encode(chunk));
        flight.Updates.Clear();
        flight.RefreshAsteroids(rocks);
        Assert.Empty(flight.WireUpdates());
        Assert.Null(flight.CallWire("beyonce", "CmdWarpToStuff", ["item", belts[1].Id]));
        clock.Seconds = 120;
        flight.Checkpoint();
        flight.Updates.Clear();
        flight.RefreshAsteroids(rocks);
        Assert.All(nearby, r => Assert.Null(flight.FindTarget(r.Id)));
        Assert.All(rocks.Except(nearby), r => Assert.NotNull(flight.FindTarget(r.Id)));
        Assert.DoesNotContain(flight.WireUpdates().Cast<object?[]>(), update => Equals(Assert.IsType<object?[]>(update[1])[0], "SetState"));
    }

    [Fact]
    public async Task FullBeltFitsTheEncryptedSessionWhileStartupKeepsItsSmallerLimit()
    {
        using TestWorld db = new();
        Prepare(db);
        var pilot = db.Store.FindPilot(1000001)! with { Position = new(10001000000, 20000000000, 30000000000) };
        var template = db.Services.GetRequiredService<IAsteroidField>().List(30000144)[0];
        // The largest pinned belt contains 186 asteroids. A complete reconnect snapshot
        // and a streamed AddBalls both have to pass through the encrypted transport.
        var rocks = Enumerable.Range(0, 186).Select(index => template with { Id = template.Id + index }).ToArray();
        object[] messages =
        [
            Assert.IsType<BlueInstance>(
                WireAssertions.Materialize(Ballpark.Snapshot(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null), 1, rocks))
            ),
            Assert.IsType<object?[]>(WireAssertions.Materialize(Ballpark.AddObjects(rocks, 1))),
        ];
        using var aes = Aes.Create();
        foreach (var message in messages)
        {
            Assert.True(BlueMarshal.Encode(message).Length > 16 * 1024);
            using MemoryStream stream = new();
            EncryptedPackets packets = new(stream, aes, default);
            await packets.WriteAsync(message);
            stream.Position = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => PacketStream.ReadAsync(stream, default));
            stream.Position = 0;
            Assert.NotNull(await packets.ReadAsync());
        }
    }

    [Fact]
    public void VersionFourteenMigrationAddsAnEmptyFieldWithoutChangingPilotOrCargo()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        var cargo = Assert
            .Single(inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, (int)InventoryFlag.Cargo, null))
            .Item;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema
                    + "DROP TABLE WorldAsteroid; DROP TABLE WorldShipEnergy; DROP TABLE WorldMarketTrade; DROP TABLE "
                    + "WorldMarketOrder; DROP TABLE WorldMarketStation; DROP TABLE WorldWallet; DROP TABLE "
                    + "WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE "
                    + "WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE WorldNpcBounty; DROP "
                    + "TABLE WorldWeaponCooldown; DELETE FROM WorldItem WHERE TypeId=3638; PRAGMA user_version=14"
            );
        }
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        Assert.Equal(cargo, Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)));
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<WorldAsteroid>());
        Assert.Equal(4, saved.Set<OwnedItem>().Count());
    }
}
