using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class ResidentAdmissionTests
{
    private const int Home = 30000141;
    private const int Station = 60015157;
    private const long Human = 90000001;
    private const long BlueprintPilot = 990000001;
    private const long BlueprintShip = 140990000001;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedBlueprintPublishesOwnedGraphWithoutSqlOrSpeculativeMembership(bool commit)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        live.Economy.Warm();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        var origin = CapsuleerProvisioningCatalog.Origins(catalog).First();
        using CapsuleerShipBlueprints blueprints = new(catalog);
        blueprints.Prepare(catalog, origin.RaceId, HaulingRules.BadgerType, null);
        long account = live.Economy.Inspect(world =>
            world.Pilots.Where(pilot => pilot.Id == Human).Select(pilot => pilot.AccountId).Single()
        );
        int skills;
        int items;
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("resident founder blueprint", _ => { }, force: true, command: commands.Add)!)
        {
            using (var action = live.Economy.Begin(TransactionParticipants.None))
            {
                action.Add(new InventoryOwner { Id = BlueprintPilot });
                action.Add(
                    new Ship
                    {
                        Id = BlueprintShip,
                        OwnerId = BlueprintPilot,
                        Name = "Resident blueprint",
                        TypeId = HaulingRules.BadgerType,
                        Position = new FlightPosition
                        {
                            SystemId = Home,
                            X = 12,
                            Y = 34,
                            Z = 56,
                        },
                    }
                );
                action.Add(
                    new Pilot
                    {
                        Id = BlueprintPilot,
                        AccountId = account,
                        ActiveShipId = BlueprintShip,
                        Name = "Resident blueprint",
                        CorporationId = origin.CorporationId,
                        RaceId = origin.RaceId,
                        BloodlineId = origin.BloodlineId,
                        SchoolId = origin.SchoolId,
                    }
                );
                blueprints.Create(
                    action,
                    catalog,
                    origin.RaceId,
                    HaulingRules.BadgerType,
                    null,
                    BlueprintPilot,
                    BlueprintShip,
                    Station,
                    fixture.Clock.Now,
                    requirePrepared: true
                );
                skills = action.Set<PilotSkill>().Count(skill => skill.PilotId == BlueprintPilot);
                items = action.Set<OwnedItem>().Count(item => item.OwnerId == BlueprintPilot);
                Assert.True(skills > 0);
                Assert.True(items > 0);
                Assert.Equal(Home, action.Positions.Find(BlueprintShip)!.SystemId);
                Assert.Null(live.KnownShipSystem(BlueprintShip));
                if (commit)
                {
                    action.Commit();
                }
            }
            Assert.Equal(commit ? Home : (int?)null, live.KnownShipSystem(BlueprintShip));
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        live.ObjectsChanged();
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using WorldDbContext reopened = new(captured.Database);
        Assert.Equal(commit, reopened.Pilots.Any(pilot => pilot.Id == BlueprintPilot));
        Assert.Equal(commit, reopened.Ships.Any(ship => ship.Id == BlueprintShip));
        Assert.Equal(commit ? skills : 0, reopened.Set<PilotSkill>().Count(skill => skill.PilotId == BlueprintPilot));
        Assert.Equal(commit ? items : 0, reopened.Set<OwnedItem>().Count(item => item.OwnerId == BlueprintPilot));
        Assert.Equal(commit ? Home : (int?)null, reopened.Positions.Find(BlueprintShip)?.SystemId);
    }

    [Fact]
    public void WarmBackgroundBatchCreatesAnotherFounderWithoutSqlOrRebuildingItsFit()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerBackgroundPreparationTests.PrepareStartup(fixture);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        var synchronization = host.GetRequiredService<IWorldSynchronization>();
        using CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock);
        var first = Assert.IsType<CapsuleerProvisioningTurn>(
            provisioner.PrepareBackgroundSystem(Home, synchronization, () => true, CancellationToken.None)
        );
        Assert.NotNull(first.CreatedPilotId);
        Assert.True(first.HasMoreWork);
        CapsuleerProvisioningTurn next;
        List<string> commands = [];
        using (var metrics = SessionPerformance.Begin("warm resident population batch", _ => { }, force: true, command: commands.Add)!)
        {
            next = Assert.IsType<CapsuleerProvisioningTurn>(
                provisioner.PrepareBackgroundSystem(Home, synchronization, () => true, CancellationToken.None)
            );
            Assert.NotNull(next.CreatedPilotId);
            Assert.NotEqual(first.CreatedPilotId, next.CreatedPilotId);
            Assert.True(metrics.Commands == 0, string.Join(Environment.NewLine, commands));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (var failed = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
        {
            using (var command = failed.Database.CreateCommand())
            {
                command.CommandText = """
                    CREATE TRIGGER reject_founder BEFORE INSERT ON WorldCapsuleerAgent
                    BEGIN SELECT RAISE(ABORT,'checkpoint founder rejection'); END;
                    """;
                command.ExecuteNonQuery();
            }
            Assert.Throws<SqliteException>(() => failed.Project());
            using WorldDbContext rejected = new(failed.Database);
            Assert.Empty(rejected.Set<SavedCapsuleerAgent>());
            Assert.Null(rejected.Pilots.Find(next.CreatedPilotId!.Value));
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using WorldDbContext reopened = new(captured.Database);
        var pilots = reopened
            .Set<SavedCapsuleerAgent>()
            .Where(agent => agent.CommunitySystemId == Home)
            .Select(agent => agent.PilotId)
            .ToArray();
        Assert.Equal(2, pilots.Length);
        Assert.Contains(first.CreatedPilotId!.Value, pilots);
        Assert.Contains(next.CreatedPilotId!.Value, pilots);
        Assert.Equal(2, reopened.Set<SavedCapsuleerJob>().Count());
        Assert.Equal(2, reopened.Set<PilotWallet>().Count(wallet => pilots.Contains(wallet.PilotId)));
    }

    [Fact]
    public void RejectedFounderBatchRetainsItsIdsAndGrantsExactlyOnceOnRetry()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock);
        long pilot;
        long ship;
        using (var action = live.Economy.Begin(new(systems: [Home])))
        {
            var created = provisioner.Provision(Home, catalog, action, maximumFounders: 1);
            pilot = Assert.IsType<long>(created.CreatedPilotId);
            ship = action.Pilots.Find(pilot)!.ActiveShipId;
            Assert.Single(action.Set<SavedCapsuleerAgent>());
            Assert.Single(action.Set<SavedCapsuleerJob>());
            action.Set<PilotWallet>().Find(pilot)!.Cents = -1;
            Assert.Throws<InvalidDataException>(action.Commit);
            Assert.Null(live.KnownShipSystem(ship));
        }
        live.Economy.Inspect(world =>
        {
            Assert.Empty(world.Set<SavedCapsuleerAgent>());
            Assert.Empty(world.Set<SavedCapsuleerJob>());
            Assert.Null(world.Pilots.Find(pilot));
            Assert.Null(world.Ships.Find(ship));
            return true;
        });
        using (var action = live.Economy.Begin(new(systems: [Home])))
        {
            Assert.Equal(pilot, provisioner.Provision(Home, catalog, action, maximumFounders: 1).CreatedPilotId);
            action.Commit();
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        captured.Project();
        using WorldDbContext reopened = new(captured.Database);
        Assert.Equal(pilot, Assert.Single(reopened.Set<SavedCapsuleerAgent>()).PilotId);
        Assert.Equal(pilot, Assert.Single(reopened.Set<SavedCapsuleerJob>()).PilotId);
        Assert.Equal(ship, reopened.Pilots.Find(pilot)!.ActiveShipId);
        Assert.True(reopened.Set<PilotWallet>().Find(pilot)!.Cents >= 0);
        Assert.NotNull(reopened.Set<SavedHomeStation>().Find(pilot));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task RoutineProjectionPersistsNewResidentFounderBeforeItsDependents(bool disconnect, bool settled)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true, migrateRoutineTables: true);
        long founder;
        long humanShip;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            var crimewatch = host.GetRequiredService<IPilotCrimewatch>();
            using (WorldDbContext saved = new(fixture.Database.Path))
            {
                humanShip = saved.Pilots.Find(Human)!.ActiveShipId;
            }
            host.GetRequiredService<SqliteWorld>().SetPosition(humanShip, new(1, 2, 3), Home);
            using var player = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock);
            using (live.EnterShared())
            {
                using (var action = live.Economy.Begin(new(systems: [Home])))
                {
                    founder = Assert.IsType<long>(provisioner.Provision(Home, catalog, action, maximumFounders: 1).CreatedPilotId);
                    action.Commit();
                }
                live.CapsuleerRoutines.Admit(founder);
                if (settled)
                {
                    live.CapsuleerRoutines.Settle(founder, TimeSpan.TicksPerSecond);
                }
            }
            host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(Home, founder);

            using (WorldDbContext raw = new(fixture.Database.Path) { IsResidentProjection = true })
            {
                Assert.Null(raw.Pilots.Find(founder));
            }
            if (disconnect)
            {
                crimewatch.Disconnect(Human, humanShip);
            }
            else
            {
                using WorldDbContext world = new(fixture.Database.Path);
                Assert.Equal(founder, world.Set<SavedCapsuleerAdmission>().Single().PilotId);
            }
            using (WorldDbContext projected = new(fixture.Database.Path) { IsResidentProjection = true })
            {
                Assert.NotNull(projected.Pilots.Find(founder));
                Assert.NotNull(projected.Set<SavedCapsuleerAdmission>().Find(founder));
                Assert.Equal(
                    settled ? TimeSpan.TicksPerSecond : (long?)null,
                    projected.Set<SavedCapsuleerCoarsePilot>().Find(founder)?.SettledTicks
                );
                Assert.Equal(disconnect ? humanShip : (long?)null, projected.Set<SavedLogoff>().Find(Human)?.ShipId);
            }
            await live.FlushAsync();
        }
        using WorldDbContext reopened = new(fixture.Database.Path);
        Assert.NotNull(reopened.Pilots.Find(founder));
        Assert.NotNull(reopened.Set<SavedCapsuleerAdmission>().Find(founder));
        Assert.Equal(
            settled ? TimeSpan.TicksPerSecond : (long?)null,
            reopened.Set<SavedCapsuleerCoarsePilot>().Find(founder)?.SettledTicks
        );
        Assert.Equal(disconnect ? humanShip : (long?)null, reopened.Set<SavedLogoff>().Find(Human)?.ShipId);
    }
}
