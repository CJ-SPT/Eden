using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
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
}
