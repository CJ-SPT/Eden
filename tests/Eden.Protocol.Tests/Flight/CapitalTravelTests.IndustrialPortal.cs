using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public void JumpPortalPublishesAndRejectsRepeatActivationWithAnImplicitOriginSystem()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen, structures: true);
        var (beacon, _) = fixture.PrepareNavigation();
        fixture.AdmitBridgePilot(NavigationCorporation);
        long module = fixture.FitCapitalModule(CapitalTravelRules.IndustrialPortalGenerator);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(source.ShipId, s_station, Jan);
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        docking.Dock(source.PilotId, source.ShipId, JanStation);
        fixture.Db.Store.SetPosition(source.ShipId, s_station + new SpacePosition(20_000, 0, 0));
        Assert.Null(fixture.Live.Economy.Inspect(world => world.Positions.Find(source.ShipId)!.SystemId));
        using Client host = fixture.Select(1000001);
        using Client passenger = fixture.Select(1000002);
        fixture.Join(passenger);

        Assert.IsType<PilotCallResult.Success>(Open());
        var portal = Assert.IsType<JumpPortal>(fixture.Travel.Portal(source.PilotId));
        Assert.Equal(module, portal.ModuleId);
        Assert.Equal(Jan, portal.Origin);
        Assert.Equal(portal, Assert.Single(fixture.Travel.FleetPortals(passenger.Pilot.PilotId)));
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        double capacitor = resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor;
        Assert.IsType<PilotCallResult.Failure>(Open());
        Assert.Equal(portal, fixture.Travel.Portal(source.PilotId));
        Assert.Equal(capacitor, resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor);

        PilotCallResult Open() => host.Call("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon", beacon.Id, NullSystem);
    }

    [Theory]
    [InlineData(32880, true)] // Venture.
    [InlineData(648, false)] // Badger.
    public async Task RorqualIndustrialBridgeChargesOnlyForEligiblePassengers(int passengerType, bool allowed)
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen, structures: true);
        var (beacon, _) = fixture.PrepareNavigation();
        fixture.AdmitBridgePilot(NavigationCorporation);
        long module = fixture.FitCapitalModule(CapitalTravelRules.IndustrialPortalGenerator);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        long targetShip = target.ShipId;
        if (!allowed)
        {
            var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
            fixture.Db.Store.SetPosition(target.ShipId, s_station, Jan);
            targetShip = fixture.AssembleAndBoard(docking.Dock(target.PilotId, target.ShipId, JanStation), passengerType);
        }
        var origin = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(source.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(targetShip, origin + new SpacePosition(2200, 0, 0), Jan);
        using Client host = fixture.Select(1000001);
        using Client passenger = fixture.Select(1000002);
        fixture.Join(passenger);
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        double charge = resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor;
        int fuel = Quantity(fixture, host, Oxygen);
        var opened = host.Call("structureCynoBeaconMgr", "CmdBridgeToStructureBeacon", beacon.Id, NullSystem);
        Assert.True(opened is PilotCallResult.Success, opened.ToString());
        var portal = Assert.IsType<JumpPortal>(fixture.Travel.Portal(source.PilotId));
        Assert.Equal(module, portal.ModuleId);
        Assert.Equal(CapitalTravelRules.IndustrialPortalGenerator, portal.ModuleTypeId);
        Assert.Equal(TimeSpan.FromSeconds(20).Ticks, portal.EndsAt - portal.StartedAt);
        Assert.Equal(100, charge - resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor, precision: 6);
        Assert.Equal(fuel, Quantity(fixture, host, Oxygen));
        await host.TickAsync();
        await passenger.TickAsync();
        Assert.Contains(passenger.Packets, packet => ContainsText(packet, "effects.JumpPortal"));
        var jump = passenger.Call("beyonce", "CmdJumpThroughFleet", source.PilotId, source.ShipId, beacon.Id, NullSystem);
        Assert.True((jump is PilotCallResult.Success) == allowed, jump.ToString());
        await passenger.TickAsync();
        fixture.Clock.Seconds += 3;
        await passenger.TickAsync();
        Assert.Equal(allowed ? NullSystem : Jan, passenger.Pilot.SystemId);
        Assert.Equal(Jan, host.Pilot.SystemId);
        // Jan-to-null distance 5.628560677 LY, Venture mass 1.2M kg and an effective 2000 isotope/LY.
        Assert.Equal(fuel - (allowed ? 14 : 0), Quantity(fixture, host, Oxygen));
        Assert.Equal(allowed, fixture.Travel.Timers(target.PilotId) is not null);
        Assert.Null(fixture.Travel.Timers(source.PilotId));
        fixture.Clock.Seconds += 17;
        await host.TickAsync();
        Assert.Null(fixture.Travel.Portal(source.PilotId));
    }
}
