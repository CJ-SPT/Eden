using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Combat;

public sealed class NpcEncounterTests
{
    private static readonly long s_now = DateTime.UtcNow.ToFileTimeUtc();

    internal static void Prepare(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (4,'Material');
            INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (186,'Wreck',2),(18,'Mineral',4);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId,Mass,Radius,Capacity) VALUES (26557,'Frigate Wreck',186,10000,14,27500),
                (34,'Tritanium',18,0,1,0);
            INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','30380','{{"wreckTypeID":26557}}'),
                ('client/types','26557','{{"published":1,"marketGroupID":1,"volume":27500}}'),
                    ('client/types','34','{{"published":1,"marketGroupID":1,"volume":0.01}}');
            INSERT INTO ItemCategory(Id,Name) VALUES (11,'Entity');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (286,'Minor Threat',11),(9,'Asteroid Belt',2);
            INSERT INTO ItemType(Id,Name,GroupId,Mass,Radius) VALUES (30380,'Guristas Rookie',286,1500100,50),(15,'Belt',9,0,1);
            INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,X,Y,Z) VALUES
                (40009232,30000144,15,9,1e10,2e10,3e10),(40009245,30000144,15,9,2e10,2e10,3e10);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (76,'maxTargetRange'),(192,'maxLockedTargets'),(552,'signatureRadius'),
                (564,'scanResolution');
            INSERT OR IGNORE INTO TypeAttribute VALUES (30380,9,20),(30380,263,20),(30380,265,15),(30380,479,2000000),
                (30380,37,440),(30380,552,33);
            INSERT OR IGNORE INTO TypeAttribute VALUES (601,76,27000),(601,192,3),(601,552,52),(601,564,475);
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (51,'speed'),(54,'maxRange'),(64,'damageMultiplier'),
                (114,'emDamage'),(116,'explosiveDamage'),(117,'kineticDamage'),(118,'thermalDamage'),(158,'falloff'),
                (160,'trackingSpeed'),(245,'gfxTurretID'),(247,'entityAttackRange'),(475,'entityAttackDelayMin'),
                (620,'optimalSigRadius');
            INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES (416,'entityFlyRange'),(508,'entityCruiseSpeed'),
                (665,'entityChaseMaxDistance');
            INSERT OR IGNORE INTO TypeAttribute VALUES (30380,416,1250),(30380,508,220),(30380,665,1875);
            INSERT OR IGNORE INTO TypeAttribute VALUES (30380,51,2500),(30380,54,500),(30380,64,0.625),
                (30380,114,0),(30380,116,0),(30380,117,2),(30380,118,2),(30380,158,3000),
                (30380,160,0.365),(30380,245,562),(30380,247,3125),(30380,475,7500),(30380,620,40);
            """
        );
    }

    [Fact]
    public async Task ConcurrentPilotsSharePersistentCatalogNpcsAtPerimeterBelts()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => encounters.List(30000144, s_now))));
        Assert.All(results, list => Assert.Equal(results[0], list));
        Assert.Equal(2, results[0].Count);
        Assert.Empty(encounters.List(30000142, s_now));
        Assert.All(
            results[0],
            n =>
            {
                Assert.Equal(30380, n.TypeId);
                Assert.Equal(286, n.GroupId);
                Assert.Equal(11, n.CategoryId);
                Assert.Equal(1000127, n.CorporationId);
                Assert.Equal(1500100, n.Mass);
                Assert.Equal(50, n.Radius);
                Assert.Equal(440, n.MaxSpeed);
                Assert.Equal(new HullDamage(20, 15, 20), n.Health.Remaining);
                Assert.True(n.Id >= NpcEncounterDefinitions.FirstNpcId);
            }
        );
        _ = WorldInitialization.Open(db.Path);
        Assert.Equal(results[0], new NpcEncounterStore(new(db.Path)).List(30000144, s_now));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(2, world.Set<WorldNpc>().Count());
        Assert.Equal(10000003500, results[0][0].Position.X);
    }

    [Fact]
    public void ReconnectDoesNotHealArmorAndDefeatedRowsDoNotRespawnFromARead()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var initial = encounters.List(30000144, s_now);
        using (WorldDbContext world = new(db.Path))
        {
            var first = world.Set<WorldNpc>().Find(initial[0].Id)!;
            first.ShieldCharge = 0;
            first.ArmorDamage = 5;
            first.StructureDamage = 10;
            world.Set<WorldNpc>().Find(initial[1].Id)!.DestroyedAt = s_now;
            world.SaveChanges();
        }
        _ = WorldInitialization.Open(db.Path);
        var survivor = Assert.Single(encounters.List(30000144, s_now + TimeSpan.TicksPerMinute));
        Assert.Equal(initial[0].Id, survivor.Id);
        Assert.InRange(survivor.Health.Remaining.Shield, 0.1, 20);
        Assert.Equal(10, survivor.Health.Remaining.Armor);
        Assert.Equal(10, survivor.Health.Remaining.Structure);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<WorldNpc>().Count());
    }

    [Fact]
    public void FreeEntityBallCarriesNpcSlimAndHealthInsteadOfAStaticCelestial()
    {
        using TestWorld db = new();
        Prepare(db);
        var npc = db.Services.GetRequiredService<INpcEncounters>().List(30000144, s_now)[0];
        var chunk = Assert.IsType<object?[]>(WireAssertions.Materialize(Ballpark.AddNpcs([npc], 42)));
        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(chunk[0])));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(42, reader.ReadInt32());
        Assert.Equal(npc.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Stop, reader.ReadByte());
        Assert.Equal(npc.Radius, reader.ReadSingle());
        Assert.Equal(npc.Position, new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        Assert.Equal((byte)(DestinyBallFlags.Free | DestinyBallFlags.Interactive | DestinyBallFlags.Massive), reader.ReadByte());
        Assert.Equal(npc.Mass, reader.ReadDouble());
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(-1, reader.ReadInt64());
        Assert.Equal(npc.CorporationId, reader.ReadInt32());
        Assert.Equal(-1, reader.ReadInt32());
        Assert.Equal(npc.MaxSpeed, reader.ReadSingle());
        Assert.Equal(SpacePosition.Zero, new(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()));
        Assert.Equal(npc.Agility, reader.ReadSingle());
        Assert.Equal(0, reader.ReadSingle());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        var slim = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(chunk[1]))).State
        );
        Assert.Equal("Guristas Rookie", slim["name"]);
        Assert.Equal(11, slim["categoryID"]);
        Assert.Null(slim["charID"]);
        Assert.Single(Assert.IsType<Dictionary<long, object?>>(chunk[2]));
        Assert.NotEmpty(BlueMarshal.Encode(chunk));
    }

    [Fact]
    public void NativeTargetingUsesNpcSignatureAndClearsTheLockWhenItLeavesVisibility()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, s_now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, npc.Position + new SpacePosition(2000, 0, 0), npc.SystemId);
        pilot = db.Store.FindPilot(1000001)!;
        Clock clock = new();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), [], encounters: encounters),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        Assert.Single(context.Flight.VisibleNpcs);
        Assert.NotNull(context.Flight.FindTarget(npc.Id));
        using TargetingHandler targeting = new(clock, db.Services.GetRequiredService<IPilotFitting>(), new());
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [npc.Id]));
        clock.Seconds = 5;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(npc.Id));
        Assert.Contains(context.Notifications, n => n.Name == "OnTarget" && Equals(n.Arguments[0], "add"));
        Assert.Null(context.Flight.CallWire("beyonce", "CmdFollowBall", [npc.Id, 500d]));
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.X += NpcEncounterDefinitions.VisibilityRange * 2;
            world.SaveChanges();
        }
        context.Flight.RefreshNpcs();
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(npc.Id));
        Assert.Empty(context.Flight.VisibleNpcs);
        Assert.Null(context.Flight.FindTarget(npc.Id));
        Assert.Contains(context.Notifications, n => n.Name == "OnTarget" && Equals(n.Arguments[0], "lost"));
    }

    [Fact]
    public void EnteringVisibilityStreamsOneNpcWithoutReplacingTheWholeBallpark()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, s_now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        var position = npc.Position + new SpacePosition(NpcEncounterDefinitions.VisibilityRange + 1, 0, 0);
        db.Store.SetPosition(pilot.ShipId, position, npc.SystemId);
        using FlightSession flight = new(db.Store, db.Store.FindPilot(1000001)!, new Clock(), new(), [], encounters: encounters);
        flight.Snapshot();
        Assert.Empty(flight.VisibleNpcs);
        flight.Updates.Clear();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.X += 2;
            world.SaveChanges();
        }
        flight.RefreshNpcs();
        Assert.Single(flight.VisibleNpcs);
        var update = Assert.IsType<object?[]>(Assert.Single(flight.WireUpdates()));
        var method = Assert.IsType<object?[]>(update[1]);
        Assert.Equal("AddBalls", method[0]);
        flight.RefreshNpcs();
        Assert.Single(flight.WireUpdates());
        Assert.NotEmpty(BlueMarshal.Encode(update));
    }

    [Fact]
    public void PlayerApproachTracksTheNpcsCurrentPositionInsteadOfItsOldAnchor()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, s_now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, npc.Position - new SpacePosition(5500, 0, 0), npc.SystemId);
        Clock clock = new();
        using FlightSession flight = new(db.Store, db.Store.FindPilot(1000001)!, clock, new(), [], encounters: encounters);
        flight.Snapshot();
        Assert.Null(flight.CallWire("beyonce", "CmdFollowBall", [npc.Id, 500d]));
        var moved = npc.Position + new SpacePosition(10000, 0, 0);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.X = moved.X;
            world.SaveChanges();
        }
        flight.RefreshNpcs();
        clock.Seconds = 100;
        flight.Checkpoint();
        var current = encounters.List(npc.SystemId, clock.Now).Single(n => n.Id == npc.Id);
        double range = flight.Pilot.Position!.DistanceTo(current.Position) - npc.Radius - pilot.Flight.ShipRadius;
        // Inertial overshoot can bump and displace the NPC before the course settles.
        Assert.InRange(range, 450, 550);
        Assert.True(flight.Pilot.Position.DistanceTo(npc.Position) > 9000);
    }

    [Fact]
    public void ApproachCrossesTheVirtualBeltMarkerInsteadOfCollidingWithItsAnchor()
    {
        using TestWorld db = new();
        Prepare(db);
        var encounters = db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, s_now)[0];
        var pilot = db.Store.FindPilot(1000001)!;
        db.Store.SetPosition(pilot.ShipId, npc.Position - new SpacePosition(5500, 0, 0), npc.SystemId);
        StaticSpaceObject belt = new(40009232, 15, 9, 2, 1, npc.Position - new SpacePosition(3500, 0, 0), 1);
        Clock clock = new();
        using FlightSession flight = new(db.Store, db.Store.FindPilot(1000001)!, clock, new(), [belt], encounters: encounters);
        flight.Snapshot();
        Assert.Null(flight.CallWire("beyonce", "CmdFollowBall", [npc.Id, 500d]));
        clock.Seconds = 100;
        flight.Checkpoint();
        var current = encounters.List(npc.SystemId, clock.Now).Single(n => n.Id == npc.Id);
        double distance = flight.Pilot.Position!.DistanceTo(current.Position) - pilot.Flight.ShipRadius - npc.Radius;
        // Follow retains inertia and can push the NPC. The belt marker remains passable.
        Assert.InRange(distance, 450, 550);
        var chunk = Assert.IsType<object?[]>(WireAssertions.Materialize(Ballpark.AddObjects([belt], 42)));
        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(chunk[0])));
        reader.ReadBytes(sizeof(byte) + sizeof(int) + sizeof(long) + sizeof(byte) + sizeof(float) + 3 * sizeof(double));
        Assert.Equal((byte)DestinyBallFlags.Global, reader.ReadByte());
    }

    [Fact]
    public void VersionEighteenMigrationRetainsSavedShipHealthAndDoesNotSpawnUntilVisited()
    {
        using TestWorld db = new();
        Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema
                    + """
                    INSERT INTO WorldShipHealth VALUES (1000000000001,10,20,30,134000000000000000);
                    DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP
                        TABLE WorldNpc; DROP TABLE WorldNpcBounty; DROP TABLE WorldWeaponCooldown; DELETE FROM WorldItem WHERE TypeId=3638;
                    PRAGMA user_version=18;
                    """
            );
        }
        _ = WorldInitialization.Open(db.Path);
        using WorldDbContext migrated = new(db.Path);
        Assert.Empty(migrated.Set<WorldNpc>());
        Assert.Equal(20, migrated.Set<ShipHealthState>().Single().ArmorDamage);
        Assert.Equal(2, db.Services.GetRequiredService<INpcEncounters>().List(30000144, s_now).Count);
    }
}
