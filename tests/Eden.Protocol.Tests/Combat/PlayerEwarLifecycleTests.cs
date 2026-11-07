using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class PlayerEwarTests
{
    [Fact]
    public void EarnedRacialSensorCompensationRaisesTheActualDefenseAgainstEcmOnce()
    {
        using Fixture f = Prepare();
        double baseline = f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.GravimetricStrength];
        Assert.True(baseline > 0);
        f.Train((int)ElectronicWarfareSkill.GravimetricSensorCompensation, 5);
        Assert.Equal(baseline * 1.2, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.GravimetricStrength], 7);
    }

    [Theory]
    [InlineData(609, 3328, 1968, DogmaAttributeId.TargetRangeBonus, 1.3, .6)]
    [InlineData(584, 3330, 1948, DogmaAttributeId.MagnetometricJammingStrength, 1.6, .6)]
    [InlineData(3766, 3329, 12709, DogmaAttributeId.SignatureRadiusBonus, 1.3, 1)]
    [InlineData(596, 3327, 2108, DogmaAttributeId.TrackingSpeedBonus, 1.15, 1)]
    public void AdmittedHullRoleAndEarnedFrigateBonusesAffectTheirElectronicWarfareEquipment(
        int hull,
        int skill,
        int moduleType,
        DogmaAttributeId bonus,
        double strengthMultiplier,
        double costMultiplier
    )
    {
        using Fixture f = Prepare();
        long module = f.Fit(moduleType);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var baseline = PilotFittingStore.ReadAttributes(catalog, moduleType);
        f.Train(skill, 4);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(f.ShipId)!.TypeId = hull;
            world.SaveChanges();
        }
        var actual = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(baseline[bonus] * strengthMultiplier, actual[bonus], 7);
        Assert.Equal(baseline[DogmaAttributeId.CapacitorNeed] * .75 * costMultiplier, actual[DogmaAttributeId.CapacitorNeed], 7);
    }

    [Fact]
    public void ProductionDispatchPrimesActiveEffectsAndReplaysObserversAndSnapshotsWithoutRestartingTheHudRing()
    {
        using Fixture f = Prepare();
        long module = f.Fit(12709);
        Space(f);
        using Clients clients = new(f);
        using var scope = f.Db.Services.CreateScope();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<PlayerEwarHandler>();
        targeting.Call(clients.Pilot, "dogmaIM", "AddTarget", [Wing(f)]);
        f.Clock.Seconds += 30;
        targeting.Advance(clients.Pilot);
        clients.Pilot.Notifications.Clear();
        Assert.Same(
            PilotCallResult.Unsupported,
            activation.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteTargetPaintFalloff", Wing(f), true])
        );
        Assert.Empty(Store(f).Incoming(Wing(f)));
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(clients.Pilot, "dogmaIM", "Activate", [module, "remoteTargetPaintFalloff", Wing(f), 2L])
        );
        var prime = controller.ActiveEffect(clients.Pilot.Pilot, module);
        Assert.NotNull(prime);
        Assert.Single(clients.Pilot.Notifications, n => n.Name == "OnGodmaShipEffect");
        clients.Wing.Flight.RefreshPeers();
        Assert.Contains(
            clients.Wing.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Wing(f)
                && fx.Graphic == "effects.TargetPaint"
                && fx.Start
        );
        clients.Wing.Flight.Updates.Clear();
        clients.Wing.Flight.Snapshot();
        Assert.Contains(
            clients.Wing.Flight.Updates,
            u =>
                u.Command is DestinyCommand.TimedSpecialEffect fx
                && fx.ModuleId == module
                && fx.TargetId == Wing(f)
                && fx.Start
                && fx.Duration == 5000
        );
        f.Clock.Seconds += 5;
        controller.Advance(clients.Pilot);
        Assert.Single(clients.Pilot.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.IsType<PilotCallResult.Success>(
            activation.Call(clients.Pilot, "dogmaIM", "Deactivate", [module, "remoteTargetPaintFalloff"])
        );
        Assert.NotEmpty(Store(f).Incoming(Wing(f)));
        f.Clock.Seconds += 5;
        controller.Advance(clients.Pilot);
        Assert.Empty(Store(f).Incoming(Wing(f)));
        Assert.Null(controller.ActiveEffect(clients.Pilot.Pilot, module));
    }

    [Theory]
    [InlineData(25561, 7)]
    [InlineData(25563, 10)]
    [InlineData(25565, 8)]
    [InlineData(85006, 11)]
    [InlineData(85007, 12)]
    [InlineData(94067, 11)]
    public void SignalDistortionAmplifiersApplyTheirOwnOnlineRangeAndRacialStrengthBonuses(int type, double percent)
    {
        using Fixture f = Prepare();
        long jammer = f.Fit(1948);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == jammer);
        var baseline = f.Fitting.ModuleAttributes(f.PilotId, item);
        long amplifier = f.Fit(type);
        var boosted = f.Fitting.ModuleAttributes(f.PilotId, item);
        Assert.Equal(baseline[DogmaAttributeId.MaxRange] * (1 + percent / 100), boosted[DogmaAttributeId.MaxRange], 7);
        Assert.Equal(
            baseline[DogmaAttributeId.MagnetometricJammingStrength] * (1 + percent / 100),
            boosted[DogmaAttributeId.MagnetometricJammingStrength],
            7
        );
        f.Fitting.SetOnline(f.PilotId, f.ShipId, amplifier, false);
        Assert.Equal(
            baseline[DogmaAttributeId.MagnetometricJammingStrength],
            f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.MagnetometricJammingStrength]
        );
    }

    [Fact]
    public async Task EcmVictimGetsNativeJamTimerAndCanLockJammerButCannotKeepAnotherPendingScan()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1948);
        NpcEncounterTests.Prepare(f.Db);
        var encounters = f.Db.Services.GetRequiredService<INpcEncounters>();
        var npc = encounters.List(30000144, f.Clock.Now)[0];
        Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var position = world.Positions.Find(Wing(f))!;
            var row = world.Set<WorldNpc>().Find(npc.Id)!;
            row.SystemId = 30000142;
            (row.X, row.Y, row.Z) = (position.X + 500, position.Y, position.Z);
            world.SaveChanges();
        }
        using Clients clients = new(f);
        using PilotCallContext wing = new(
            1000002,
            clients.Wing.Pilot.Account,
            [clients.Wing.Pilot],
            p => new(f.Db.Store, p, f.Clock, new(), [], encounters: encounters),
            1000002,
            SignedSettings.Empty
        );
        wing.Select(clients.Wing.Pilot);
        wing.Flight.Snapshot();
        using TargetingHandler targeting = new(f.Clock, f.Fitting, new(), electronicWarfare: Store(f));
        var scan = Assert.IsType<TargetLockReply>(
            Assert.IsType<PilotCallResult.Success>(targeting.Call(wing, "dogmaIM", "AddTarget", [npc.Id])).Reply
        );
        Assert.NotNull(scan.Completion);
        Activate(f, module, DogmaEffect.PlayerEcm);
        targeting.Advance(wing);
        Assert.True(scan.Completion!.IsCompletedSuccessfully);
        Assert.IsType<PilotCallResult.NamedFailure>(await scan.Completion);
        Assert.IsType<PilotCallResult.Failure>(targeting.Call(wing, "dogmaIM", "AddTarget", [npc.Id]));
        Assert.IsType<PilotCallResult.Success>(clients.WingTargeting.Call(clients.Wing, "dogmaIM", "AddTarget", [f.ShipId]));
        f.Clock.Seconds += 15;
        clients.WingTargeting.Advance(clients.Wing);
        Assert.True(clients.WingTargeting.IsLocked(f.ShipId));
        clients.WingHandler.Advance(clients.Wing);
        var jam = Assert.Single(clients.Wing.NotificationsBeforeFlight.OfType<PlayerJamChanged>());
        Assert.True(jam.Active);
        Assert.Equal(6, jam.Arguments.Length);
        Assert.Equal("electronic", jam.Arguments[3]);
        Assert.Equal(20000d, jam.Arguments[5]);
        Store(f).Stop(f.PilotId, f.ShipId, module);
        clients.WingHandler.Advance(clients.Wing);
        Assert.Contains(clients.Wing.NotificationsBeforeFlight.OfType<PlayerJamChanged>(), n => !n.Active);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(.12, false)]
    public void EcmChecksExactRacialChanceAndZeroResistanceImmunity(double roll, bool expected)
    {
        var module = PlayerEwarRules.Define(
            1,
            1948,
            DogmaEffect.PlayerEcm,
            new Dictionary<DogmaAttributeId, double>
            {
                [DogmaAttributeId.Duration] = 20000,
                [DogmaAttributeId.MaxRange] = 10000,
                [DogmaAttributeId.FalloffEffectiveness] = 10000,
                [DogmaAttributeId.MagnetometricJammingStrength] = 3.3,
                [DogmaAttributeId.GravimetricJammingStrength] = 1.1,
            }
        );
        Dictionary<DogmaAttributeId, double> target = new() { [DogmaAttributeId.GravimetricStrength] = 10 };
        Assert.Equal(expected, PlayerEwarRules.Attempt(module, 10000, target, roll) is not null);
        Assert.Null(PlayerEwarRules.Attempt(module, 10000, target, module.Power.Jamming!.Gravimetric / 10));
        target[DogmaAttributeId.EcmResistance] = 0;
        Assert.Null(PlayerEwarRules.Attempt(module, 10000, target, roll));
    }

    [Fact]
    public void VersionNinetyFiveMigrationCreatesLiveEwarStateAndPreservesInventory()
    {
        using Fixture f = Prepare();
        long module = f.Fit(1968);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldPlayerEwar; PRAGMA user_version=95");
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Empty(reopened.Set<WorldPlayerEwar>());
        Assert.Equal(1968, reopened.Set<OwnedItem>().Find(module)!.TypeId);
        Assert.True(reopened.Set<OwnedItem>().Find(module)!.Online);
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("sourceDock")]
    [InlineData("victimDock")]
    [InlineData("disconnect")]
    public void InvalidSourcesAndTargetsReleaseEffectsWithoutRefundingTheirPaidCycle(string transition)
    {
        using Fixture f = Prepare();
        long module = f.Fit(1968);
        Space(f);
        var activation = Activate(f, module, DogmaEffect.SensorDampener);
        if (transition == "disconnect")
        {
            Store(f).Cancel(f.PilotId, f.ShipId);
        }
        else
        {
            using WorldDbContext world = new(f.Db.Path);
            if (transition == "offline")
            {
                world.Set<OwnedItem>().Find(module)!.Online = false;
            }
            else
            {
                world.Remove(world.Positions.Find(transition == "sourceDock" ? f.ShipId : Wing(f))!);
            }
            world.SaveChanges();
        }
        Store(f).Sweep(f.Clock.Now);
        Assert.Empty(Store(f).Incoming(Wing(f)));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(activation.Capacitor, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(activation.Contacts[0].ExpiresAt, saved.Set<ModuleCooldown>().Find(module)!.ReadyAt);
    }
}
