using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Flight;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Data.World.Sovereignty;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;
using Eden.Gameplay.Sovereignty;
using Eden.Gameplay.Structures;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int Jita = 30000142;
    private const int Jan = 30001385;
    private const int NullSystem = 30000251;
    private const int SecondNullSystem = 30000248;
    private const int JanStation = 60001714;
    private const int Rhea = 28844;
    private const int Rapier = 11963;
    private const int Sin = 22430;
    private const int Nitrogen = 17888;
    private const int Oxygen = 17887;
    private const int LiquidOzone = 16273;
    private const int Cargo = 34;
    private static readonly SpacePosition s_station = new(-102770810880, 7849451520, 353786634240);

    [Fact]
    public async Task CovertMobileBeaconSupportsFleetPortalWithHostFuel()
    {
        using Fixture fixture = new(Sin, Oxygen, Rapier, CapitalTravelRules.CovertGenerator, driveFuel: 10_000, fittedPortal: true);
        fixture.CalibrateDrive();
        long mobile = fixture.PrepareCovertMobile();
        using Client host = fixture.Select(1000001);
        long fleet;
        using (Client deployer = fixture.Select(1000002))
        {
            fleet = fixture.Join(deployer);
            Assert.IsType<PilotCallResult.Success>(deployer.Call("ship", "Drop", new List<object?> { mobile }, null, false));
        }
        fixture.MoveSecondToJita();
        using Client passenger = fixture.Select(1000002);
        Assert.Null(fixture.Fleets.Identity(passenger.Pilot.PilotId));
        fixture.Fleets.Invite(
            host.Pilot.PilotId,
            fleet,
            passenger.Pilot.PilotId,
            new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member)
        );
        fixture.Fleets.Accept(passenger.Pilot.PilotId, fleet);
        fixture.Clock.Seconds += 60;
        var field = Assert.Single(fixture.Travel.FleetFields(host.Pilot.PilotId));
        Assert.Equal(CapitalTravelRules.CovertMobileField, field.Beacon.TypeId);
        Assert.False(field.Beacon.IsGlobal);
        Assert.Empty(fixture.Travel.BeaconCounts());
        long deployable = field.DeployableId!.Value;
        var opened = host.Call("beyonce", "CmdBridgeToFleetDeployableBeacon", deployable, Jan, field.Beacon.Id);
        Assert.True(opened is PilotCallResult.Success, opened.ToString());
        var portal = Assert.IsType<JumpPortal>(fixture.Travel.Portal(host.Pilot.PilotId));
        Assert.Equal(20 * TimeSpan.TicksPerSecond, portal.EndsAt - portal.StartedAt);
        Assert.Equal(10_000, Quantity(fixture, host, Oxygen));
        Assert.IsType<PilotCallResult.Failure>(host.Call("dogmaIM", "Deactivate", portal.ModuleId, "jumpPortalGenerationBO"));
        await host.TickAsync();
        await passenger.TickAsync();
        Assert.Contains(passenger.Packets, packet => ContainsText(packet, "OnBridgeModeChange"));
        Assert.Contains(passenger.Packets, packet => ContainsText(packet, "effects.JumpPortalBO"));
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(portal, fixture.Travel.Portal(host.Pilot.PilotId));

        fixture.Clock.Seconds += 18;
        Assert.IsType<PilotCallResult.Success>(Jump());
        await passenger.TickAsync();
        fixture.Clock.Seconds += 3;
        await passenger.TickAsync();
        Assert.Equal(Jita, passenger.Pilot.SystemId);
        Assert.Equal(10_000, Quantity(fixture, host, Oxygen));
        Assert.Null(fixture.Travel.Timers(passenger.Pilot.PilotId));
        Assert.IsType<PilotCallResult.Success>(host.Call("beyonce", "CmdBridgeToFleetDeployableBeacon", deployable, Jan, field.Beacon.Id));
        host.Packets.Clear();

        var jumped = Jump();
        Assert.True(jumped is PilotCallResult.Success, jumped.ToString());
        await passenger.TickAsync();
        Assert.Equal(Jita, passenger.Pilot.SystemId);
        Assert.Equal(10_000, Quantity(fixture, host, Oxygen));
        fixture.Clock.Seconds += 3;
        await passenger.TickAsync();
        await host.TickAsync();
        Assert.Equal(Jan, passenger.Pilot.SystemId);
        Assert.Equal(Jita, host.Pilot.SystemId);
        Assert.Equal(1_857, Quantity(fixture, host, Oxygen));
        Assert.NotNull(fixture.Travel.Timers(passenger.Pilot.PilotId));
        Assert.Null(fixture.Travel.Timers(host.Pilot.PilotId));
        Assert.Contains(host.Packets, packet => ContainsText(packet, "OnItemChange"));

        fixture.Clock.Seconds += 17;
        await passenger.TickAsync();
        Assert.Empty(fixture.Travel.FleetPortals(passenger.Pilot.PilotId));
        Assert.Null(fixture.Travel.Portal(host.Pilot.PilotId));

        PilotCallResult Jump() =>
            passenger.Call("beyonce", "CmdJumpThroughFleet", host.Pilot.PilotId, host.Pilot.ShipId, field.Beacon.Id, Jan);
    }

    [Fact]
    public async Task BlackOpsJumpsToCovertCynoInsideMobileInhibitor()
    {
        using Fixture fixture = new(Sin, Oxygen, Rapier, CapitalTravelRules.CovertGenerator, driveFuel: 10_000);
        fixture.CalibrateDrive();
        var (_, inhibitor) = fixture.PrepareDeployables();
        using Client blackOps = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Success>(emitter.Call("ship", "Drop", new List<object?> { inhibitor }, null, false));
        fixture.Clock.Seconds += 60;
        var field = fixture.Light(emitter);
        Assert.Equal(CapitalTravelRules.CovertField, field.Beacon.TypeId);
        Assert.False(field.Beacon.IsGlobal);
        Assert.Equal(30 * TimeSpan.TicksPerSecond, field.EndsAt - field.StartedAt);
        Assert.Empty(fixture.Travel.BeaconCounts());
        Assert.Equal(field, Assert.Single(fixture.Travel.FleetFields(blackOps.Pilot.PilotId)));
        Assert.IsType<PilotCallResult.Success>(fixture.Jump(blackOps, field));
        await blackOps.TickAsync();
        fixture.Clock.Seconds += 3;
        await blackOps.TickAsync();
        Assert.Equal(Jan, blackOps.Pilot.SystemId);
        Assert.Equal(6_313, Quantity(fixture, blackOps, Oxygen));
    }

    [Fact]
    public void JumpFreighterRejectsCovertFleetField()
    {
        using Fixture fixture = new(emitterHull: Rapier, generator: CapitalTravelRules.CovertGenerator);
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        Assert.IsType<PilotCallResult.Failure>(fixture.Jump(freighter, field));
        Assert.Equal(60_000, Quantity(fixture, freighter, Nitrogen));
        Assert.Null(fixture.Travel.Timers(freighter.Pilot.PilotId));
    }

    [Fact]
    public async Task AnsiblexChargesGateCapacitorAndRechecksBothEndpointsAtDeparture()
    {
        using Fixture fixture = new(structures: true);
        var (source, exit) = fixture.PrepareJumpBridges();
        var bridges = fixture.Db.Services.GetRequiredService<IPilotJumpBridges>();
        var operatorPilot = fixture.Db.Store.FindPilot(1000002)!;
        long owner = operatorPilot.PilotId;
        using (Client outsider = fixture.Select(1000001))
        {
            Assert.IsType<PilotCallResult.Failure>(outsider.Call("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate", source.Id));
            Assert.Empty(bridges.Network(outsider.Pilot.PilotId).Allowed);
            Assert.Equal(2, bridges.Network(outsider.Pilot.PilotId).Denied.Count);
        }
        fixture.AdmitBridgePilot(operatorPilot.CorpId);
        using Client freighter = fixture.Select(1000001);
        var directory = Assert.IsType<PilotCallResult.Success>(freighter.Call("structureDirectory", "GetJumpBridgesWithMyAccess"));
        var shape = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(directory.Reply)));
        Assert.Single(Assert.IsType<List<object?>>(shape[0]));
        Assert.Equal(new object?[] { source.Id, exit.Id }, Assert.IsType<List<object?>>(shape[1]));
        Assert.Empty(Assert.IsType<List<object?>>(shape[2]));
        var nearby = Assert.IsType<PilotCallResult.Success>(freighter.Call("structureDirectory", "GetNearbyJumpBridges"));
        Assert.Equal(2, Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(nearby.Reply))).Count);
        var linked = Assert.IsType<PilotCallResult.Success>(freighter.Call("structureJumpBridgeMgr", "GetLinkedStructure", source.Id));
        Assert.Equal(new object?[] { exit.Id, (long)SecondNullSystem }, BlueMarshal.Decode(BlueMarshal.Encode(linked.Reply)));
        var details = Assert.IsType<PilotCallResult.Success>(
            freighter.Call("beyonce", "CallComponentFromClient", source.Id, "structureJumpBridge", "GetJumpBridgeInfo")
        );
        var info = Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(details.Reply)));
        Assert.Contains("JumpBridgeAuthorizedInfo", info.Name, StringComparison.Ordinal);
        Assert.Equal(1_250_000, bridges.Info(freighter.Pilot.PilotId, freighter.Pilot.ShipId, source.Id)?.Capacitor);
        long exitModule = Assert.Single(exit.Modules).ItemId;
        Assert.IsType<PilotCallResult.Success>(Jump());
        await freighter.TickAsync();
        fixture.Structures.SetServiceOnline(owner, exit.Id, exitModule, false);
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(NullSystem, freighter.Pilot.SystemId);
        Assert.Equal(1_250_000, bridges.Info(freighter.Pilot.PilotId, freighter.Pilot.ShipId, source.Id)?.Capacitor);
        Assert.Empty(bridges.Network(freighter.Pilot.PilotId).Links);

        fixture.Structures.SetServiceOnline(owner, exit.Id, exitModule, true);
        Assert.IsType<PilotCallResult.Success>(Jump());
        await freighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(SecondNullSystem, freighter.Pilot.SystemId);
        Assert.Null(fixture.Travel.Timers(freighter.Pilot.PilotId));
        Assert.Equal(60_000, Quantity(fixture, freighter, Nitrogen));
        long chargedAt = fixture.Clock.Now;
        var charge = Charge();
        Assert.Equal((1_235_000d, chargedAt), charge);

        freighter.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(charge, Charge());
        fixture.SetUpgrade(JumpBridgeRules.SovereigntyUpgrade, SovereigntyUpgradePower.Offline, SecondNullSystem);
        Assert.Empty(bridges.Network(owner).Links);
        fixture.Structures.Unanchor(owner, exit.Id);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromDays(7));
        var packaged = fixture.Structures.Read(owner, exit.Id)!;
        Assert.Equal(StructureState.Unanchored, packaged.State);
        Assert.Throws<InvalidDataException>(() => fixture.Structures.Withdraw(owner, exit.Id, exitModule, 1, toCargo: true));
        fixture.Structures.Recover(owner, exit.Id);
        Assert.Null(fixture.Structures.Read(owner, exit.Id));
        Assert.Null(bridges.Destination(exit.Id));
        Assert.Null(fixture.Live.Economy.Inspect(world => world.Set<SavedJumpBridge>().Find(exit.Id)));
        var recovered = fixture.Inventory.List(owner, operatorPilot.ShipId);
        Assert.Equal(1, Assert.Single(recovered, item => item.TypeId == JumpBridgeRules.Ansiblex).Quantity);
        Assert.DoesNotContain(recovered, item => item.TypeId == JumpBridgeRules.Service);

        PilotCallResult Jump() => freighter.Call("structureJumpBridgeMgr", "CmdJumpThroughStructureStargate", source.Id);
        (double Charge, long At) Charge() =>
            fixture.Live.Economy.Inspect(world =>
            {
                var state = world.Set<SavedJumpBridge>().Find(source.Id)!;
                return (state.Capacitor, state.ChargedAt);
            });
    }

    [Fact]
    public async Task TurretDamagePersistsAndDestructionRetiresFleetDeployable()
    {
        using Fixture fixture = new(armed: true);
        var (item, _) = fixture.PrepareDeployables();
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        fixture.Join(emitter);
        Assert.IsType<PilotCallResult.Success>(emitter.Call("ship", "Drop", new List<object?> { item }, null, false));
        long pilot = emitter.Pilot.PilotId;
        long deployable = Assert.Single(fixture.Travel.Deployables(Jan)).Id;
        fixture.Clock.Seconds += 120;
        var field = Assert.Single(fixture.Travel.FleetFields(freighter.Pilot.PilotId));
        var combat = fixture.Db.Services.GetRequiredService<IPilotCombat>();
        var hit = combat.Fire(pilot, fixture.Gun!.Value, deployable, SpacePosition.Zero, fixture.Clock.Now, 0.1, WeaponActivation.Laser);
        Assert.True(hit.Applied > 0);
        Assert.False(hit.Destroyed);
        Assert.Equal(hit.Health, Assert.Single(fixture.Travel.Deployables(Jan)).CynoDeployable!.Health);
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(hit.Health, Assert.Single(fixture.Travel.Deployables(Jan)).CynoDeployable!.Health);

        fixture.Clock.Seconds += 60;
        using (var action = fixture.Live.Economy.Begin(new(systems: [Jan])))
        {
            var saved = action.Set<SavedCynoDeployable>().Find(deployable)!;
            saved.Shield = saved.Armor = 0;
            saved.Structure = 0.01;
            saved.DamagedAt = fixture.Clock.Now;
            action.Commit();
        }
        var fatal = combat.Fire(pilot, fixture.Gun.Value, deployable, SpacePosition.Zero, fixture.Clock.Now, 0.1, WeaponActivation.Laser);
        Assert.True(fatal.Destroyed);
        Assert.Empty(fixture.Travel.Fields(Jan));
        Assert.Empty(fixture.Travel.Deployables(Jan));
        Assert.Empty(fixture.Travel.FleetFields(freighter.Pilot.PilotId));
        Assert.IsType<PilotCallResult.Failure>(
            freighter.Call("beyonce", "CmdJumpToFleetDeployableBeacon", deployable, Jan, field.Beacon.Id)
        );
    }

    [Fact]
    public async Task MobileCynoWarmsForLaunchFleetAndInhibitsModuleIgnition()
    {
        using Fixture fixture = new();
        var (beaconItem, inhibitorItem) = fixture.PrepareDeployables();
        using Client freighter = fixture.Select(1000001);
        using Client operatorPilot = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Failure>(Drop(beaconItem));
        long fleet = fixture.Join(operatorPilot);
        var launch = Assert.IsType<PilotCallResult.Success>(Drop(beaconItem));
        var result = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(launch.Reply)));
        long deployable = Assert.IsType<long>(Assert.Single(Assert.IsType<List<object?>>(result[beaconItem])));
        Assert.IsType<PilotCallResult.Success>(Drop(inhibitorItem));
        Assert.Equal(0, Quantity(fixture, operatorPilot, CapitalTravelRules.MobileBeacon));
        Assert.Equal(0, Quantity(fixture, operatorPilot, CapitalTravelRules.MobileInhibitor));
        Assert.False(Assert.Single(fixture.Travel.Deployables(Jan), body => body.Id == deployable).CynoDeployable!.Active);
        Assert.Empty(fixture.Travel.FleetFields(freighter.Pilot.PilotId));
        fixture.Fleets.Leave(operatorPilot.Pilot.PilotId, fleet);

        fixture.Clock.Seconds += 60;
        Assert.IsType<PilotCallResult.Failure>(operatorPilot.Call("dogmaIM", "Activate", fixture.Module, "cynosuralGeneration", null, 0));
        Assert.Equal(1000, Quantity(fixture, operatorPilot, LiquidOzone));
        Assert.Empty(fixture.Travel.FleetFields(freighter.Pilot.PilotId));
        fixture.Clock.Seconds += 60;
        var field = Assert.Single(fixture.Travel.FleetFields(freighter.Pilot.PilotId));
        Assert.Equal(deployable, field.DeployableId);
        Assert.Equal(CapitalTravelRules.MobileField, field.Beacon.TypeId);
        Assert.Equal(1, fixture.Travel.BeaconCounts()[Jan]);
        await freighter.TickAsync();
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "OnFleetJumpBeaconDeployableChange"));
        Assert.IsType<PilotCallResult.Failure>(
            freighter.Call("beyonce", "CmdJumpToFleetDeployableBeacon", deployable, Jan, field.Beacon.Id + 1)
        );
        Assert.IsType<PilotCallResult.Success>(
            freighter.Call("beyonce", "CmdJumpToFleetDeployableBeacon", deployable, Jan, field.Beacon.Id)
        );
        await freighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(Jan, freighter.Pilot.SystemId);
        Assert.Equal(12_587, Quantity(fixture, freighter, Nitrogen));

        freighter.Dispose();
        operatorPilot.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(field, Assert.Single(fixture.Travel.Fields(Jan)));
        Assert.Equal(2, fixture.Travel.Deployables(Jan).Count);
        fixture.Clock.Seconds = 3600;
        Assert.Empty(fixture.Travel.Fields(Jan));
        Assert.Empty(fixture.Travel.Deployables(Jan));
        Assert.Empty(fixture.Travel.BeaconCounts());

        PilotCallResult Drop(long item) =>
            operatorPilot.Call("ship", "Drop", new List<object?> { new object?[] { item, 1L } }, null, false);
    }

    [Fact]
    public async Task PharoluxJumpRechecksAccessAndJammerDeadline()
    {
        using Fixture fixture = new(emitterHull: Rapier, generator: CapitalTravelRules.StandardGenerator, structures: true);
        var (beacon, jammer) = fixture.PrepareNavigation();
        using Client freighter = fixture.Select(1000001);
        using Client operatorPilot = fixture.Select(1000002);
        long actor = operatorPilot.Pilot.PilotId;
        Assert.Empty(ReadBeacons());
        Assert.IsType<PilotCallResult.Failure>(Jump());
        fixture.Structures.SetAccess(actor, beacon.Id, false, [new(freighter.Pilot.PilotId, true)]);
        var entry = Assert.IsType<List<object?>>(Assert.Single(ReadBeacons()));
        Assert.Equal(6, entry.Count);
        Assert.Equal(
            new object?[] { beacon.Id, (long)beacon.Profile.TypeId, (long)beacon.CorporationId, (long)NullSystem, 110L },
            entry.Take(5)
        );
        Assert.Equal(beacon.Name, BlueMarshal.Text(entry[5]));
        Assert.Equal(
            (long)NullSystem,
            Assert.Single(Assert.IsType<List<object?>>(Reply(freighter.Call("structureDirectory", "GetSolarSystemsWithBeacons"))))
        );

        var module = Assert.Single(jammer.Modules);
        fixture.Structures.SetServiceOnline(actor, jammer.Id, module.ItemId, true);
        long deadline = fixture.Clock.Now + 5 * TimeSpan.TicksPerMinute;
        Assert.Equal(deadline, Reply(operatorPilot.Call("beyonce", "GetCynoJammerState")));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromSeconds(298));
        Assert.IsType<PilotCallResult.Success>(Jump());
        await freighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(Jita, freighter.Pilot.SystemId);
        Assert.Equal(60_000, Quantity(fixture, freighter, Nitrogen));
        Assert.Null(fixture.Travel.Timers(freighter.Pilot.PilotId));
        Assert.Empty(ReadBeacons());
        Assert.IsType<PilotCallResult.Failure>(operatorPilot.Call("dogmaIM", "Activate", fixture.Module, "cynosuralGeneration", null, 0));
        Assert.Equal(1000, Quantity(fixture, operatorPilot, LiquidOzone));

        freighter.Dispose();
        operatorPilot.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        using Client reconnectedFreighter = fixture.Select(1000001);
        using Client reconnectedOperator = fixture.Select(1000002);
        Assert.Equal(deadline, Reply(reconnectedOperator.Call("beyonce", "GetCynoJammerState")));
        fixture.SetUpgrade(CapitalTravelRules.CynosuralSuppressionUpgrade, SovereigntyUpgradePower.Offline);
        Assert.Null(Reply(reconnectedOperator.Call("beyonce", "GetCynoJammerState")));
        Assert.False(Assert.Single(fixture.Structures.Read(actor, jammer.Id)!.Modules).Online);
        Assert.IsType<PilotCallResult.Success>(
            reconnectedFreighter.Call("structureCynoBeaconMgr", "CmdJumpToStructureBeacon", beacon.Id, NullSystem)
        );
        await reconnectedFreighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await reconnectedFreighter.TickAsync();
        Assert.Equal(NullSystem, reconnectedFreighter.Pilot.SystemId);
        Assert.InRange(Quantity(fixture, reconnectedFreighter, Nitrogen), 1, 59_999);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.NotEmpty(fixture.Db.Services.GetRequiredService<ICapitalNavigation>().AccessibleBeacons(reconnectedFreighter.Pilot.PilotId));
        Assert.DoesNotContain(fixture.Structures.Inventory(actor, beacon.Id), item => item.FlagId == StructureRules.FuelBayFlag);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Empty(fixture.Db.Services.GetRequiredService<ICapitalNavigation>().AccessibleBeacons(reconnectedFreighter.Pilot.PilotId));
        Assert.False(Assert.Single(fixture.Structures.Read(actor, beacon.Id)!.Modules).Online);

        List<object?> ReadBeacons() =>
            Assert.IsType<List<object?>>(Reply(freighter.Call("structureDirectory", "GetMyAccessibleOnlineCynoBeaconStructures")));
        PilotCallResult Jump() => freighter.Call("structureCynoBeaconMgr", "CmdJumpToStructureBeacon", beacon.Id, NullSystem);
        static object? Reply(PilotCallResult result) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
    }

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
    public async Task RapierStandardCynoAcceptsFleetJumpFreighter()
    {
        using Fixture fixture = new(emitterHull: Rapier, generator: CapitalTravelRules.StandardGenerator);
        using Client freighter = fixture.Select(1000001);
        using Client emitter = fixture.Select(1000002);
        var field = fixture.Light(emitter);
        Assert.Equal(CapitalTravelRules.StandardField, field.Beacon.TypeId);
        Assert.Equal(910, Quantity(fixture, emitter, LiquidOzone));
        Assert.Equal(fixture.Clock.Now, field.StartedAt);
        Assert.Equal(5 * TimeSpan.TicksPerMinute, field.EndsAt - field.StartedAt);
        Assert.Equal(field, Assert.Single(fixture.Travel.FleetFields(freighter.Pilot.PilotId)));
        await freighter.TickAsync();
        Assert.Contains(freighter.Packets, packet => ContainsText(packet, "OnFleetJumpBeaconModuleChange"));
        Assert.IsType<PilotCallResult.Success>(fixture.Jump(freighter, field));
        await freighter.TickAsync();
        fixture.Clock.Seconds += 3;
        await freighter.TickAsync();
        Assert.Equal(Jan, freighter.Pilot.SystemId);
        Assert.Equal(12_587, Quantity(fixture, freighter, Nitrogen));

        freighter.Dispose();
        emitter.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen();
        Assert.Equal(field, fixture.Travel.Field(field.PilotId));
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
        internal IPilotStructures Structures => Db.Services.GetRequiredService<IPilotStructures>();
        internal long FreighterId { get; }
        internal long Module { get; }
        internal long? Gun { get; }
        internal long CargoId { get; }
        private bool _joined;

        internal Fixture(
            int hull = Rhea,
            int isotope = Nitrogen,
            int emitterHull = (int)StarterShipType.Venture,
            int generator = CapitalTravelRules.IndustrialGenerator,
            bool structures = false,
            bool armed = false,
            int driveFuel = 60_000,
            bool fittedPortal = false
        )
        {
            Db = new(clock: Clock);
            var database = new WorldDatabase(Db.Path);
            PassiveCatalogFixture.LoadCapitalTravel(database.CatalogPath);
            if (structures)
            {
                PassiveCatalogFixture.LoadStructures(database.CatalogPath);
            }
            using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO NpcCorporation VALUES (1000015,'Caldari Steel')");
                catalog.Database.ExecuteSql(
                    $"INSERT INTO Station VALUES ({JanStation},{Jan},1531,1000015,39,40088235,{s_station.X},{s_station.Y},{s_station.Z})"
                );
                if (structures)
                {
                    catalog.Database.ExecuteSql($"UPDATE ItemType SET Capacity=50000 WHERE Id={emitterHull}");
                    catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value=50000 WHERE ItemTypeId={emitterHull} AND AttributeId=38");
                }
            }
            Live = new(database, Clock, _ => { });
            var first = Db.Store.FindPilot(1000001)!;
            var second = Db.Store.FindPilot(1000002)!;
            var assembly = Db.Services.GetRequiredService<IPilotFleet>();
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            Train(first.PilotId, [.. HullRequirements(hull), (3456, 5)]);
            Train(
                second.PilotId,
                [
                    .. HullRequirements(emitterHull),
                    (CapitalTravelRules.CynosuralFieldTheory, generator == CapitalTravelRules.CovertGenerator ? 5 : 1),
                ]
            );
            FreighterId = Board(first, hull);
            if (fittedPortal)
            {
                int portalType = CapitalTravelRules.CovertPortalGenerator;
                Train(first.PilotId, [.. HullRequirements(portalType)]);
                long portal = Grant(first.PilotId, first.StationId, InventoryFlag.Hangar, portalType, 1);
                fitting.Fit(first.PilotId, [portal], first.StationId, (int)InventoryFlag.AutoFit);
                fitting.SetOnline(first.PilotId, FreighterId, portal, true);
            }
            long venture = Board(second, emitterHull);
            Module = Grant(second.PilotId, second.StationId, InventoryFlag.Hangar, generator, 1);
            fitting.Fit(second.PilotId, [Module], second.StationId, (int)InventoryFlag.AutoFit);
            fitting.SetOnline(second.PilotId, venture, Module, true);
            if (armed)
            {
                const int civilianRailgun = 3638;
                Train(second.PilotId, [.. HullRequirements(civilianRailgun)]);
                Gun = Grant(second.PilotId, second.StationId, InventoryFlag.Hangar, civilianRailgun, 1);
                fitting.Fit(second.PilotId, [Gun.Value], second.StationId, (int)InventoryFlag.AutoFit);
                fitting.SetOnline(second.PilotId, venture, Gun.Value, true);
            }
            Grant(second.PilotId, venture, InventoryFlag.Cargo, LiquidOzone, 1000);
            int firstStack = Math.Min(14_000, driveFuel / 2);
            Grant(first.PilotId, FreighterId, InventoryFlag.FuelBay, isotope, firstStack);
            Grant(first.PilotId, FreighterId, InventoryFlag.FuelBay, isotope, driveFuel - firstStack);
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

        internal long AddUpwell(int type, int system, SpacePosition point, int corporation)
        {
            using var action = Live.Economy.Begin(new(systems: [system]));
            long id = StructureRules.FirstStructureId + action.NextItemId();
            action.Add(
                new SavedStructure
                {
                    Id = id,
                    TypeId = type,
                    CorporationId = corporation,
                    SystemId = system,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                }
            );
            action.Commit();
            return id;
        }

        internal long AssembleAndBoard(DockedPilot pilot, int type, long? deliveredPackage = null)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath);
            Assert.True(Eden.World.Fittings.HullCatalog.UsesCatalogBonuses(catalog, type));
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            var attributes = fitting.Attributes(type);
            Train(
                pilot.PilotId,
                InventoryDefinitions
                    .RequiredSkills.Select(req =>
                        ((int)attributes.GetValueOrDefault(req.Skill), (int)attributes.GetValueOrDefault(req.Level))
                    )
                    .Where(req => req.Item1 > 0)
                    .ToArray()
            );
            var fleet = Db.Services.GetRequiredService<IPilotFleet>();
            long package = deliveredPackage ?? Grant(pilot.PilotId, pilot.DockedLocationId, InventoryFlag.Hangar, type, 1);
            long assembled = Assert.Single(fleet.Assemble(pilot.PilotId, [package], null).ShipIds);
            return fleet.Board(pilot.PilotId, assembled, pilot.ShipId).Pilot.ShipId;
        }

        internal long FitCapitalModule(int type, long account = 1000001)
        {
            var pilot = Db.Store.FindPilot(account)!;
            var fitting = Db.Services.GetRequiredService<IPilotFitting>();
            var attributes = fitting.Attributes(type);
            Train(
                pilot.PilotId,
                InventoryDefinitions
                    .RequiredSkills.Select(req =>
                        ((int)attributes.GetValueOrDefault(req.Skill), (int)attributes.GetValueOrDefault(req.Level))
                    )
                    .Where(req => req.Item1 > 0)
                    .ToArray()
            );
            // Fit through the normal docked path, then restore the prepared flight position.
            if (pilot.Position is not null)
            {
                using var action = Live.Economy.Begin(new(pilots: [pilot.PilotId]));
                action.Remove(action.Positions.Find(pilot.ShipId)!);
                action.Commit();
            }
            long module = Grant(pilot.PilotId, pilot.DockedLocationId, InventoryFlag.Hangar, type, 1);
            fitting.Fit(pilot.PilotId, [module], pilot.DockedLocationId, (int)InventoryFlag.AutoFit);
            fitting.SetOnline(pilot.PilotId, pilot.ShipId, module, true);
            if (pilot.Position is { } position)
            {
                Db.Store.SetPosition(pilot.ShipId, position, pilot.SystemId);
            }
            return module;
        }

        internal long PutInFighterBay(int type, int quantity) => PutInTrainedHold(type, quantity, InventoryFlag.FighterBay);

        internal long PutInTrainedHold(int type, int quantity, InventoryFlag flag)
        {
            var pilot = Db.Store.FindPilot(1000001)!;
            var attributes = Db.Services.GetRequiredService<IPilotFitting>().Attributes(type);
            Train(
                pilot.PilotId,
                InventoryDefinitions
                    .RequiredSkills.Select(req =>
                        ((int)attributes.GetValueOrDefault(req.Skill), (int)attributes.GetValueOrDefault(req.Level))
                    )
                    .Where(req => req.Item1 > 0)
                    .ToArray()
            );
            return Grant(pilot.PilotId, pilot.ShipId, flag, type, quantity);
        }

        internal long PutInEscortHold(int type, int quantity, InventoryFlag flag)
        {
            var pilot = Db.Store.FindPilot(1000002)!;
            return Grant(pilot.PilotId, pilot.ShipId, flag, type, quantity);
        }

        internal long PutInCapitalCargo(int type, int quantity)
        {
            var pilot = Db.Store.FindPilot(1000001)!;
            return Grant(pilot.PilotId, pilot.ShipId, InventoryFlag.Cargo, type, quantity);
        }

        internal void CalibrateDrive() => Train(Db.Store.FindPilot(1000001)!.PilotId, (CapitalTravelRules.JumpDriveCalibration, 5));

        internal long PrepareCovertMobile()
        {
            const int anchoring = 11584;
            var pilot = Db.Store.FindPilot(1000002)!;
            Train(pilot.PilotId, (anchoring, 5));
            return Grant(pilot.PilotId, pilot.ShipId, InventoryFlag.Cargo, CapitalTravelRules.CovertMobileBeacon, 1);
        }

        internal void MoveSecondToJita()
        {
            long ship = Db.Store.FindPilot(1000002)!.ShipId;
            Db.Store.SetPosition(ship, new(10_001_000, 0, 0), Jita);
        }

        internal (long Beacon, long Inhibitor) PrepareDeployables()
        {
            const int anchoring = 11584;
            var pilot = Db.Store.FindPilot(1000002)!;
            Train(pilot.PilotId, (anchoring, 3));
            Db.Store.SetPosition(pilot.ShipId, s_station + new SpacePosition(-250_000, 0, 0), Jan);
            return (
                Grant(pilot.PilotId, pilot.ShipId, InventoryFlag.Cargo, CapitalTravelRules.MobileBeacon, 1),
                Grant(pilot.PilotId, pilot.ShipId, InventoryFlag.Cargo, CapitalTravelRules.MobileInhibitor, 1)
            );
        }

        internal (StructureSnapshot Beacon, StructureSnapshot Jammer) PrepareNavigation()
        {
            const int corporationId = 98000001;
            const int allianceId = 99000001;
            const int fuelType = 4051;
            var owner = Db.Store.FindPilot(1000002)!;
            long pilot = owner.PilotId;
            long ship = owner.ShipId;
            Train(
                Db.Store.FindPilot(1000001)!.PilotId,
                (CapitalTravelRules.JumpDriveCalibration, 5),
                (CapitalTravelRules.JumpFuelConservation, 5)
            );
            using (var action = Live.Economy.Begin(new(systems: [NullSystem], pilots: [pilot])))
            {
                action.Add(
                    new SavedCorporation
                    {
                        Id = corporationId,
                        Name = "Navigation",
                        Ticker = "NAV",
                        MemberLimit = 10,
                        CeoId = pilot,
                        CreatorId = pilot,
                        StationId = owner.StationId,
                        SystemId = NullSystem,
                        CreatedAt = Clock.Now - 8 * TimeSpan.TicksPerDay,
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(PlayerCorporationTests.Details("Navigation", "NAV")),
                    }
                );
                action.Pilots.Find(pilot)!.CorporationId = corporationId;
                action.Add(new InventoryOwner { Id = corporationId });
                action.Add(
                    new SavedAlliance
                    {
                        Id = allianceId,
                        Name = "Navigation Alliance",
                        Ticker = "NAV",
                        CreatorId = pilot,
                        CreatorCorporationId = corporationId,
                        ExecutorCorporationId = corporationId,
                        CreatedAt = Clock.Now,
                    }
                );
                action.Add(
                    new SavedAllianceMember
                    {
                        AllianceId = allianceId,
                        CorporationId = corporationId,
                        JoinedAt = Clock.Now,
                    }
                );
                long hubId = action.NextItemId();
                action.Add(
                    new SavedStructure
                    {
                        Id = hubId,
                        TypeId = EntosisRules.HubTypeId,
                        CorporationId = corporationId,
                        SystemId = NullSystem,
                        State = StructureState.ShieldVulnerable,
                        HasCore = true,
                        X = 30_000_000,
                    }
                );
                action.Add(
                    new SavedSovereigntyClaim
                    {
                        SystemId = NullSystem,
                        ClaimedAt = Clock.Now,
                        HubId = hubId,
                        CorporationId = corporationId,
                        AllianceId = allianceId,
                    }
                );
                foreach (
                    int type in new[] { CapitalTravelRules.CynosuralNavigationUpgrade, CapitalTravelRules.CynosuralSuppressionUpgrade }
                )
                {
                    action.Add(
                        new SavedSovereigntyUpgrade
                        {
                            Id = action.NextItemId(),
                            HubId = hubId,
                            SystemId = NullSystem,
                            TypeId = type,
                            ConfiguredOnline = true,
                            PowerState = SovereigntyUpgradePower.Online,
                        }
                    );
                }
                action.Commit();
            }
            var beacon = Deploy(CapitalTravelRules.Pharolux, new(10_000_000, 0, 0), "Pharolux");
            var jammer = Deploy(CapitalTravelRules.Tenebrex, new(20_000_000, 0, 0), "Tenebrex");
            var duplicate = Assert.Throws<InvalidDataException>(() =>
                Deploy(CapitalTravelRules.Tenebrex, new(40_000_000, 0, 0), "Second Tenebrex")
            );
            Assert.Contains("maximum number", duplicate.Message, StringComparison.Ordinal);
            Clock.AdvanceWallTime(TimeSpan.FromHours(1));
            Assert.True(Structures.Read(pilot, beacon.Id)!.HasCore);
            Assert.True(Structures.Read(pilot, jammer.Id)!.HasCore);
            Fuel(beacon, 1095);
            Fuel(jammer, 2880);
            long module = Assert.Single(beacon.Modules).ItemId;
            var online = Structures.SetServiceOnline(pilot, beacon.Id, module, true).Structure;
            Assert.True(Assert.Single(online.Modules).Online);
            Assert.Equal(15, Structures.Inventory(pilot, beacon.Id).Single(item => item.FlagId == StructureRules.FuelBayFlag).Quantity);
            return (online, jammer);

            StructureSnapshot Deploy(int type, SpacePosition point, string name)
            {
                Db.Store.SetPosition(ship, point, NullSystem);
                return Structures.Deploy(pilot, new(Grant(pilot, ship, InventoryFlag.Cargo, type, 1), point, 0, name)).Structure;
            }

            void Fuel(StructureSnapshot structure, int blocks)
            {
                Db.Store.SetPosition(ship, structure.Position + new SpacePosition(25_000, 0, 0), NullSystem);
                Structures.DepositFuel(pilot, structure.Id, Grant(pilot, ship, InventoryFlag.Cargo, fuelType, blocks));
            }
        }

        internal (StructureSnapshot Source, StructureSnapshot Exit) PrepareJumpBridges()
        {
            PrepareNavigation();
            var owner = Db.Store.FindPilot(1000002)!;
            long pilot = owner.PilotId;
            long ship = owner.ShipId;
            using (var action = Live.Economy.Begin(new(systems: [NullSystem, SecondNullSystem])))
            {
                var first = action.Set<SavedSovereigntyClaim>().Find(NullSystem)!;
                long secondHub = action.NextItemId();
                action.Add(
                    new SavedStructure
                    {
                        Id = secondHub,
                        TypeId = EntosisRules.HubTypeId,
                        CorporationId = owner.CorpId,
                        SystemId = SecondNullSystem,
                        State = StructureState.ShieldVulnerable,
                        HasCore = true,
                        X = 30_000_000,
                    }
                );
                action.Add(
                    new SavedSovereigntyClaim
                    {
                        SystemId = SecondNullSystem,
                        HubId = secondHub,
                        CorporationId = owner.CorpId,
                        AllianceId = first.AllianceId,
                        ClaimedAt = Clock.Now,
                    }
                );
                foreach (var endpoint in new[] { (NullSystem, first.HubId), (SecondNullSystem, secondHub) })
                {
                    action.Add(
                        new SavedSovereigntyUpgrade
                        {
                            Id = action.NextItemId(),
                            HubId = endpoint.Item2,
                            SystemId = endpoint.Item1,
                            TypeId = JumpBridgeRules.SovereigntyUpgrade,
                            ConfiguredOnline = true,
                            PowerState = SovereigntyUpgradePower.Online,
                        }
                    );
                }
                action.Commit();
            }
            var source = Deploy(NullSystem, SecondNullSystem);
            var destination = Deploy(SecondNullSystem, NullSystem);
            Clock.AdvanceWallTime(TimeSpan.FromHours(1));
            source = Enable(source);
            destination = Enable(destination);
            Db.Store.SetPosition(FreighterId, source.Position + new SpacePosition(source.Profile.Radius + 1000, 0, 0), NullSystem);
            return (source, destination);

            StructureSnapshot Deploy(int system, int peer)
            {
                SpacePosition point = new(1_000_000_000, 0, 0);
                Db.Store.SetPosition(ship, point, system);
                var item = Grant(pilot, ship, InventoryFlag.Cargo, JumpBridgeRules.Ansiblex, 1);
                using var client = Select(1000002);
                var extra = new BlueInstance("util.KeyVal", new Dictionary<string, object?> { ["destinationSolarsystemID"] = peer });
                var result = client.Call(
                    "structureDeployment",
                    "Anchor",
                    item,
                    0d,
                    0d,
                    0d,
                    (long)owner.CorpId,
                    "Ansiblex",
                    "",
                    4L,
                    12L,
                    extra
                );
                Assert.True(result is PilotCallResult.Success, result.ToString());
                return Assert.Single(Structures.List(pilot, system), structure => structure.Profile.TypeId == JumpBridgeRules.Ansiblex);
            }

            StructureSnapshot Enable(StructureSnapshot endpoint)
            {
                const int fuelBlock = 4051;
                Db.Store.SetPosition(ship, endpoint.Position + new SpacePosition(25_000, 0, 0), endpoint.SystemId);
                Structures.DepositFuel(pilot, endpoint.Id, Grant(pilot, ship, InventoryFlag.Cargo, fuelBlock, 4500));
                return Structures.SetServiceOnline(pilot, endpoint.Id, Assert.Single(endpoint.Modules).ItemId, true).Structure;
            }
        }

        internal void AdmitBridgePilot(int corporation)
        {
            long pilot = Db.Store.FindPilot(1000001)!.PilotId;
            using var action = Live.Economy.Begin(new(pilots: [pilot]));
            action.Pilots.Find(pilot)!.CorporationId = corporation;
            action.Commit();
        }

        internal void SetUpgrade(int type, SovereigntyUpgradePower power, int system = NullSystem)
        {
            using var action = Live.Economy.Begin(new(systems: [system]));
            action.Set<SavedSovereigntyUpgrade>().Single(row => row.SystemId == system && row.TypeId == type).PowerState = power;
            action.Commit();
        }

        internal CynosuralField Light(Client emitter)
        {
            if (!_joined)
            {
                Join(emitter);
            }
            var response = emitter.Call("dogmaIM", "Activate", Module, "cynosuralGeneration", null, 0);
            Assert.True(response is PilotCallResult.Success, response.ToString());
            return Assert.IsType<CynosuralField>(Travel.Field(emitter.Pilot.PilotId));
        }

        internal long Join(Client emitter)
        {
            long other = Db.Store.FindPilot(1000001)!.PilotId;
            long fleet = Fleets.Create(emitter.Pilot.PilotId).Id;
            Fleets.Invite(emitter.Pilot.PilotId, fleet, other, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
            Fleets.Accept(other, fleet);
            _joined = true;
            return fleet;
        }

        internal PilotCallResult Jump(Client client, CynosuralField field) =>
            client.Call("beyonce", "CmdJumpToFleetModuleBeacon", field.PilotId, field.Beacon.Id, field.SystemId);

        internal void Reopen(bool restart = false)
        {
            Live.Dispose();
            var database = new WorldDatabase(Db.Path);
            if (restart)
            {
                WorldInitialization.Initialize(database, Clock);
            }
            Live = new(database, Clock, _ => { });
        }

        internal void Train(long pilot, params (int Skill, int Level)[] skills)
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

        internal long Grant(long pilot, long location, InventoryFlag flag, int type, int quantity)
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

        internal PilotCallResult CallWithKeywords(
            string service,
            string method,
            Dictionary<string, object?> keywords,
            params object?[] args
        ) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

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
