using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task RorqualCloneVatPaysOzoneAndCompletesItsImmobilizingCycleAfterRestart()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        long vat = fixture.FitCapitalModule(CapitalModeRules.CloneVat);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(pilot.ShipId, new(10_000_000, 0, 0), Jan);
        using Client rorqual = fixture.Select(1000001);
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        double velocity = fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity];
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));
        fixture.PutInCapitalCargo(LiquidOzone, 2);
        var paid = Activate();
        Assert.True(paid is PilotCallResult.Success, paid.ToString());
        var active = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(DogmaEffect.CloneJumpAccepting, active.Effect);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, active.EndsAt - active.StartedAt);
        Assert.Equal(1, Quantity(fixture, rorqual, LiquidOzone));
        Assert.Equal(0, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity]);
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Same(PilotCallResult.None, rorqual.Call("dogmaIM", "Deactivate", vat, "cloneJumpAccepting"));
        var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
        Assert.Throws<InvalidDataException>(() => docking.Dock(pilot.PilotId, pilot.ShipId, JanStation));
        await rorqual.TickAsync();
        Assert.Contains(rorqual.Packets, packet => ContainsText(packet, "OnGodmaShipEffect"));
        rorqual.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(active, fixture.Travel.Mode(pilot.ShipId));
        using Client returned = fixture.Select(1000001);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(active.EndsAt - fixture.Clock.Now));
        await returned.TickAsync();
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(1, Quantity(fixture, returned, LiquidOzone));
        Assert.Equal(velocity, fitting.ShipAttributes(pilot.PilotId)[DogmaAttributeId.MaxVelocity]);

        PilotCallResult Activate() => rorqual.Call("dogmaIM", "Activate", vat, "cloneJumpAccepting", null, 0L);
    }
}
