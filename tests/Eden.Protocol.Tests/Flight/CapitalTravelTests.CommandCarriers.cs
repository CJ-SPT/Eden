using Eden.Data.World.Drones;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task CommandCarriersAssembleAndSalvationLaunchesBoostedSupportFightersAndUsesAStandardBeacon()
    {
        const int salvation = 92822;
        const int cenobite = 37599;
        using Fixture fixture = new(structures: true);
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        foreach (int hull in new[] { salvation, 92823, 92824, 92825 })
        {
            Assert.True(HullCatalog.UsesCatalogBonuses(catalog, hull));
            Assert.True(HullCatalog.CanAssemble(catalog, catalog.RequireItem(hull), (int)InventoryCategory.Ship));
        }
        var (beacon, _) = fixture.PrepareNavigation();
        fixture.AdmitBridgePilot(NavigationCorporation);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(source.ShipId, s_station, Jan);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        long ship = fixture.AssembleAndBoard(docking.Dock(source.PilotId, source.ShipId, JanStation), salvation);
        fixture.Grant(source.PilotId, ship, InventoryFlag.FuelBay, Helium, 60_000);
        fixture.Db.Store.SetPosition(ship, new(10_000_000, 0, 0), Jan);
        long stack = fixture.PutInFighterBay(cenobite, 3);
        using Client carrier = fixture.Select(1000001);
        int tube = FighterDeckRules.FirstTube;
        var fitting = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        var hullAttributes = fitting.Read(source.PilotId, ship, fixture.Clock.Now).Attributes;
        Assert.Equal(10, hullAttributes[DogmaAttributeId.ConduitPassengerCount]);
        var load = Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LoadFightersToTube", stack, tube));
        Assert.True(Assert.IsType<bool>(load.Reply.ToBlue()));
        Assert.IsType<PilotCallResult.Success>(carrier.Call("fighterMgr", "LaunchFightersFromTubes", new List<object?> { tube }));
        var utility = fixture.Live.Economy.Inspect(world =>
            PilotDroneStore.Contact(world, catalog, world.Set<WorldDrone>().Find(stack)!).Profile.Utility
        );
        Assert.True(utility!.Amount > DroneCatalog.Profile(catalog, cenobite).Utility!.Amount);
        var jumped = carrier.Call("structureCynoBeaconMgr", "CmdJumpToStructureBeacon", beacon.Id, NullSystem);
        Assert.True(jumped is PilotCallResult.Success, jumped.ToString());
        await carrier.TickAsync();
        fixture.Clock.Seconds += 3;
        await carrier.TickAsync();
        Assert.Equal(NullSystem, carrier.Pilot.SystemId);
        Assert.True(Quantity(fixture, carrier, Helium) < 60_000);
    }
}
