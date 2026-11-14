using Eden.Data.World.Agents;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed partial class MissionStructureTests
{
    [Fact]
    public void DestroyedMissionStructuresNeverScheduleRegionalRespawnsAndWarmObserverReadsStaySqlFree()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = Hidden(f);
        var warehouse = Assert.Single(Structures(f, mission), n => n.TypeId == WarehouseType);
        f.Kill(warehouse.Id);
        f.Clock.Seconds += NpcEncounterDefinitions.RespawnSeconds + 1;
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(long.MaxValue, NpcPopulationSchedule.Next(world, mission.SystemId, f.Clock.Now));
        }
        var encounters = f.Services.GetRequiredService<INpcEncounters>();
        _ = encounters.List(mission.SystemId, f.Clock.Now);
        var sites = f.Missions.Sites(mission.SystemId);
        using var measured = SessionPerformance.Begin("warm destroyed mission structure", _ => { }, force: true)!;
        for (int tick = 0; tick < 4; tick++)
        {
            f.Clock.Seconds++;
            Assert.DoesNotContain(encounters.List(mission.SystemId, f.Clock.Now), n => n.Id == warehouse.Id);
            Assert.Same(sites, f.Missions.Sites(mission.SystemId));
        }
        Assert.Equal(0, measured.Commands);
        Assert.Equal(0, measured.Connections);
        Assert.Equal(0, measured.FittingCalculations);
    }

    [Fact]
    public void FailedStructureCheckpointRetriesWithoutDuplicatingResidentAcceptance()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var offer = Hidden(f, accept: false);
        var live = f.Services.GetRequiredService<LiveWorld>();
        f.Accept(offer);
        using (var failed = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using WorldDbContext unchanged = new(failed.Database);
            var original = unchanged.Set<SavedMission>().AsNoTracking().SingleOrDefault(row => row.Id == offer.Id)?.State;
            using var command = failed.Database.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER Reject_Structure_Acceptance BEFORE INSERT ON WorldMissionStructure
                    BEGIN SELECT RAISE(ABORT,'injected structure membership failure'); END;
                """;
            command.ExecuteNonQuery();
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => failed.Project());
            Assert.Empty(unchanged.Set<SavedMissionStructure>());
            Assert.Equal(original, unchanged.Set<SavedMission>().AsNoTracking().SingleOrDefault(row => row.Id == offer.Id)?.State);
        }
        Assert.Equal(MissionState.Accepted, Current(f).State);
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        using var retry = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        retry.Project();
        using WorldDbContext saved = new(retry.Database);
        Assert.Equal(5, saved.Set<SavedMissionStructure>().Count());
        Assert.Equal(MissionState.Accepted, saved.Set<SavedMission>().Find(offer.Id)!.State);
    }

    [Fact]
    public void StructureMotionAndCollisionStayFixedWhileShipsReactAndNativeBallIsRigidWithHealth()
    {
        DamageVector resonance = new(1, 1, 1, 1);
        HullDefense defense = new(100, 100, 100, 100000, resonance, resonance, resonance);
        IReadOnlyDictionary<DogmaAttributeId, double> attributes = new Dictionary<DogmaAttributeId, double>
        {
            [DogmaAttributeId.MaxVelocity] = 100,
            [DogmaAttributeId.Agility] = 1,
        };
        LiveCombatNpc structure = new(
            1,
            WarehouseType,
            30000144,
            SpacePosition.Zero,
            new(100, 0, 0),
            100,
            1000,
            new(defense, defense.Undamaged, 0),
            true,
            new Dictionary<long, Eden.Gameplay.Characters.CombatParty>(),
            NpcRole.MissionStructure,
            1,
            0,
            1000124,
            500011,
            SpacePosition.Zero,
            attributes
        )
        {
            GroupId = MissionStructureRules.LargeCollidableStructureGroup,
            CategoryId = (int)InventoryCategory.Entity,
            Mass = 1_000_000,
        };
        var stopped = LiveSystemMotionRules.Move(
            structure,
            attributes,
            new(2, 601, 237, 6, 1, new(10000, 0, 0), 10),
            0,
            TimeSpan.TicksPerSecond,
            1
        );
        Assert.Equal(SpacePosition.Zero, stopped.Position);
        Assert.Equal(SpacePosition.Zero, stopped.Velocity);
        Assert.Equal(NpcNavigationState.Idle, stopped.Navigation);
        FlightProperties hull = new(0, 10, 1_000_000, 1, 100, 1);
        NavigationState navigation = new();
        navigation.SetVelocity(new(100, 0, 0));
        var result = LiveSystemMotionRules.Advance(
            1,
            TimeSpan.TicksPerSecond,
            [new ShipMotion(2, 30000144, 0, new(-105, 0, 0), hull, navigation)],
            [],
            [stopped],
            new Dictionary<long, LiveAggression>(),
            new Dictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> { [stopped.Id] = attributes }
        );
        Assert.Equal(SpacePosition.Zero, Assert.Single(result.Npcs).Position);
        Assert.Equal(SpacePosition.Zero, Assert.Single(result.Npcs).Velocity);
        Assert.Contains(result.Collisions, c => c.Id == 2 && c.Bumped);
        Assert.False(Assert.Single(result.Collisions, c => c.Id == 1).Bumped);

        var contact = new NpcContact(
            1,
            WarehouseType,
            MissionStructureRules.LargeCollidableStructureGroup,
            (int)InventoryCategory.Entity,
            1000124,
            "Warehouse",
            30000144,
            SpacePosition.Zero,
            1_000_000,
            100,
            0,
            1,
            new(1, defense, defense.Undamaged),
            Role: NpcRole.MissionStructure
        );
        var packet = Ballpark.AddNpcs([contact], 10);
        using BinaryReader reader = new(new MemoryStream(packet.State));
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(10, reader.ReadInt32());
        Assert.Equal(contact.Id, reader.ReadInt64());
        Assert.Equal((byte)NavigationMode.Rigid, reader.ReadByte());
        Assert.Equal(contact.Radius, reader.ReadSingle());
        Assert.Equal(0, reader.ReadDouble());
        Assert.Equal(0, reader.ReadDouble());
        Assert.Equal(0, reader.ReadDouble());
        Assert.Equal((byte)DestinyBallFlags.Massive, reader.ReadByte());
        Assert.Equal(-1, reader.ReadSByte());
        Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        Assert.Equal(contact.Health.DamageState(), packet.Damage[contact.Id]);
        Assert.NotEmpty(BlueMarshal.Encode(packet));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequiredStructuresAndEnemiesMustBothBeDestroyedInEitherOrder(bool structuresFirst)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = Hidden(f);
        var room = f.Missions.Sites(mission.SystemId).Single(s => s.Position != mission.Position);
        f.Missions.Arrive(mission.SystemId, room.Position);
        long[] enemies;
        long[] structures;
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (var row in world.Set<SavedMissionStructure>().Where(s => s.MissionId == mission.Id))
            {
                row.Required = true;
                row.CompletesMission = false;
            }
            foreach (var row in world.Set<SavedMissionMember>().Where(s => s.MissionId == mission.Id))
            {
                row.Required = true;
            }
            world.SaveChanges();
            structures = world.Set<SavedMissionStructure>().Where(s => s.MissionId == mission.Id).Select(s => s.NpcId).ToArray();
            enemies = world.Set<SavedMissionMember>().Where(s => s.MissionId == mission.Id).Select(s => s.NpcId).ToArray();
        }
        var first = structuresFirst ? structures : enemies;
        var second = structuresFirst ? enemies : structures;
        foreach (long id in first)
        {
            f.Kill(id);
            Assert.False(Current(f).CombatComplete);
        }
        foreach (long id in second[..^1])
        {
            f.Kill(id);
            Assert.False(Current(f).CombatComplete);
        }
        f.Kill(second[^1]);
        Assert.True(Current(f).CombatComplete);
        Assert.False(Current(f).ObjectiveComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonmentAndExactExpiryRetireStructuresAndDeleteOnlyUnclaimedRecoveryCargo(bool expire)
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = Hidden(f);
        var warehouse = Assert.Single(Structures(f, mission), n => n.TypeId == WarehouseType);
        Move(f, mission.SystemId, warehouse.Position);
        f.Kill(warehouse.Id);
        long recovery = MissionContainerRules.Id(mission.Id, 0);
        var containers = f.Services.GetRequiredService<IPilotMissionContainers>();
        var cargo = Assert.Single(containers.Read(MissionFixture.PilotId, recovery).Items);
        containers.Take(MissionFixture.PilotId, f.Context.Pilot.ShipId, new([cargo.Id], recovery, (int)InventoryFlag.Cargo, 1));
        if (expire)
        {
            f.Clock.AdvanceWallTime(MissionRules.MissionLifetime);
        }
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, expire ? MissionAction.View : MissionAction.Quit, mission.Id);
        Assert.Empty(Structures(f, mission));
        Assert.Throws<InvalidDataException>(() => containers.Read(MissionFixture.PilotId, recovery));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<OwnedItem>().Where(i => i.LocationId == recovery));
        Assert.Equal(1, world.Set<OwnedItem>().Where(i => i.TypeId == ObjectiveType).Sum(i => i.Quantity));
        Assert.Equal(expire ? MissionState.Failed : MissionState.Quit, world.Set<SavedMission>().Find(mission.Id)!.State);
    }

    [Theory]
    [InlineData(125)]
    [InlineData(126)]
    public void ExistingSaveUpgradeAddsStructuresAndNullableNpcNamesWithoutChangingExistingMissionOrItems(int version)
    {
        Assert.Equal(8, (int)NpcRole.Concord);
        Assert.Equal(9, (int)NpcRole.MissionStructure);
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var mission = f.Accept(f.Request());
        int items;
        using (WorldDbContext world = new(f.Db.Path))
        {
            items = world.Set<OwnedItem>().Count();
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldMissionStructure;
                ALTER TABLE WorldNpc DROP COLUMN Name;
                """
            );
            string pragma = version switch
            {
                125 => "PRAGMA user_version=125",
                126 => "PRAGMA user_version=126",
                _ => throw new ArgumentOutOfRangeException(nameof(version)),
            };
            world.Database.ExecuteSqlRaw(pragma);
        }
        for (int reopen = 0; reopen < 2; reopen++)
        {
            WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
            Assert.Equal(mission, Current(f));
            using WorldDbContext world = new(f.Db.Path);
            Assert.Equal(items, world.Set<OwnedItem>().Count());
            Assert.Empty(world.Set<SavedMissionStructure>());
            Assert.All(world.Set<WorldNpc>(), n => Assert.Null(n.Name));
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
    }
}
