using Eden.Data.World.Inventory;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Mining;

public sealed partial class MiningTests
{
    [Theory]
    [InlineData(63, 95)]
    [InlineData(63, 100)]
    [InlineData(63, 40)]
    public async Task AutoMiningHaulsTwiceAndResumesThroughNormalSessionTransitions(int unitsPerTrip, int threshold)
    {
        using HaulFixture f = new(unitsPerTrip * .1 + (threshold >= 95 ? .043 : 8.2));
        long pilot = f.Context.Pilot.PilotId;
        Assert.Equal(AutoMiningHaulSettings.DefaultThreshold, f.Status().Hauling.Threshold);
        Assert.IsType<PilotCallResult.Failure>(f.Call("SaveHauling", f.Station + 99, 50L));
        Assert.IsType<PilotCallResult.Failure>(f.Call("SaveHauling", f.Station, 0L));
        Assert.Equal(new AutoMiningHaulSettings(f.Station, threshold), f.Save(threshold).Hauling);
        Assert.True(f.Start().Running);
        await f.Step(1);
        {
            using var metrics = SessionPerformance.Begin("warmed hold status", _ => { }, force: true)!;
            var before = f.Store.ReadHold(pilot, f.Context.Pilot.ShipId);
            Assert.Equal(before, f.Store.ReadHold(pilot, f.Context.Pilot.ShipId));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }

        for (int trip = 1; trip <= 2; trip++)
        {
            await f.WaitFor(() => f.Context.Pilot.Position is null);
            Assert.Equal(f.Station, f.Context.Pilot.DockedLocationId);
            Assert.Equal(
                trip,
                f.Packets.Count(packet =>
                    packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal) && SessionLocation(packet) == f.Station
                )
            );
            await f.Step(1);
            var hangar = f.Ore(f.Station);
            Assert.Equal(unitsPerTrip * trip, hangar.Sum(item => item.Quantity));
            Assert.Empty(f.Ore(f.Context.Pilot.ShipId));
            Assert.Single(f.Inventory.List(pilot, f.Context.Pilot.ShipId), item => item.TypeId == f.UnrelatedType);
            await f.WaitFor(() => f.Context.Pilot.Position is not null);
            Assert.False(f.Context.Flight.InBallpark);
            await f.Step(2);
            Assert.False(f.Context.Flight.PreparingWarp);
            await f.Calls.ProcessAsync(PilotServicesTests.Request("beyonce", "MachoBindObject", [(long)f.Context.Pilot.SystemId, null]));
            await f.WaitFor(() => f.Mining.IsActive(f.Module));
            Assert.InRange(f.Context.Pilot.Position!.DistanceTo(f.Origin), 0, 2500);
            Assert.True(f.Status().Running);
        }

        Assert.Equal(4, f.Packets.Count(packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)));
        Assert.Equal(unitsPerTrip * 2, Assert.Single(f.Ledger.Read(pilot)).Quantity.Collected);
        Assert.Equal(unitsPerTrip * 2, f.Ore(f.Station).Sum(item => item.Quantity));
        Assert.IsType<PilotCallResult.Success>(f.Call("Stop"));
        f.Db.Reopen();
        using (var live = new LiveWorld(new(f.Db.Db.Path), f.Db.Clock))
        {
            Assert.Equal(new AutoMiningHaulSettings(f.Station, threshold), f.Store.ReadHauling(pilot));
            Assert.Equal(unitsPerTrip * 2, f.Ore(f.Station).Sum(item => item.Quantity));
        }

        static long SessionLocation(BlueInstance packet)
        {
            var change = BlueRead.Tuple(BlueRead.Tuple(packet.State, 14)[4], 3);
            return Convert.ToInt64(BlueRead.Tuple(BlueRead.Fields(BlueRead.Tuple(change[1], 2)[1])["locationid"], 2)[1]);
        }
    }

    [Fact]
    public async Task AutoMiningPausesDockedWhenTheTransferFailsAndNeverRetries()
    {
        using HaulFixture f = new(6.343, rejectUnload: true);
        f.Save(95);
        f.Start();
        await f.WaitFor(() => f.Context.Pilot.Position is null);
        await f.Step(1);
        var paused = f.Status();
        Assert.False(paused.Running);
        Assert.Contains("test transfer rejection", paused.Status);
        Assert.Equal(63, Assert.Single(f.Ore(f.Context.Pilot.ShipId)).Quantity);
        Assert.Empty(f.Ore(f.Station));
        await f.Step(60);
        Assert.Null(f.Context.Pilot.Position);
        Assert.Equal(1, f.UnloadAttempts);
    }

    [Fact]
    public async Task AutoMiningPausesWhenTheReturnSiteIsDepleted()
    {
        using HaulFixture f = new(6.343, remainingOre: 63);
        f.Save(95);
        f.Start();
        await f.WaitFor(() => f.Context.Pilot.Position is null);
        await f.WaitFor(() => f.Context.Pilot.Position is not null);
        await f.Calls.ProcessAsync(PilotServicesTests.Request("beyonce", "MachoBindObject", [(long)f.Context.Pilot.SystemId, null]));
        await f.WaitFor(() => !f.Status().Running);
        Assert.Contains("no matching ore in mining range", f.Status().Status);
        Assert.False(f.Mining.IsActive(f.Module));
        Assert.Equal(63, Assert.Single(f.Ore(f.Station)).Quantity);
        Assert.Empty(f.Ore(f.Context.Pilot.ShipId));
    }

    [Fact]
    public async Task AutoMiningYieldsToPlayerNavigationDuringTheHaul()
    {
        using HaulFixture f = new(6.343);
        f.Save(95);
        f.Start();
        await f.WaitFor(() => f.Context.Flight.PreparingWarp);
        await f.Calls.ProcessAsync(PilotServicesTests.Request("beyonce", "CmdStop", []));
        Assert.False(f.Context.Flight.PreparingWarp);
        Assert.False(f.Status().Running);
        Assert.Contains("manual navigation", f.Status().Status);
        await f.Step(60);
        Assert.NotNull(f.Context.Pilot.Position);
        Assert.Empty(f.Ore(f.Station));
        Assert.Equal(63, Assert.Single(f.Ore(f.Context.Pilot.ShipId)).Quantity);
    }

    private sealed class HaulFixture : IDisposable
    {
        public Fixture Db { get; }
        public PilotCallContext Context { get; }
        public MiningHandler Mining { get; }
        public IPersonalMiningLedger Ledger { get; }
        public IPilotInventory Inventory => Db.Inventory;
        public IPilotAutoMiningHauling Store { get; }
        public long Station { get; }
        public SpacePosition Origin { get; }
        public long Module { get; }
        public int UnrelatedType { get; }
        public int UnloadAttempts => _hauling.Attempts;
        public List<BlueInstance> Packets { get; } = [];
        public PilotSessionCalls Calls { get; }
        private readonly PilotSessionTick _tick;
        private readonly PilotSessionOutput _output;
        private readonly IServiceScope _scope;
        private readonly Hauling _hauling;
        private readonly ResidentFitCache _fits;
        private readonly PilotCallRouter _router;
        private readonly AutoMiningHandler _auto;

        public HaulFixture(double oreCapacity, bool rejectUnload = false, int? remainingOre = null)
        {
            Db = new(useTestClock: true);
            _fits = new(new(Db.Db.Path));
            var owner = Db.Db.Store.FindPilot(1000001)!;
            Station = owner.StationId;
            Origin = owner.Home.Position + new SpacePosition(10_000_000, 0, 0);
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Db.Path).CatalogPath, writable: true))
            {
                int holdAttribute = (int)Eden.Gameplay.Dogma.DogmaAttributeId.GeneralMiningHoldCapacity;
                catalog.Database.ExecuteSqlInterpolated(
                    $"INSERT OR IGNORE INTO DogmaAttribute (Id, Name) VALUES ({holdAttribute}, 'generalMiningHoldCapacity')"
                );
                catalog.Database.ExecuteSqlInterpolated(
                    $"INSERT INTO TypeAttribute VALUES ({owner.ShipTypeId},{holdAttribute},{oreCapacity})"
                );
            }
            using (WorldDbContext world = new(Db.Db.Path))
            {
                var rock = world.Set<WorldAsteroid>().Find(Fixture.Asteroid)!;
                rock.X = Origin.X + 1500;
                rock.Y = Origin.Y;
                rock.Z = Origin.Z;
                if (remainingOre is int quantity)
                {
                    rock.Quantity = quantity;
                }

                var spare = world
                    .Set<OwnedItem>()
                    .First(item =>
                        item.OwnerId == owner.PilotId && item.LocationId == owner.StationId && item.TypeId != (int)MiningLaserType.Civilian
                    );
                spare.LocationId = owner.ShipId;
                spare.FlagId = (int)InventoryFlag.Cargo;
                UnrelatedType = spare.TypeId;
                world.SaveChanges();
            }
            (Context, _, _, Module) = Db.Client(initialPosition: Origin, locked: false);
            _scope = Db.Db.Services.CreateScope();
            var services = _scope.ServiceProvider;
            Store = services.GetRequiredService<IPilotAutoMiningHauling>();
            _hauling = new(Store, rejectUnload);
            Ledger = services.GetRequiredService<IPersonalMiningLedger>();
            var targeting = services.GetRequiredService<TargetingHandler>();
            Mining = services.GetRequiredService<MiningHandler>();
            _auto = new AutoMiningHandler(
                Db.Clock,
                services.GetRequiredService<IPilotAutoMining>(),
                services.GetRequiredService<IPilotFitting>(),
                targeting,
                Mining,
                new TestAutoMiningAvailability(true),
                _hauling
            );
            var updates = services.GetRequiredService<PilotUpdateCoordinator>();
            updates.Enter(Context);
            _router = new(
                services.GetServices<IPilotCallHandler>(),
                services.GetServices<ISelectedPilotCallHandler>().Where(handler => handler is not AutoMiningHandler).Append(_auto)
            );
            _output = new(
                Context,
                1000001,
                42,
                packet =>
                {
                    Packets.Add(Assert.IsType<BlueInstance>(packet));
                    return Task.CompletedTask;
                },
                _ => { }
            );
            var operations = services.GetRequiredService<Eden.Infrastructure.Operations.IPilotOperationScope>();
            var skills = services.GetRequiredService<SkillHandler>();
            Calls = new(Context, _output, _router, updates, skills, Db.Clock, _ => { }, 1000001, 42, operations);
            _tick = new(Db.Clock, updates, skills, services.GetRequiredService<IAsteroidField>(), operations, autoMining: _auto);
        }

        public PilotCallResult Call(string method, params object?[] args) =>
            _router.Call(Context, AutoMiningHandler.Service, method, args, _ => { });

        public AutoMiningStatus Status() => Status(Call("GetStatus"));

        public AutoMiningStatus Save(int threshold) => Status(Call("SaveHauling", Station, (long)threshold));

        public AutoMiningStatus Start() => Status(Call("Start"));

        private static AutoMiningStatus Status(PilotCallResult result) =>
            Assert.IsType<AutoMiningStatus>(Assert.IsType<PilotCallResult.Success>(result).Reply);

        public InventoryItem[] Ore(long location) => Inventory.List(Context.Pilot.PilotId, location).Where(AutoMiningHold.IsOre).ToArray();

        public async Task Step(int seconds)
        {
            Db.Clock.Seconds += seconds;
            await _tick.AdvanceAsync(Context, _output, _ => { }, 1000001, 42);
        }

        public async Task WaitFor(Func<bool> condition)
        {
            const int timeout = 300;
            for (int second = 0; second < timeout && !condition(); second++)
            {
                await Step(1);
            }
            Assert.True(condition(), Status().Status);
        }

        public void Dispose()
        {
            _auto.Cancel(Context);
            _scope.Dispose();
            _fits.Dispose();
            Db.Dispose();
        }

        private sealed class Hauling(IPilotAutoMiningHauling store, bool rejectUnload) : IPilotAutoMiningHauling
        {
            public int Attempts { get; private set; }

            public AutoMiningHaulSettings ReadHauling(long pilotId) => store.ReadHauling(pilotId);

            public void SaveHauling(long pilotId, int systemId, AutoMiningHaulSettings settings) =>
                store.SaveHauling(pilotId, systemId, settings);

            public IReadOnlyList<AutoMiningStation> Stations(int systemId) => store.Stations(systemId);

            public AutoMiningHold ReadHold(long pilotId, long shipId) => store.ReadHold(pilotId, shipId);

            public IReadOnlyList<InventoryChange> Unload(long pilotId, long shipId, long stationId)
            {
                Attempts++;
                if (rejectUnload)
                {
                    throw new InvalidDataException("test transfer rejection");
                }
                return store.Unload(pilotId, shipId, stationId);
            }
        }
    }
}
