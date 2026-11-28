using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Fleets;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MicroJumpSettlementHonorsPanicAppliedDuringSpool(bool protectSource)
    {
        const int Bustard = 12731;
        const int MediumMicroJumpDrive = 33915;
        using Fixture fixture = new(protectSource ? Bustard : Archon, Helium, extendCatalog: PassiveCatalogFixture.LoadSpatialModules);
        long module = fixture.FitCapitalModule(protectSource ? MediumMicroJumpDrive : SpatialModuleRules.CapitalField);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        var targetStart = origin + new SpacePosition(0, 9000, 0);
        fixture.Db.Store.SetPosition(source.ShipId, origin, NullSystem);
        fixture.Db.Store.SetPosition(target.ShipId, targetStart, NullSystem);
        var engine = fixture.Db.Services.GetRequiredService<IPilotSpatialModules>();
        var cycle = engine.Activate(
            source.PilotId,
            module,
            protectSource ? DogmaEffect.MicroJumpDrive : DogmaEffect.CapitalMicroJumpField,
            new(1, 0, 0),
            SpacePosition.Zero,
            fixture.Clock.Now
        );
        long protectedShip = protectSource ? source.ShipId : target.ShipId;
        using (var action = fixture.Live.Economy.Begin(new(systems: [NullSystem], ships: [protectedShip])))
        {
            action.Add(
                new WorldCommandBurst
                {
                    Id = action.Next("WorldCommandBurst"),
                    ShipId = protectedShip,
                    SystemId = NullSystem,
                    ExpiresAt = cycle.EndsAt + 1,
                    BoostsJson = JsonSerializer.Serialize(IndustrialProtectionRules.Buffs),
                    IndustrialProtection = true,
                }
            );
            action.Commit();
        }
        var jump = engine.CompleteJump(
            source.PilotId,
            source.ShipId,
            module,
            cycle.StartedAt,
            cycle.EndsAt,
            [new(target.PilotId, target.ShipId, targetStart)]
        );
        if (protectSource)
        {
            Assert.Null(jump);
            Assert.Equal(origin, fixture.Db.Store.FindPilot(1000001)!.Position);
        }
        else
        {
            Assert.Empty(Assert.IsType<MicroJumpResult>(jump).Passengers);
            Assert.Equal(origin + new SpacePosition(250_000, 0, 0), fixture.Db.Store.FindPilot(1000001)!.Position);
        }
        Assert.Equal(targetStart, fixture.Db.Store.FindPilot(1000002)!.Position);
        Assert.Equal(0, engine.Status(target.ShipId).FieldScrambledUntil);
        Assert.Equal(cycle.ReadyAt, fixture.Live.Economy.Inspect(world => world.Set<ModuleCooldown>().Find(module)!.ReadyAt));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SubcapitalMicroJumpFieldMovesEligibleShipsInLowsecAndPersistsPositions(bool capital, bool outside)
    {
        const int Bifrost = 37480;
        const string FieldEffect = "microJumpPortalDrive";
        const int Radius = 6000;
        const double SpoolMilliseconds = 8122.5; // Bifrost with Micro Jump Drive Operation I and Command Destroyers I.
        using Fixture fixture = new(Bifrost, driveFuel: 0, extendCatalog: LoadCatalog);
        long module = fixture.FitCapitalModule(SpatialModuleRules.SubcapitalField);
        var source = fixture.Db.Store.FindPilot(1000001)!;
        var target = fixture.Db.Store.FindPilot(1000002)!;
        if (capital)
        {
            fixture.Db.Store.SetPosition(target.ShipId, s_station, Jan);
            var docking = fixture.Db.Services.GetRequiredService<IStationDocking>();
            fixture.AssembleAndBoard(docking.Dock(target.PilotId, target.ShipId, JanStation), Revelation);
            target = fixture.Db.Store.FindPilot(1000002)!;
        }
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        var catalogAttributes = fitting.Attributes(SpatialModuleRules.SubcapitalField);
        var profile = SpatialModuleRules.Define(module, SpatialModuleRules.SubcapitalField, DogmaEffect.MicroJumpField, catalogAttributes);
        var field = Assert.IsType<MicroJumpField>(profile.Field);
        Assert.Equal(Radius, field.Radius);
        Assert.Equal(24, field.PassengerLimit);
        Assert.Equal(0, field.CapitalLimit);
        Assert.Equal(9_000, profile.DurationMilliseconds);
        Assert.Equal(100_000, SpatialModuleRules.Distance(profile.TypeId, catalogAttributes));

        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        var crimewatch = fixture.Db.Services.GetRequiredService<IPilotCrimewatch>();
        var before = resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now);
        using (Client highsec = fixture.Select(1000001))
        {
            Assert.Equal(Jita, highsec.Pilot.SystemId);
            Assert.IsType<PilotCallResult.Failure>(highsec.Call("dogmaIM", "Activate", module, FieldEffect, null, 0L));
            Assert.Equal(before.Capacitor, resources.Read(source.PilotId, source.ShipId, fixture.Clock.Now).Capacitor, 6);
            Assert.Equal(CrimewatchTimers.Empty, crimewatch.Read(source.PilotId, Jita).Timers);
            Assert.False(highsec.Context.Flight.MicroJumpSpooling);
        }

        var origin = new SpacePosition(1_000_000_000, 0, 0);
        double centerDistance = Radius + source.Flight.ShipRadius + target.Flight.ShipRadius + (outside ? 1 : 0);
        var targetStart = origin + new SpacePosition(0, centerDistance, 0);
        fixture.Db.Store.SetPosition(source.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(target.ShipId, targetStart, Jan);
        using Client generator = fixture.Select(1000001);
        using Client passenger = fixture.Select(1000002);
        Assert.Equal(ShipSafetyLevel.Full, crimewatch.Read(source.PilotId, Jan).Safety);
        var flight = generator.Context.Flight;
        Assert.IsType<PilotCallResult.Success>(flight.Execute(new FlightCommand.Heading(1, 0, 0)));
        Assert.IsType<PilotCallResult.Success>(flight.Execute(new FlightCommand.Stop()));

        long start = fixture.Clock.Now;
        var activation = generator.Call("dogmaIM", "Activate", module, FieldEffect, null, 0L);
        Assert.True(activation is PilotCallResult.Success, activation.ToString());
        Assert.True(flight.MicroJumpSpooling);
        var after = resources.Read(source.PilotId, source.ShipId, start);
        Assert.Equal(85, before.Capacitor - after.Capacitor, 6);
        Assert.Equal(before.Attributes[DogmaAttributeId.SignatureRadius] * 2.5, after.Attributes[DogmaAttributeId.SignatureRadius], 6);
        var effect = Assert.Single(generator.Context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.ModuleId == module);
        Assert.Equal(6208, (int)effect.Effect.Effect);
        Assert.True(effect.Active);
        Assert.Equal(SpoolMilliseconds, Assert.IsType<double>(effect.Effect.DurationMilliseconds), 6);
        var graphic = Assert.Single(flight.Updates.Select(update => update.Command).OfType<DestinyCommand.TimedSpecialEffect>());
        Assert.True(graphic.Offensive && graphic.Start && graphic.Active);
        var timers = CrimewatchTimers.Empty with { Weapons = new(start, start + TimeSpan.FromSeconds(60).Ticks) };
        Assert.Equal(timers, crimewatch.Read(source.PilotId, Jan).Timers);

        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks((long)(SpoolMilliseconds * TimeSpan.TicksPerMillisecond) - 1));
        await generator.TickAsync();
        Assert.True(flight.MicroJumpSpooling);
        Assert.Equal(origin, generator.Pilot.Position);
        Assert.Equal(targetStart, fixture.Db.Store.FindPilot(1000002)!.Position);
        Assert.Contains(generator.Packets, packet => ContainsText(packet, "effects.MicroJumpFieldEngage"));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        await generator.TickAsync();

        var displacement = new SpacePosition(100_000, 0, 0);
        var expectedTarget = capital || outside ? targetStart : targetStart + displacement;
        Assert.Equal(origin + displacement, generator.Pilot.Position);
        Assert.Equal(generator.Pilot.Position, fixture.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(expectedTarget, fixture.Db.Store.FindPilot(1000002)!.Position);
        Assert.False(flight.MicroJumpSpooling);
        await passenger.TickAsync();
        Assert.Equal(expectedTarget, passenger.Pilot.Position);
        if (!capital && !outside)
        {
            Assert.Contains(passenger.Packets, packet => ContainsText(packet, "SetBallPosition"));
            Assert.Contains(passenger.Packets, packet => ContainsText(packet, "effects.MicroJumpDriveJump"));
        }
        var spatial = fixture.Db.Services.GetRequiredService<IPilotSpatialModules>();
        Assert.Equal(0, spatial.Status(source.ShipId).FieldScrambledUntil);
        Assert.Equal(0, spatial.Status(target.ShipId).FieldScrambledUntil);
        Assert.Equal(timers, crimewatch.Read(source.PilotId, Jan).Timers);
        Assert.Equal(CrimewatchTimers.Empty, crimewatch.Read(target.PilotId, Jan).Timers);
        Assert.IsType<PilotCallResult.Failure>(generator.Call("dogmaIM", "Activate", module, FieldEffect, null, 0L));

        generator.Dispose();
        passenger.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(origin + displacement, fixture.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(expectedTarget, fixture.Db.Store.FindPilot(1000002)!.Position);
        Assert.Equal(timers, crimewatch.Read(source.PilotId, Jan).Timers);

        static void LoadCatalog(string path)
        {
            PassiveCatalogFixture.LoadTechTwoHulls(path, Bifrost);
            PassiveCatalogFixture.LoadSpatialModules(path);
        }
    }

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
