using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(Archon, 9000)]
    [InlineData(92822, 8550)] // Salvation with Command Carriers I.
    [InlineData(CapitalModeRules.Sarathiel, 9000)]
    public async Task CapitalMicroJumpUsesPinnedCostsAndRangeIncludingSarathielInSiege(int hull, int spoolMilliseconds)
    {
        using Fixture fixture = new(hull, Helium);
        long drive = fixture.FitCapitalModule(SpatialModuleRules.CapitalDrive);
        bool siege = hull == CapitalModeRules.Sarathiel;
        long siegeModule = siege ? fixture.FitCapitalModule(CapitalModeRules.SiegeOne) : 0;
        if (siege)
        {
            fixture.PutInCapitalCargo(Strontium, 500);
        }

        var ship = fixture.Db.Store.FindPilot(1000001)!;
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(ship.ShipId, origin, NullSystem);
        using Client pilot = fixture.Select(1000001);
        var flight = pilot.Context.Flight;
        if (siege)
        {
            Assert.IsType<PilotCallResult.Success>(pilot.Call("dogmaIM", "Activate", siegeModule, "moduleBonusSiegeModule", null, 0L));
        }
        Assert.IsType<PilotCallResult.Success>(flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        Assert.IsType<PilotCallResult.Success>(flight.Execute(new FlightCommand.Stop()));
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        var before = resources.Read(ship.PilotId, ship.ShipId, fixture.Clock.Now);
        var activation = pilot.Call("dogmaIM", "Activate", drive, "microJumpDrive", null, 0L);
        Assert.True(activation is PilotCallResult.Success, activation.ToString());
        Assert.True(flight.MicroJumpSpooling);
        var after = resources.Read(ship.PilotId, ship.ShipId, fixture.Clock.Now);
        Assert.Equal(19000, before.Capacitor - after.Capacitor, precision: 6);
        Assert.Equal(before.Attributes[DogmaAttributeId.SignatureRadius] * 2.5, after.Attributes[DogmaAttributeId.SignatureRadius], 6);
        fixture.Clock.Milliseconds += spoolMilliseconds - 1;
        await pilot.TickAsync();
        Assert.True(flight.MicroJumpSpooling);
        Assert.Equal(origin, pilot.Pilot.Position);
        fixture.Clock.Milliseconds += 2;
        await pilot.TickAsync();
        Assert.Equal(origin + new SpacePosition(250_000, 0, 0), pilot.Pilot.Position);
        Assert.Equal(pilot.Pilot.Position, fixture.Db.Store.FindPilot(1000001)!.Position);
        Assert.False(flight.MicroJumpSpooling);
        Assert.IsType<PilotCallResult.Failure>(pilot.Call("dogmaIM", "Activate", drive, "microJumpDrive", null, 0L));
        if (siege)
        {
            Assert.True(flight.MobileSiege);
            Assert.IsType<PilotCallResult.Failure>(flight.Execute(new FlightCommand.WarpToPoint(1, new(2_000_000_000, 0, 0), 0)));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task CapitalMicroJumpFieldTranslatesNearbyShipsAndPersistsScramble(bool capital, bool outside)
    {
        using Fixture fixture = new(Archon, Helium);
        long module = fixture.FitCapitalModule(SpatialModuleRules.CapitalField);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        long siege = 0;
        if (capital)
        {
            fixture.Db.Store.SetPosition(target.ShipId, s_station, Jan);
            var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
            fixture.AssembleAndBoard(docking.Dock(target.PilotId, target.ShipId, JanStation), Revelation);
            target = fixture.Db.Store.FindPilot(1000002)!;
            siege = fixture.FitCapitalModule(CapitalModeRules.SiegeOne, 1000002);
            fixture.Grant(target.PilotId, target.ShipId, InventoryFlag.Cargo, Strontium, 500);
        }
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        double available = resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor;
        using (Client highsec = fixture.Select(1000001))
        {
            Assert.Equal(Jita, highsec.Pilot.SystemId);
            Assert.IsType<PilotCallResult.Failure>(highsec.Call("dogmaIM", "Activate", module, "microJumpPortalDriveCapital", null, 0L));
            Assert.Equal(available, resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor, 6);
        }
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        var targetStart = origin + new SpacePosition(0, outside ? 30_000 : 9_000, 0);
        fixture.Db.Store.SetPosition(source.ShipId, origin, NullSystem);
        fixture.Db.Store.SetPosition(target.ShipId, targetStart, NullSystem);
        using Client carrier = fixture.Select(1000001);
        using Client passenger = fixture.Select(1000002);
        if (capital)
        {
            Assert.IsType<PilotCallResult.Success>(passenger.Call("dogmaIM", "Activate", siege, "moduleBonusSiegeModule", null, 0L));
        }
        var activation = carrier.Call("dogmaIM", "Activate", module, "microJumpPortalDriveCapital", null, 0L);
        Assert.True(activation is PilotCallResult.Success, activation.ToString());
        Assert.Equal(28_500, available - resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor, 6);
        fixture.Clock.Milliseconds += 9999;
        await carrier.TickAsync();
        Assert.True(carrier.Context.Flight.MicroJumpSpooling);
        Assert.Contains(carrier.Packets, packet => ContainsText(packet, "effects.MicroJumpFieldEngageCapital"));
        fixture.Clock.Milliseconds++;
        await carrier.TickAsync();
        await passenger.TickAsync();
        var offset = new SpacePosition(250_000, 0, 0);
        long expiry = fixture.Clock.Now + TimeSpan.FromSeconds(5).Ticks;
        Assert.Equal(origin + offset, carrier.Pilot.Position);
        Assert.Equal(outside ? targetStart : targetStart + offset, passenger.Pilot.Position);
        if (capital)
        {
            Assert.NotNull(fixture.Travel.Mode(target.ShipId));
        }
        var navigation = fixture.Db.Services.GetRequiredService<IPilotSpatialModules>();
        Assert.Equal(expiry, navigation.Status(source.ShipId).FieldScrambledUntil);
        Assert.Equal(outside ? 0 : expiry, navigation.Status(target.ShipId).FieldScrambledUntil);
        if (!outside)
        {
            Assert.Contains(passenger.Packets, packet => ContainsText(packet, "SetBallPosition"));
            Assert.IsType<PilotCallResult.Failure>(
                passenger.Context.Flight.Execute(new FlightCommand.WarpToPoint(1, new(2_000_000_000, 0, 0), 0))
            );
        }
        carrier.Dispose();
        passenger.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(outside ? targetStart : targetStart + offset, fixture.Db.Store.FindPilot(1000002)!.Position);
        Assert.Equal(expiry, navigation.Status(source.ShipId).FieldScrambledUntil);
        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        Assert.Contains("micro jump field", Assert.Throws<InvalidDataException>(() => PayForWarp(expiry - 1)).Message);
        Assert.True(PayForWarp(expiry).Cost > 0);

        WarpCapacitorPayment PayForWarp(long at) => warp.Activate(source.PilotId, source.ShipId, 10_000_000, at);
    }
}
