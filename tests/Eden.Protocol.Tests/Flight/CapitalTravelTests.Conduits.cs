using Eden.Gameplay.Docking;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(Sin, Rapier, true, true)]
    [InlineData(Sin, Rapier, false, false)]
    [InlineData(Archon, 32880, true, true)]
    [InlineData(Archon, 648, true, false)]
    [InlineData(28352, 32880, true, true)]
    public async Task ConduitJumpPaysTheHostAndMovesOnlyEligibleConsentingFleetmates(
        int anchorHull,
        int passengerHull,
        bool consent,
        bool expectedPassenger
    )
    {
        bool covert = anchorHull == Sin;
        int isotope = covert || anchorHull == 28352 ? 17887 : Helium;
        bool standardEmitter = passengerHull == Rapier;
        using Fixture fixture = new(
            anchorHull,
            isotope,
            standardEmitter ? Rapier : (int)StarterShipType.Venture,
            standardEmitter ? CapitalTravelRules.StandardGenerator : CapitalTravelRules.IndustrialGenerator,
            structures: true,
            fittedPortal: covert
        );
        if (anchorHull == 28352)
        {
            fixture.FitCapitalModule(CapitalTravelRules.IndustrialPortalGenerator);
        }
        var (beacon, _) = fixture.PrepareNavigation();
        fixture.AdmitBridgePilot(NavigationCorporation);
        var first = fixture.Db.Store.FindPilot(1000001)!;
        var second = fixture.Db.Store.FindPilot(1000002)!;
        long passenger = second.ShipId;
        if (passengerHull == 648)
        {
            var docking = fixture.Db.Services.GetRequiredService<Eden.Gameplay.Docking.IStationDocking>();
            fixture.Db.Store.SetPosition(second.ShipId, s_station, Jan);
            passenger = fixture.AssembleAndBoard(docking.Dock(second.PilotId, second.ShipId, JanStation), passengerHull);
        }
        var point = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(first.ShipId, point, Jan);
        fixture.Db.Store.SetPosition(passenger, point + new SpacePosition(7000, 0, 0), Jan);
        using Client anchor = fixture.Select(1000001);
        using Client escort = fixture.Select(1000002);
        fixture.Join(escort);
        var fleet = fixture.Db.Services.GetRequiredService<ICooperativeFleets>();
        long fleetId = fleet.Identity(second.PilotId)!.FleetId;
        fleet.SetAcceptsConduitJumps(second.PilotId, fleetId, consent);
        int before = Quantity(fixture, anchor, isotope);
        var jump = anchor.Call("structureCynoBeaconMgr", "CmdGroupJumpToStructureBeacon", beacon.Id, NullSystem);
        Assert.True(jump is PilotCallResult.Success, jump.ToString());
        await anchor.TickAsync();
        fixture.Clock.Seconds += 3;
        await anchor.TickAsync();
        await escort.TickAsync();
        Assert.Equal(NullSystem, anchor.Pilot.SystemId);
        // Pinned Jan-to-NullSystem distance is 5.628560677 LY.
        int expectedFuel = anchorHull switch
        {
            Sin => 5628,
            Archon => 16885,
            _ => 28142,
        };
        Assert.Equal(before - expectedFuel, Quantity(fixture, anchor, isotope));
        Assert.Equal(expectedPassenger ? NullSystem : Jan, escort.Pilot.SystemId);
        Assert.Equal(expectedPassenger, fixture.Travel.Timers(second.PilotId) is not null);
        if (expectedPassenger)
        {
            Assert.True(
                anchor.Pilot.Position!.DistanceTo(escort.Pilot.Position!) > anchor.Pilot.Flight.ShipRadius + escort.Pilot.Flight.ShipRadius
            );
            Assert.Contains(
                escort.Packets.OfType<BlueInstance>(),
                packet => packet.Name.EndsWith(".SessionChangeNotification", StringComparison.Ordinal)
            );
        }
        anchor.Dispose();
        escort.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(expectedPassenger ? NullSystem : Jan, fixture.Db.Store.FindPilot(1000002)!.SystemId);
    }
}
