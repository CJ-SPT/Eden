using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Fighters;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StandupFighterTests
{
    [Fact]
    public async Task AstrahusLaunchesStandupSquadronAndRecallsItAfterGunnerReleasesControl()
    {
        using Fixture fixture = new(liveWorld: true);
        PassiveCatalogFixture.LoadCapitalTravel(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var structure = fixture.Online();
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        var pilot = docking.Dock(fixture.PilotId, fixture.ShipId, structure.Id);
        var world = LiveWorld.Find(fixture.Db.Path)!;
        const int tube = FighterDeckRules.FirstTube;
        using (var action = world.Economy.Begin(new(pilots: [pilot.PilotId], ships: [structure.Id])))
        {
            action.Add(
                new OwnedItem
                {
                    Id = action.NextItemId(),
                    OwnerId = fixture.CorporationId,
                    LocationId = structure.Id,
                    FlagId = tube,
                    TypeId = 47035,
                    Quantity = 9,
                }
            );
            action.Commit();
        }
        using var scope = fixture.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 52, SignedSettings.Empty);
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var deck = scope.ServiceProvider.GetRequiredService<IPilotFighterDeck>();
        var recovery = scope.ServiceProvider.GetRequiredService<IStandupFighterFlight>();
        var drones = scope.ServiceProvider.GetRequiredService<IPilotDrones>();
        Assert.False(
            Assert.IsType<bool>(
                Assert
                    .IsType<PilotCallResult.Success>(Call("fighterMgr", "LaunchFightersFromTubes", [new object?[] { (long)tube }]))
                    .Reply.ToBlue()
            )
        );
        Assert.Same(PilotCallResult.None, Call("structureControl", "TakeControl", [structure.Id]));
        Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "LaunchFightersFromTubes", [new object?[] { (long)tube }]));
        var squadron = Assert.Single(deck.Read(pilot.PilotId, structure.Id).Squadrons);
        Assert.Empty(deck.Read(pilot.PilotId, structure.Id).Tubes);
        Assert.Equal(9, squadron.Count);
        var launched = Contact();
        Assert.Equal(structure.Id, launched.ShipId);
        Assert.Equal(pilot.PilotId, launched.PilotId);
        Assert.Contains(
            context.Flight.Updates,
            update =>
                update.Command is DestinyCommand.AddBalls add
                && add.Addition.Slims.OfType<DroneSlimItem>().Any(slim => slim.Drone.Id == squadron.SquadronId)
        );
        Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "RecallFightersToTubes", [new object?[] { squadron.SquadronId }]));
        Assert.Equal(DroneActivity.ReturningToBay, Contact().Activity);
        Tick();
        Assert.Equal(squadron.SquadronId, Assert.Single(deck.Read(pilot.PilotId, structure.Id).Tubes).SquadronId);
        Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "LaunchFightersFromTubes", [new object?[] { (long)tube }]));
        using (var action = world.Economy.Begin(new(systems: [pilot.SystemId])))
        {
            action.Set<WorldDrone>().Find(squadron.SquadronId)!.X += 5_000;
            action.Commit();
        }
        double distantX = Contact().Position.X;
        Assert.Same(PilotCallResult.None, Call("structureControl", "ReleaseControl", []));
        Assert.Null(context.ControlledStructureId);
        Assert.Equal(DroneActivity.ReturningToBay, Contact().Activity);
        Tick();
        Assert.True(Contact().Position.X < distantX);
        for (int step = 0; step < 60 && deck.Read(pilot.PilotId, structure.Id).Squadrons.Count > 0; step++)
        {
            Tick();
        }
        var landed = Assert.Single(deck.Read(pilot.PilotId, structure.Id).Tubes);
        Assert.Equal(squadron.SquadronId, landed.SquadronId);
        Assert.Equal(9, landed.Count);
        Assert.Empty(drones.List(pilot.SystemId, fixture.Clock.Now));
        await world.FlushAsync();
        using WorldDbContext saved = new(fixture.Db.Path);
        var item = saved.Set<OwnedItem>().Find(landed.SquadronId)!;
        Assert.Equal(fixture.CorporationId, item.OwnerId);
        Assert.Equal(structure.Id, item.LocationId);
        Assert.Equal(tube, item.FlagId);

        PilotCallResult Call(string service, string method, object?[] args) => router.Call(context, service, method, args, _ => { });
        DroneContact Contact() => Assert.Single(drones.List(pilot.SystemId, fixture.Clock.Now), row => row.Id == squadron.SquadronId);
        void Tick()
        {
            fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            recovery.AdvanceReturns(fixture.Clock.Now);
        }
    }

    [Fact]
    public async Task AstrahusMovesStandupStockAndLoadsNativeTubesWithClassLimits()
    {
        using Fixture fixture = new(liveWorld: true);
        PassiveCatalogFixture.LoadCapitalTravel(new WorldDatabase(fixture.Db.Path).CatalogPath);
        var structure = fixture.Online();
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        var pilot = docking.Dock(fixture.PilotId, fixture.ShipId, structure.Id);
        var world = LiveWorld.Find(fixture.Db.Path)!;
        long templar = Stock(47035, 12);
        long siren = Stock(47037, 1);
        long extra = Stock(47037, 1);
        long heavy = Stock(47117, 1);
        long ordinary = Stock(23055, 1);
        using var scope = fixture.Db.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 51, SignedSettings.Empty);
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var reference = Assert.IsType<BlueObjectReference>(
            Assert.IsType<PilotCallResult.Success>(Call("invbroker", "GetInventoryFromId", [structure.Id])).Reply.ToBlue()
        );
        string inventory = context.ResolveReference(reference.Id)!;
        Assert.IsType<PilotCallResult.Failure>(Move(ordinary, InventoryFlag.FighterBay));
        Assert.Same(PilotCallResult.None, Move(templar, InventoryFlag.FighterBay));
        Assert.Same(PilotCallResult.None, Move(siren, InventoryFlag.FighterBay));
        Assert.Same(PilotCallResult.None, Move(extra, InventoryFlag.FighterBay));
        Assert.Same(PilotCallResult.None, Move(heavy, InventoryFlag.FighterBay));
        int tube = FighterDeckRules.FirstTube;
        Assert.False(Loaded(templar, tube));
        Assert.Same(PilotCallResult.None, Call("structureControl", "TakeControl", [structure.Id]));
        Assert.Equal(
            200_000d,
            fixture.Db.Services.GetRequiredService<IStandupFighterInventory>().Bay(pilot.PilotId, structure.Id).Capacity
        );
        Assert.True(Loaded(templar, tube));
        Assert.True(Loaded(siren, tube + 1));
        Assert.False(Loaded(extra, tube + 2));
        Assert.False(Loaded(heavy, tube + 2));
        Assert.False(Loaded(templar, tube + 3));
        var state = Assert
            .IsType<FighterDeckReply>(Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "GetFightersForShip", [])).Reply)
            .State;
        Assert.Equal(9, state.Tubes[0].Count);
        Assert.Equal(47035, state.Tubes[0].TypeId);
        Assert.Equal(47037, state.Tubes[1].TypeId);
        Assert.Equal(3, Assert.Single(fixture.Structures.Inventory(pilot.PilotId, structure.Id), row => row.Id == templar).Quantity);
        Assert.True(
            Assert.IsType<bool>(
                Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "UnloadTubeToFighterBay", [(long)tube])).Reply.ToBlue()
            )
        );
        Assert.Same(PilotCallResult.None, Move(state.Tubes[0].SquadronId, InventoryFlag.Hangar, 2));
        await world.FlushAsync();
        using WorldDbContext saved = new(fixture.Db.Path);
        var supply = saved.Set<OwnedItem>().Find(state.Tubes[0].SquadronId)!;
        Assert.Equal(7, supply.Quantity);
        Assert.Equal(fixture.CorporationId, supply.OwnerId);
        Assert.Equal((int)InventoryFlag.FighterBay, supply.FlagId);

        long Stock(int type, int count)
        {
            using var action = world.Economy.Begin(new(pilots: [pilot.PilotId]));
            long id = action.NextItemId();
            action.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = pilot.PilotId,
                    LocationId = structure.Id,
                    FlagId = (int)InventoryFlag.Hangar,
                    TypeId = type,
                    Quantity = count,
                }
            );
            action.Commit();
            return id;
        }
        PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.Call(context, service, method, args, _ => { }, keywords);
        PilotCallResult Move(long item, InventoryFlag flag, int? quantity = null) =>
            Call(inventory, "Add", [item, structure.Id], new() { ["flag"] = (long)flag, ["qty"] = (long?)quantity });
        bool Loaded(long item, int flag) =>
            Assert.IsType<bool>(
                Assert.IsType<PilotCallResult.Success>(Call("fighterMgr", "LoadFightersToTube", [item, (long)flag])).Reply.ToBlue()
            );
    }
}
