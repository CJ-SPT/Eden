using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed class CapitalTravelTests
{
    private const int Jita = 30000142;
    private const int Jan = 30001385;
    private const int JanStation = 60001714;
    private const int Rhea = 28844;
    private const int Nitrogen = 17888;
    private const int LiquidOzone = 16273;
    private const int Cargo = 34;
    private static readonly SpacePosition s_station = new(-102770810880, 7849451520, 353786634240);

    [Fact]
    public async Task RheaDeliversCargoFromJitaToJanThroughVentureCyno()
    {
        using Fixture fixture = new();
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var beacon = fixture.Light(emitter);
        Assert.Equal(640, Quantity(fixture, emitter, LiquidOzone));
        Assert.IsType<PilotCallResult.Failure>(emitter.Call("dogmaIM", "Deactivate", fixture.Module, "cynosuralGeneration"));
        Assert.IsType<PilotCallResult.Failure>(emitter.Context.Flight.Execute(new FlightCommand.Dock(JanStation, emitter.Pilot.ShipId)));
        Assert.Equal(1, fixture.Travel.BeaconCounts()[Jan]);
        await freighter.TickAsync();
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "OnFleetJumpBeaconModuleChange"));
        Assert.IsType<PilotCallResult.Failure>(freighter.Call("jumpTimers", "GetTimers", emitter.Pilot.PilotId));

        freighter.Packets.Clear();
        Assert.IsType<PilotCallResult.Success>(fixture.Jump(freighter, beacon));
        Assert.IsType<PilotCallResult.Failure>(fixture.Jump(freighter, beacon));
        Assert.Equal(60_000, Quantity(fixture, freighter, Nitrogen));
        Assert.Equal(Jita, freighter.Pilot.SystemId);
        await freighter.TickAsync();
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "effects.JumpDriveOut"));
        Assert.Equal(Jita, freighter.Pilot.SystemId);

        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(Jan, freighter.Pilot.SystemId);
        Assert.Equal(fixture.FreighterId, freighter.Pilot.ShipId);
        Assert.Equal(12_587, Quantity(fixture, freighter, Nitrogen));
        Assert.Equal(14, Quantity(fixture, freighter, Cargo));
        var timers = Assert.IsType<JumpTimers>(fixture.Travel.Timers(freighter.Pilot.PilotId));
        Assert.Equal(
            new(fixture.Clock.Now + 91 * TimeSpan.TicksPerSecond, fixture.Clock.Now + 916 * TimeSpan.TicksPerSecond, fixture.Clock.Now),
            timers
        );
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "OnJumpTimersUpdated"));
        var read = Assert.IsType<PilotCallResult.Success>(freighter.Call("jumpTimers", "GetTimers", freighter.Pilot.PilotId));
        Assert.Equal(
            new object?[] { timers.ActivationUntil, timers.FatigueUntil, timers.UpdatedAt },
            BlueMarshal.Decode(BlueMarshal.Encode(read.Reply))
        );
        Assert.Equal(
            10_062.5,
            fixture.Live.Economy.Inspect(world => world.Set<Eden.Data.World.Fittings.ShipEnergy>().Find(fixture.FreighterId)!.Charge),
            6
        );
        freighter.Context.Flight.Snapshot();
        Assert.Contains(freighter.Context.Flight.Updates, entry => entry.Command is DestinyCommand.SetState);
        Assert.Contains(
            freighter.Context.Flight.Updates,
            entry => entry.Command is DestinyCommand.SpecialEffect { Graphic: "effects.JumpDriveIn" }
        );

        Assert.IsType<PilotCallResult.Success>(freighter.Context.Flight.Execute(new FlightCommand.Dock(JanStation, fixture.FreighterId)));
        var unloaded = fixture.Inventory.Move(
            freighter.Pilot.PilotId,
            JanStation,
            new([fixture.CargoId], fixture.FreighterId, (int)InventoryFlag.Hangar, null)
        );
        Assert.Contains(
            unloaded,
            change => change.Item.TypeId == Cargo && change.Item.LocationId == JanStation && change.Item.Quantity == 14
        );
        long freighterPilot = freighter.Pilot.PilotId;
        freighter.Dispose();
        emitter.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(timers, fixture.Travel.Timers(freighterPilot));
        Assert.Equal(JanStation, fixture.Db.Store.FindPilot(1000001)!.StationId);
        Assert.Contains(fixture.Inventory.List(freighterPilot, JanStation), item => item.TypeId == Cargo && item.Quantity == 14);
        Assert.Equal(beacon.Beacon.Id, fixture.Travel.Field(beacon.PilotId)?.Beacon.Id);
    }

    [Fact]
    public async Task ExpiredFieldDuringDepartureLeavesFuelAndTimersAtOrigin()
    {
        using Fixture fixture = new();
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var beacon = fixture.Light(emitter);
        fixture.Clock.Seconds += 597;
        Assert.IsType<PilotCallResult.Failure>(
            freighter.Call("beyonce", "CmdJumpToFleetModuleBeacon", beacon.PilotId, beacon.Beacon.Id + 1, Jan)
        );
        Assert.IsType<PilotCallResult.Success>(fixture.Jump(freighter, beacon));
        await freighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Null(fixture.Travel.Timers(freighter.Pilot.PilotId));
        Assert.Equal(Jita, fixture.Db.Store.FindPilot(1000001)!.SystemId);
        Assert.Equal(60_000, Quantity(fixture, freighter, Nitrogen));
        Assert.False(freighter.Context.Flight.Translocating);
        Assert.Empty(fixture.Travel.BeaconCounts());
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "CustomNotify"));
    }

    [Theory]
    [InlineData(28846, 17889, 38_879)]
    [InlineData(28848, 17887, 44_568)]
    [InlineData(28850, 16274, 41_724)]
    public void OtherJumpFreightersUseTheirOwnIsotopesAndHullRate(int hull, int fuel, int paid)
    {
        using Fixture fixture = new(hull, fuel);
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        var jump = fixture.Travel.Jump(freighter.Pilot.PilotId, freighter.Pilot.ShipId, new(field.PilotId, field.Beacon.Id, Jan));
        Assert.Equal(Jan, jump.Pilot.SystemId);
        Assert.Equal(60_000 - paid, Quantity(fixture, freighter, fuel));
    }

    [Theory]
    [InlineData(648, 720)]
    [InlineData(12729, 720)]
    [InlineData(12731, 720)]
    [InlineData(89648, 360)]
    public void EligibleHaulersAndConsortiumVentureCanLightOnePaidField(int hull, int ozone)
    {
        using Fixture fixture = new(emitterHull: hull);
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        Assert.Equal(1000 - ozone, Quantity(fixture, emitter, LiquidOzone));
        Assert.Throws<InvalidDataException>(() => fixture.Travel.Light(emitter.Pilot.PilotId, emitter.Pilot.ShipId, fixture.Module, 0));
        Assert.Equal(1000 - ozone, Quantity(fixture, emitter, LiquidOzone));
        Assert.Equal(field, Assert.Single(fixture.Travel.FleetFields(freighter.Pilot.PilotId)));
    }

    [Fact]
    public void HighSecurityAndExcessSpeedRejectWithoutOzoneDebit()
    {
        using Fixture fixture = new();
        var pilot = fixture.Db.Store.FindPilot(1000002)!;
        Assert.Throws<InvalidDataException>(() => fixture.Travel.Light(pilot.PilotId, pilot.ShipId, fixture.Module, 500));
        fixture.Db.Store.SetPosition(pilot.ShipId, new(10_000_000, 0, 0), Jita);
        Assert.Throws<InvalidDataException>(() => fixture.Travel.Light(pilot.PilotId, pilot.ShipId, fixture.Module, 0));
        Assert.Null(fixture.Travel.Field(pilot.PilotId));
        Assert.Equal(1000, fixture.Inventory.List(pilot.PilotId, pilot.ShipId).Single(item => item.TypeId == LiquidOzone).Quantity);
    }

    private static int Quantity(Fixture fixture, Client client, int type) =>
        fixture.Inventory.List(client.Pilot.PilotId, client.Pilot.ShipId).Where(item => item.TypeId == type).Sum(item => item.Quantity);

    private static bool ContainsText(object? packet, string text) =>
        System.Text.Encoding.Latin1.GetString(BlueMarshal.Encode(packet)).Contains(text, StringComparison.Ordinal);

    private sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal LiveWorld Live { get; private set; }
        internal IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        internal IPilotCapitalTravel Travel => Db.Services.GetRequiredService<IPilotCapitalTravel>();
        internal ICooperativeFleets Fleets => Db.Services.GetRequiredService<ICooperativeFleets>();
        internal long FreighterId { get; }
        internal long Module { get; }
        internal long CargoId { get; }
        private bool _joined;

        internal Fixture(int hull = Rhea, int isotope = Nitrogen, int emitterHull = (int)StarterShipType.Venture)
        {
            Db = new(clock: Clock);
            var database = new WorldDatabase(Db.Path);
            PassiveCatalogFixture.LoadCapitalTravel(database.CatalogPath);
            using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO NpcCorporation VALUES (1000015,'Caldari Steel')");
                catalog.Database.ExecuteSql(
                    $"INSERT INTO Station VALUES ({JanStation},{Jan},1531,1000015,39,40088235,{s_station.X},{s_station.Y},{s_station.Z})"
                );
            }
            Live = new(database, Clock, _ => { });
            var first = Db.Store.FindPilot(1000001)!;
            var second = Db.Store.FindPilot(1000002)!;
            var assembly = Db.Services.GetRequiredService<IPilotFleet>();
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            Train(first.PilotId, [.. HullRequirements(hull), (3456, 5)]);
            Train(second.PilotId, [.. HullRequirements(emitterHull), (CapitalTravelRules.CynosuralFieldTheory, 1)]);
            FreighterId = Board(first, hull);
            long venture = Board(second, emitterHull);
            Module = Grant(second.PilotId, second.StationId, InventoryFlag.Hangar, CapitalTravelRules.IndustrialGenerator, 1);
            fitting.Fit(second.PilotId, [Module], second.StationId, (int)InventoryFlag.AutoFit);
            fitting.SetOnline(second.PilotId, venture, Module, true);
            Grant(second.PilotId, venture, InventoryFlag.Cargo, LiquidOzone, 1000);
            Grant(first.PilotId, FreighterId, InventoryFlag.FuelBay, isotope, 14_000);
            Grant(first.PilotId, FreighterId, InventoryFlag.FuelBay, isotope, 46_000);
            CargoId = Grant(first.PilotId, FreighterId, InventoryFlag.Cargo, Cargo, 14);
            Db.Store.SetPosition(FreighterId, new(10_000_000, 0, 0), Jita);
            Db.Store.SetPosition(venture, s_station + new SpacePosition(-40_213, 0, 0), Jan);

            IEnumerable<(int Skill, int Level)> HullRequirements(int type)
            {
                var attributes = fitting.Attributes(type);
                return InventoryDefinitions
                    .RequiredSkills.Select(requirement =>
                        (
                            Skill: (int)attributes.GetValueOrDefault(requirement.Skill),
                            Level: (int)attributes.GetValueOrDefault(requirement.Level)
                        )
                    )
                    .Where(requirement => requirement.Skill > 0);
            }

            long Board(DockedPilot pilot, int type)
            {
                long packaged = Grant(pilot.PilotId, pilot.StationId, InventoryFlag.Hangar, type, 1);
                long ship = Assert.Single(assembly.Assemble(pilot.PilotId, [packaged], null).ShipIds);
                return assembly.Board(pilot.PilotId, ship, pilot.ShipId).Pilot.ShipId;
            }
        }

        internal Client Select(long account) => new(Db, Clock, account);

        internal CynosuralField Light(Client emitter)
        {
            if (!_joined)
            {
                long other = Db.Store.FindPilot(1000001)!.PilotId;
                long fleet = Fleets.Create(emitter.Pilot.PilotId).Id;
                Fleets.Invite(emitter.Pilot.PilotId, fleet, other, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
                Fleets.Accept(other, fleet);
                _joined = true;
            }
            var response = emitter.Call("dogmaIM", "Activate", Module, "cynosuralGeneration", null, 0);
            Assert.True(response is PilotCallResult.Success, response.ToString());
            return Assert.IsType<CynosuralField>(Travel.Field(emitter.Pilot.PilotId));
        }

        internal PilotCallResult Jump(Client client, CynosuralField field) =>
            client.Call("beyonce", "CmdJumpToFleetModuleBeacon", field.PilotId, field.Beacon.Id, field.SystemId);

        internal void Reopen()
        {
            Live.Dispose();
            Live = new(new(Db.Path), Clock, _ => { });
        }

        private void Train(long pilot, params (int Skill, int Level)[] skills)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath);
            using var action = Live.Economy.Begin(new(pilots: [pilot]));
            foreach (var (skill, level) in skills)
            {
                var saved =
                    action.Set<PilotSkill>().Find(pilot, skill) ?? action.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
                saved.Points = TrainedSkill.PointsForLevel(catalog.FittingAttributes(skill)[DogmaAttributeId.SkillTimeConstant], level);
            }
            action.Commit();
        }

        private long Grant(long pilot, long location, InventoryFlag flag, int type, int quantity)
        {
            using var action = Live.Economy.Begin(new(pilots: [pilot]));
            long id = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = pilot,
                    LocationId = location,
                    FlagId = (int)flag,
                    TypeId = type,
                    Quantity = quantity,
                }
            );
            action.Commit();
            return id;
        }

        public void Dispose()
        {
            Live.Dispose();
            Db.Dispose();
        }
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotSessionTick _tick;
        private readonly PilotSessionOutput _output;
        private readonly long _account;
        private bool _disposed;
        internal PilotCallContext Context { get; }
        internal DockedPilot Pilot => Context.Pilot;
        internal List<object?> Packets { get; } = [];

        internal Client(TestWorld db, TestClock clock, long account)
        {
            _account = account;
            _scope = db.Services.CreateScope();
            var services = _scope.ServiceProvider;
            var pilot = db.Store.FindPilot(account)!;
            Context = new(
                account,
                pilot.Account,
                [pilot],
                services.GetRequiredService<FlightSessionFactory>().Create,
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
            var updates = services.GetRequiredService<PilotUpdateCoordinator>();
            updates.Enter(Context);
            Context.Flight.Snapshot();
            Context.Flight.Updates.Clear();
            _tick = new(clock, updates, services.GetRequiredService<SkillHandler>(), services.GetRequiredService<IAsteroidField>());
            _output = new(
                Context,
                account,
                42,
                packet =>
                {
                    Packets.Add(packet);
                    return Task.CompletedTask;
                },
                _ => { }
            );
        }

        internal PilotCallResult Call(string service, string method, params object?[] args) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        internal Task TickAsync() => _tick.AdvanceAsync(Context, _output, _ => { }, _account, 42);

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Context.Dispose();
                _scope.Dispose();
            }
        }
    }
}
