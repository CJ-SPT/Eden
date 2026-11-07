using System.Text.Json;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Drones;
using Eden.Protocol.Flight;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class ExpandedCombatDroneTests
{
    private const int SystemId = 30000144;
    private const int Berserker = 2476;
    private const int Garde = 23561;

    [Fact]
    public void OperationSkillsDetermineHeavySentryAndUnusualBandwidthMediumProfiles()
    {
        using Fixture fixture = new();
        using CatalogDbContext catalog = new(fixture.Base.CatalogPath);
        var cases = new[]
        {
            new DroneCase(2488, CombatDroneKind.Light, DroneSkill.LightDroneOperation),
            new DroneCase(2185, CombatDroneKind.Medium, DroneSkill.MediumDroneOperation),
            new DroneCase(Berserker, CombatDroneKind.Heavy, DroneSkill.HeavyDroneOperation),
            new DroneCase(Garde, CombatDroneKind.Sentry, DroneSkill.SentryDroneInterfacing),
            new DroneCase(33681, CombatDroneKind.Heavy, DroneSkill.HeavyDroneOperation),
            new DroneCase(48744, CombatDroneKind.Medium, DroneSkill.MediumDroneOperation),
            new DroneCase(60766, CombatDroneKind.Medium, DroneSkill.MediumDroneOperation),
        };
        foreach (var sample in cases)
        {
            var basis = DroneCatalog.Profile(catalog, sample.TypeId);
            Assert.Equal(sample.Kind, basis.Kind);
            Dictionary<DroneSkill, int> skills = new()
            {
                [DroneSkill.LightDroneOperation] = 5,
                [DroneSkill.MediumDroneOperation] = 5,
                [DroneSkill.HeavyDroneOperation] = 5,
                [DroneSkill.SentryDroneInterfacing] = 5,
            };
            skills[sample.Skill] = 2;
            var result = DroneRules.ApplySkills(basis, skills, new HashSet<int>());
            Assert.Equal(basis.Damage.Total * 1.1, result.Damage.Total, 8);
        }
        Assert.Throws<InvalidDataException>(() => DroneRules.Kind(25, new HashSet<int>()));
        Assert.Throws<InvalidDataException>(() => DroneRules.Kind(double.NaN, new HashSet<int> { (int)DroneSkill.HeavyDroneOperation }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeavyDronesUseRealBandwidthMoveAndReturnTheSameIndividuals(bool resident)
    {
        using Fixture fixture = new();
        using LiveWorld? live = resident ? new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { }) : null;
        var ids = fixture.Launch(Berserker, 2);
        var before = fixture.Drones.List(SystemId, fixture.Now).ToArray();
        Assert.All(before, drone => Assert.Equal(CombatDroneKind.Heavy, drone.Profile.Kind));
        Assert.Equal(50, fixture.Drones.Control(fixture.Pilot).UsedBandwidth);
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.Engage, fixture.Npc.Id, new HashSet<long> { fixture.Npc.Id }, fixture.Now);
        fixture.Tick();
        var after = fixture.Drones.List(SystemId, fixture.Now);
        Assert.All(after, drone => Assert.NotEqual(before.Single(old => old.Id == drone.Id).Position, drone.Position));
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), fixture.Now);
        var changes = fixture.Tick().Changes;
        Assert.Equal(ids.Order(), changes.Select(change => change.Item.Id).Order());
        Assert.Empty(fixture.Drones.List(SystemId, fixture.Now));
        Assert.All(changes, change => Assert.Equal((int)InventoryFlag.DroneBay, change.Item.FlagId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SentriesFireWithoutMovingAndCannotRecallUntilTheirShipReturns(bool resident)
    {
        using Fixture fixture = new();
        using LiveWorld? live = resident ? new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { }) : null;
        var ids = fixture.Launch(Garde, 2);
        var initial = fixture.Drones.List(SystemId, fixture.Now).ToDictionary(drone => drone.Id, drone => drone.Position);
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.Engage, fixture.Npc.Id, new HashSet<long> { fixture.Npc.Id }, fixture.Now);
        fixture.Tick();
        Assert.All(
            fixture.Drones.List(SystemId, fixture.Now),
            drone =>
            {
                Assert.Equal(initial[drone.Id], drone.Position);
                Assert.Equal(SpacePosition.Zero, drone.Velocity);
                Assert.Equal(0, drone.SpeedLimit);
            }
        );
        // One sentry can destroy the Rookie before its flight-mate is processed.
        Assert.Contains(
            fixture.Drones.List(SystemId, fixture.Now),
            drone => drone.ShotTargetId == fixture.Npc.Id && drone.ShotAt == fixture.Now
        );
        var anchor = initial[ids[0]];
        fixture.Base.Db.Store.SetPosition(fixture.Ship, anchor + new SpacePosition(6000, 0, 0), SystemId);
        fixture.Drones.Order(fixture.Pilot, ids, DroneOrder.ReturnToBay, null, new HashSet<long>(), fixture.Now);
        Assert.Empty(fixture.Tick().Changes);
        Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(initial[drone.Id], drone.Position));
        fixture.Base.Db.Store.SetPosition(fixture.Ship, anchor + new SpacePosition(100, 0, 0), SystemId);
        Assert.Equal(ids.Order(), fixture.Tick().Changes.Select(change => change.Item.Id).Order());
        Assert.Empty(fixture.Drones.List(SystemId, fixture.Now));
        live?.Dispose();
        _ = WorldInitialization.Open(fixture.Base.Db.Path);
        Assert.Equal(ids.Order(), fixture.Base.Inventory.List(fixture.Pilot, fixture.Ship).Select(item => item.Id).Order());
    }

    [Fact]
    public void WarmSentriesStayStillWithoutSqlConnectionsOrFittingRebuilds()
    {
        using Fixture fixture = new();
        using LiveWorld live = new(new(fixture.Base.Db.Path), fixture.Base.Clock, _ => { });
        fixture.Launch(Garde, 2);
        fixture.Tick();
        var initial = fixture.Drones.List(SystemId, fixture.Now).ToDictionary(drone => drone.Id, drone => drone.Position);
        using var metrics = SessionPerformance.Begin("stationary sentry frames", _ => { }, force: true);
        for (int frame = 0; frame < 12; frame++)
        {
            fixture.Tick();
            for (int observer = 0; observer < 10; observer++)
            {
                Assert.All(fixture.Drones.List(SystemId, fixture.Now), drone => Assert.Equal(initial[drone.Id], drone.Position));
            }
        }
        Assert.Equal(0, metrics!.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void SentryWireBallsStopWhileControlAndTargetRowsRemainIntactForBothObservers()
    {
        using Fixture fixture = new();
        long stack = fixture.Bay(Garde, 2);
        fixture.Space();
        var pilot = fixture.Base.Db.Store.FindPilot(1000001)!;
        var wing = fixture.Base.Db.Store.FindPilot(1000002)!;
        fixture.Base.Db.Store.SetPosition(wing.ShipId, pilot.Position! + new SpacePosition(2000, 0, 0), SystemId);
        using var ownerScope = fixture.Base.Db.Services.CreateScope();
        using var observerScope = fixture.Base.Db.Services.CreateScope();
        using var owner = DroneSynchronizationTests.Context(fixture, ownerScope.ServiceProvider, 1000001);
        using var observer = DroneSynchronizationTests.Context(fixture, observerScope.ServiceProvider, 1000002);
        owner.Flight.Snapshot();
        observer.Flight.Snapshot();
        _ = fixture.Drones.Launch(fixture.Pilot, [new(stack, 2)], fixture.Now);
        foreach (var session in new[] { owner, observer })
        {
            session.Flight.RefreshDrones();
            Assert.Equal(2, session.Flight.Updates.Count(update => update.Command is DestinyCommand.Stop));
            Assert.DoesNotContain(session.Flight.Updates, update => update.Command is DestinyCommand.Follow);
            Assert.All(session.Flight.VisibleDrones, drone => Assert.Equal(fixture.Ship, new DroneState(drone).Arguments[2]));
        }
        var contacts = fixture.Drones.List(SystemId, fixture.Now);
        var balls = Ballpark.AddDrones(contacts, 7);
        using MemoryStream stream = new(balls.State);
        using BinaryReader reader = new(stream);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(7, reader.ReadInt32());
        foreach (var drone in contacts)
        {
            Assert.Equal(drone.Id, reader.ReadInt64());
            Assert.Equal((byte)NavigationMode.Stop, reader.ReadByte());
            _ = reader.ReadSingle();
            ReadVector(reader);
            _ = reader.ReadByte();
            _ = reader.ReadDouble();
            _ = reader.ReadByte();
            _ = reader.ReadInt64();
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
            Assert.Equal(0, reader.ReadSingle());
            ReadVector(reader);
            _ = reader.ReadSingle();
            Assert.Equal(0, reader.ReadSingle());
            Assert.Equal(-1, reader.ReadSByte());
        }
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MutatedOrAdditionalEffectProfilesRemainUnavailable(bool abyssal)
    {
        using Fixture fixture = new();
        using (CatalogDbContext catalog = new(fixture.Base.CatalogPath, writable: true))
        {
            string data = abyssal
                ? JsonSerializer.Serialize(
                    new
                    {
                        published = 1,
                        metaGroupID = 15,
                        volume = 25,
                    }
                )
                : JsonSerializer.Serialize(
                    new
                    {
                        dogmaEffects = new[]
                        {
                            new { effectID = (int)DogmaEffect.LaserFired, isDefault = 1 },
                            new { effectID = 900041, isDefault = 0 },
                        },
                    }
                );
            string dataset = abyssal ? "client/types" : "client/typedogma";
            catalog.Database.ExecuteSql($"UPDATE SourceRecord SET Data={data} WHERE Dataset={dataset} AND Key={Berserker.ToString()}");
        }
        using CatalogDbContext check = new(fixture.Base.CatalogPath);
        Assert.Throws<InvalidDataException>(() => DroneCatalog.Profile(check, Berserker));
    }

    private static void ReadVector(BinaryReader reader)
    {
        _ = reader.ReadDouble();
        _ = reader.ReadDouble();
        _ = reader.ReadDouble();
    }

    private sealed record DroneCase(int TypeId, CombatDroneKind Kind, DroneSkill Skill);
}
