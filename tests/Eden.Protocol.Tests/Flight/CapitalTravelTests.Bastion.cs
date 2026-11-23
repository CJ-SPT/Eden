using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task MarauderBastionRunsWithoutFuelAndRestoresItsDeadlineAfterCheckpoint()
    {
        const int Vargur = 28665;
        const int RepeatingCannon = 496;
        const int LargeProjectileTurret = 3308;
        const string BastionEffect = "moduleBonusBastionModule";
        const string AfterburnerEffect = "moduleBonusAfterburner";
        using Fixture fixture = new(Vargur, driveFuel: 0, extendCatalog: LoadCatalog);
        long bastion = fixture.FitCapitalModule(CapitalModeRules.BastionOne);
        long afterburner = fixture.FitCapitalModule((int)PropulsionModuleType.OneMnAfterburnerI);
        long turret = fixture.FitCapitalModule(RepeatingCannon);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        long pilotId = pilot.PilotId;
        long shipId = pilot.ShipId;
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var crimewatch = fixture.Db.Services.GetRequiredService<IPilotCrimewatch>();
        var weapon = Assert.Single(fixture.Inventory.List(pilotId, shipId), item => item.Id == turret);
        var originalShip = fitting.ShipAttributes(pilotId);
        double originalDuration = TurretDuration();
        double originalSpeed = originalShip[DogmaAttributeId.MaxVelocity];
        double originalShieldEm = originalShip[DogmaAttributeId.ShieldEmResonance];
        var cargo = fixture.Inventory.List(pilotId, shipId).Where(item => item.FlagId == (int)InventoryFlag.Cargo).ToArray();
        Assert.Equal(fixture.CargoId, Assert.Single(cargo).Id);
        Assert.DoesNotContain(fixture.Inventory.List(pilotId, shipId), item => item.TypeId is Strontium or LiquidOzone);

        using Client marauder = fixture.Select(1000001);
        Assert.Same(PilotCallResult.None, marauder.Call("dogmaIM", "Activate", afterburner, AfterburnerEffect, null, 100L));
        Assert.True(marauder.Pilot.Flight.MaxSpeed > originalSpeed);
        Assert.IsType<PilotCallResult.Success>(marauder.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        fixture.Clock.Seconds += 2;
        await marauder.TickAsync();
        Assert.True(marauder.Context.Flight.Velocity.Length > 0);
        var activated = marauder.Call("dogmaIM", "Activate", bastion, BastionEffect, null, 0L);
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        var cycle = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(shipId));
        Assert.Equal(bastion, cycle.ModuleId);
        Assert.Equal(6658, (int)cycle.Effect);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, cycle.EndsAt - cycle.StartedAt);
        Assert.Equal(cycle.EndsAt + TimeSpan.FromSeconds(60).Ticks, crimewatch.Read(pilotId, pilot.SystemId).Timers.Weapons.ExpiresAt);
        Assert.Equal(0, marauder.Pilot.Flight.MaxSpeed);
        Assert.Equal(SpacePosition.Zero, marauder.Context.Flight.Velocity);
        Assert.Equal(originalDuration * .5, TurretDuration(), 6);
        Assert.Equal(originalShieldEm * .7, fitting.ShipAttributes(pilotId)[DogmaAttributeId.ShieldEmResonance], 6);
        Assert.Equal(cargo, fixture.Inventory.List(pilotId, shipId).Where(item => item.FlagId == (int)InventoryFlag.Cargo));
        Assert.Contains(marauder.Context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.ModuleId == afterburner && !e.Active);
        var start = Assert.Single(
            marauder.Context.NotificationsBeforeFlight.OfType<ShipEffectChanged>(),
            e => e.Effect.ModuleId == bastion
        );
        Assert.True(start.Active);
        Assert.Equal(60_000, start.Effect.DurationMilliseconds);
        Assert.IsType<PilotCallResult.Failure>(marauder.Context.Flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        Assert.IsType<PilotCallResult.Failure>(marauder.Call("dogmaIM", "Activate", afterburner, AfterburnerEffect, null, 0L));
        Assert.Same(PilotCallResult.None, marauder.Call("dogmaIM", "Deactivate", bastion, BastionEffect));
        Assert.Equal(cycle, fixture.Travel.Mode(shipId));
        await marauder.TickAsync();
        Assert.Contains(marauder.Packets, packet => ContainsText(packet, "effects.SiegeMode"));

        marauder.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        crimewatch = fixture.Db.Services.GetRequiredService<IPilotCrimewatch>();
        Assert.Equal(cycle, fixture.Travel.Mode(shipId));
        Assert.Equal(cycle.EndsAt + TimeSpan.FromSeconds(60).Ticks, crimewatch.Read(pilotId, pilot.SystemId).Timers.Weapons.ExpiresAt);
        using Client returned = fixture.Select(1000001);
        Assert.Equal(0, returned.Pilot.Flight.MaxSpeed);
        AdvanceTo(cycle.EndsAt - 1);
        Assert.Equal(cycle, fixture.Travel.Mode(shipId));
        AdvanceTo(cycle.EndsAt);
        await returned.TickAsync();
        Assert.Null(fixture.Travel.Mode(shipId));
        Assert.Equal(originalSpeed, returned.Pilot.Flight.MaxSpeed, 4);
        Assert.Equal(originalDuration, TurretDuration(), 6);
        Assert.Equal(originalShieldEm, fitting.ShipAttributes(pilotId)[DogmaAttributeId.ShieldEmResonance], 6);
        Assert.Throws<InvalidDataException>(() => crimewatch.CheckTravel(pilotId, CrimewatchTravel.Dock));

        var repeating = ActivateWithRepeat();
        AdvanceTo(repeating.EndsAt - 1);
        await returned.TickAsync();
        Assert.Equal(repeating, fixture.Travel.Mode(shipId));
        AdvanceTo(repeating.EndsAt);
        await returned.TickAsync();
        var repeated = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(shipId));
        Assert.Equal(repeating.EndsAt, repeated.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, repeated.EndsAt - repeated.StartedAt);
        Assert.Equal(repeated.EndsAt + TimeSpan.FromSeconds(60).Ticks, crimewatch.Read(pilotId, pilot.SystemId).Timers.Weapons.ExpiresAt);
        AdvanceTo(repeated.EndsAt);
        await returned.TickAsync();
        Assert.Null(fixture.Travel.Mode(shipId));

        var stopping = ActivateWithRepeat();
        Assert.Same(PilotCallResult.None, returned.Call("dogmaIM", "Deactivate", bastion, BastionEffect));
        Assert.Equal(stopping, fixture.Travel.Mode(shipId));
        AdvanceTo(stopping.EndsAt);
        await returned.TickAsync();
        Assert.Null(fixture.Travel.Mode(shipId));

        double TurretDuration() => fitting.ModuleAttributes(pilotId, weapon)[DogmaAttributeId.WeaponCycleTime];

        CapitalModeCycle ActivateWithRepeat()
        {
            var result = returned.Call("dogmaIM", "Activate", bastion, BastionEffect, null, 1L);
            Assert.True(result is PilotCallResult.Success, result.ToString());
            return Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(shipId));
        }

        void AdvanceTo(long time) => fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(time - fixture.Clock.Now));

        static void LoadCatalog(string path)
        {
            PassiveCatalogFixture.LoadTechTwoHulls(path);
            PassiveCatalogFixture.LoadActiveTiers(path, new HashSet<int> { (int)PropulsionModuleType.OneMnAfterburnerI });
            PassiveCatalogFixture.LoadProjectileTiers(path, new HashSet<int> { RepeatingCannon, LargeProjectileTurret });
        }
    }
}
