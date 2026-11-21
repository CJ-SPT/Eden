using Eden.Data.World.Combat;
using Eden.Data.World.Drones;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureTetherTests
{
    private static readonly TetherFlightState s_flight = new(true, false, false, false, false);

    [Fact]
    public void LegacySpacePositionsResolveTheirSystemFromTheLastStation()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Ships.Find(f.ShipId)!.StationId = 60015159;
            action.World.Positions.Find(f.ShipId)!.SystemId = null;
            action.Commit();
        }
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.Equal(structure.Id, tethering.Advance(f.PilotId, s_flight)!.StructureId);
        Assert.True(tethering.Protected(f.ShipId));
    }

    [Fact]
    public void RepairsFittedBurnoutAndBayDronesWithoutRepairingCargoOrDeployedDrones()
    {
        using var f = StructureAttackTests.Prepare();
        var path = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadCombatDrones(path);
        f.Online();
        long fitted = f.Item(3638);
        long cargo = f.Item(3638);
        const int Hobgoblin = 2454;
        long bay = f.Item(Hobgoblin);
        long deployed = f.Item(Hobgoblin);
        using (var action = StructureAttackTests.Scope(f))
        {
            var module = action.World.Set<OwnedItem>().Find(fitted)!;
            module.FlagId = (int)InventoryFlag.HighSlot0;
            module.Singleton = true;
            module.Online = false;
            action.World.Add(new SavedModuleCondition { ModuleId = fitted, Damage = 1 });
            action.World.Add(new SavedModuleCondition { ModuleId = cargo, Damage = 1 });
            action.World.Set<OwnedItem>().Find(bay)!.FlagId = (int)InventoryFlag.DroneBay;
            action.World.Add(
                new WorldDrone
                {
                    ItemId = bay,
                    PilotId = f.PilotId,
                    ControllerShipId = f.ShipId,
                    Shield = 0,
                    ArmorDamage = 1,
                }
            );
            action.World.Add(
                new WorldDrone
                {
                    ItemId = deployed,
                    PilotId = f.PilotId,
                    ControllerShipId = f.ShipId,
                    SystemId = StructureTests.Fixture.SystemId,
                    Shield = 0,
                    ArmorDamage = 1,
                }
            );
            action.Commit();
        }
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.False(tethering.Advance(f.PilotId, s_flight)!.RepairCompleted);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(StructureTetherRules.RepairDelay));
        Assert.True(tethering.Advance(f.PilotId, s_flight)!.RepairCompleted);
        using var check = StructureAttackTests.Scope(f);
        Assert.Null(check.World.Set<SavedModuleCondition>().Find(fitted));
        Assert.False(check.World.Set<OwnedItem>().Find(fitted)!.Online);
        Assert.Equal(1, check.World.Set<SavedModuleCondition>().Find(cargo)!.Damage);
        Assert.Equal(0, check.World.Set<WorldDrone>().Find(bay)!.ArmorDamage);
        Assert.True(check.World.Set<WorldDrone>().Find(bay)!.Shield > 0);
        Assert.Equal(1, check.World.Set<WorldDrone>().Find(deployed)!.ArmorDamage);
        Assert.Equal(0, check.World.Set<WorldDrone>().Find(deployed)!.Shield);
    }

    [Fact]
    public void NativeSnapshotReplaysTetherAfterSetStateAndAnotherPilotCannotTargetTheShip()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        var factory = f.Db.Services.GetRequiredService<FlightSessionFactory>();
        using var flight = factory.Create(f.Db.Store.FindPilot(1000001)!);
        flight.Snapshot();
        int state = flight.Updates.FindLastIndex(update => update.Command is DestinyCommand.SetState);
        Assert.True(state >= 0);
        Assert.Contains(
            flight.Updates.Skip(state + 1),
            update => update.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.Tethering", Start: true }
        );
        var visitor = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(visitor.ShipId, structure.Position, StructureTests.Fixture.SystemId);
        using var observer = factory.Create(f.Db.Store.FindPilot(1000002)!);
        observer.Snapshot();
        Assert.Null(observer.FindTarget(f.ShipId));
        Assert.True(f.Db.Services.GetRequiredService<IPilotCombatPresence>().IsAreaProtected(StructureTests.Fixture.SystemId, f.ShipId));
        var locks = f.Db.Services.GetRequiredService<IPilotTargetLocks>();
        locks.Pending(f.ShipId, structure.Id);
        Assert.NotNull(observer.FindTarget(f.ShipId));
        flight.Checkpoint();
        Assert.Contains(
            flight.Updates,
            update => update.Command is DestinyCommand.TimedSpecialEffect { Graphic: "effects.Tethering", Start: false }
        );
    }

    [Fact]
    public void PlayerAttackRejectsATetheredVictimBeforePayingForTheShot()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        var victim = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(victim.ShipId, structure.Position, StructureTests.Fixture.SystemId);
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.NotNull(tethering.Advance(victim.PilotId, s_flight));
        long gun = StructureAttackTests.Gun(f);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        Assert.Throws<InvalidDataException>(() =>
            combat.FireAdvanced(
                f.PilotId,
                gun,
                victim.ShipId,
                new(victim.ShipId, SpacePosition.Zero),
                SpacePosition.Zero,
                f.Clock.Now,
                0.5,
                new(WeaponActivation.Laser)
            )
        );
        using var check = StructureAttackTests.Scope(f);
        Assert.Null(check.World.Set<ModuleCooldown>().Find(gun));
        Assert.Null(check.World.Set<SavedCrimewatch>().Find(f.PilotId));
    }

    [Theory]
    [InlineData(1.0, -2)]
    [InlineData(0.9, -2.5)]
    [InlineData(0.8, -3)]
    [InlineData(0.7, -3.5)]
    [InlineData(0.6, -4)]
    [InlineData(0.5, -4.5)]
    public void HighSecurityTetherRestrictionsUseTheActualPoliceThreshold(double system, double threshold)
    {
        Assert.False(StructureTetherRules.SecurityPermits(system, threshold));
        Assert.True(StructureTetherRules.SecurityPermits(system, threshold + 0.0001));
        Assert.True(StructureTetherRules.SecurityPermits(0.4, -10));
        Assert.False(StructureTetherRules.SecurityPermits(double.NaN, 0));
    }

    [Fact]
    public void CoreAccessAbandonmentAndWeaponsDeadlinesAreCheckedOnEveryProtectionRead()
    {
        using var f = StructureAttackTests.Prepare();
        var structure = f.Online();
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.NotNull(tethering.Advance(f.PilotId, s_flight));
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<SavedStructure>().Find(structure.Id)!.HasCore = false;
            action.Commit();
        }
        Assert.False(tethering.Protected(f.ShipId));
        using (var action = StructureAttackTests.Scope(f))
        {
            var row = action.World.Set<SavedStructure>().Find(structure.Id)!;
            row.HasCore = true;
            row.State = StructureState.HullReinforced;
            action.Commit();
        }
        Assert.True(tethering.Protected(f.ShipId));
        f.Structures.SetAccess(f.PilotId, structure.Id, false, [new(f.PilotId, false)]);
        Assert.False(tethering.Protected(f.ShipId));
        f.Structures.SetAccess(f.PilotId, structure.Id, false, []);
        Assert.True(tethering.Protected(f.ShipId));
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Add(
                new SavedCrimewatch
                {
                    PilotId = f.PilotId,
                    WeaponsUntil = f.Clock.Now + 1,
                    CriminalUntil = f.Clock.Now + TimeSpan.TicksPerHour,
                }
            );
            action.Commit();
        }
        Assert.False(tethering.Protected(f.ShipId));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.True(tethering.Protected(f.ShipId));
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<SavedStructure>().Find(structure.Id)!.LowPowerAt = f.Clock.Now - StructureRules.AbandonmentDuration;
            action.Commit();
        }
        Assert.False(tethering.Protected(f.ShipId));
        Assert.Null(tethering.Advance(f.PilotId, s_flight));
    }

    [Fact]
    public async Task SavedRepairDeadlineSurvivesRollbackAndCheckpointReopen()
    {
        using var f = StructureAttackTests.Prepare();
        f.Online();
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Add(
                new ShipHealthState
                {
                    ShipId = f.ShipId,
                    ArmorDamage = 10,
                    UpdatedAt = f.Clock.Now,
                }
            );
            action.Commit();
        }
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        var original = tethering.Advance(f.PilotId, s_flight)!;
        using (var rollback = StructureAttackTests.Scope(f))
        {
            rollback.World.Set<SavedPilotTether>().Find(f.ShipId)!.RepairCompleted = true;
        }
        Assert.False(tethering.Advance(f.PilotId, s_flight)!.RepairCompleted);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "tether.sqlite");
        File.Copy(f.Db.Path, copy);
        using LiveWorld reopened = new(new(copy), f.Clock);
        PilotTetherStore saved = new(new(copy), f.Clock);
        Assert.Equal(original, saved.Advance(f.PilotId, s_flight));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(StructureTetherRules.RepairDelay));
        Assert.True(saved.Advance(f.PilotId, s_flight)!.RepairCompleted);
        using WorldActionScope check = new(new(copy));
        Assert.Equal(0, check.World.Set<ShipHealthState>().Find(f.ShipId)!.ArmorDamage);
    }

    [Fact]
    public void TetherProtectsAtTheSurfaceBoundaryAndPendingLocksImmediatelyRemoveProtection()
    {
        using var f = StructureAttackTests.Prepare();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var structure = f.Online();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var position = structure.Position + new SpacePosition(structure.Profile.Radius + pilot.Flight.ShipRadius + 10_000, 0, 0);
        f.Db.Store.SetPosition(f.ShipId, position, StructureTests.Fixture.SystemId);
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        Assert.Equal(structure.Id, tethering.Advance(f.PilotId, s_flight)!.StructureId);
        Assert.True(tethering.Protected(f.ShipId));
        using (var metrics = SessionPerformance.Begin("warm tether reads", _ => { }, force: true)!)
        {
            Assert.True(tethering.Protected(f.ShipId));
            Assert.Equal(structure.Id, tethering.Advance(f.PilotId, s_flight)!.StructureId);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            Assert.Equal(0, metrics.TransactionStartMilliseconds);
            Assert.Equal(0, metrics.CommitMilliseconds);
        }
        using (var action = StructureAttackTests.Scope(f))
        {
            var defense = ShipHealth.DefenseFrom(action.World.Attributes(f.PilotId));
            var hit = PilotHealthStore.Apply(action.World, f.ShipId, defense, new(1_000_000, 0, 0, 0), f.Clock.Now);
            Assert.Equal(0, hit.Applied);
            Assert.Equal(defense.Undamaged, hit.Remaining);
            action.Commit();
        }
        var locks = f.Db.Services.GetRequiredService<IPilotTargetLocks>();
        locks.Pending(f.ShipId, structure.Id);
        Assert.False(tethering.Protected(f.ShipId));
        Assert.Null(tethering.Advance(f.PilotId, s_flight));
        locks.Remove(f.ShipId, structure.Id);
        Assert.NotNull(tethering.Advance(f.PilotId, s_flight));
        f.Db.Store.SetPosition(f.ShipId, position + new SpacePosition(0.01, 0, 0), StructureTests.Fixture.SystemId);
        Assert.False(tethering.Protected(f.ShipId));
        Assert.Null(tethering.Advance(f.PilotId, s_flight));
    }

    [Fact]
    public void RepairsOnceAfterTheSavedDeadlineAndDoesNotRefillCapacitorWhileTetherRemains()
    {
        using var f = StructureAttackTests.Prepare();
        f.Online();
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Add(
                new ShipHealthState
                {
                    ShipId = f.ShipId,
                    ShieldCharge = 0,
                    ArmorDamage = 1,
                    StructureDamage = 1,
                    UpdatedAt = f.Clock.Now,
                }
            );
            action.World.Add(
                new ShipEnergy
                {
                    ShipId = f.ShipId,
                    Charge = 0,
                    UpdatedAt = f.Clock.Now,
                }
            );
            action.Commit();
        }
        var tethering = f.Db.Services.GetRequiredService<IPilotTethering>();
        var first = tethering.Advance(f.PilotId, s_flight)!;
        Assert.False(first.RepairCompleted);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(StructureTetherRules.RepairDelay - 1));
        Assert.False(tethering.Advance(f.PilotId, s_flight)!.RepairCompleted);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.True(tethering.Advance(f.PilotId, s_flight)!.RepairCompleted);
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Equal(0, action.World.Set<ShipHealthState>().Find(f.ShipId)!.ArmorDamage);
            Assert.Equal(0, action.World.Set<ShipHealthState>().Find(f.ShipId)!.StructureDamage);
            var attributes = action.World.Attributes(f.PilotId);
            Assert.Equal(attributes[DogmaAttributeId.CapacitorCapacity], action.World.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
            action.World.Set<ShipEnergy>().Find(f.ShipId)!.Charge = 0;
            action.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(11));
        Assert.Equal(first.EngagedAt, tethering.Advance(f.PilotId, s_flight)!.EngagedAt);
        using var check = StructureAttackTests.Scope(f);
        Assert.Equal(0, check.World.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.True(check.World.Set<SavedPilotTether>().Find(f.ShipId)!.RepairCompleted);
    }
}
