using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
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
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class MissileCombatTests
{
    private const int LightLauncher = 499;
    private const int LightMissile = 212;
    private const double Shield = 1_000_000;

    internal static Fixture Prepare(params int[] types)
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadMissileTiers(f.CatalogPath, types.Concat(MissileDefinitions.Skills).Append(601).ToHashSet());
        NpcEncounterTests.Prepare(f.Db);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO DogmaAttribute VALUES (481,'entityKillBounty');
            INSERT OR REPLACE INTO TypeAttribute VALUES (30380,481,3000);
            INSERT INTO SourceRecord VALUES ('client/asteroidcountbytypeidbeltid','40009232','{{}}'),
                ('client/asteroidcountbytypeidbeltid','40009245','{{}}');
            """
        );
        foreach (var attribute in new[] { DogmaAttributeId.CpuOutput, DogmaAttributeId.PowerOutput })
        {
            catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == 601 && a.AttributeId == (int)attribute)
                .ExecuteUpdate(set => set.SetProperty(a => a.Value, Shield));
        }
        catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,101,8)");
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.ShieldCapacity,
                DogmaAttributeId.SignatureRadius,
                DogmaAttributeId.ShieldEmResonance,
                DogmaAttributeId.ShieldExplosiveResonance,
                DogmaAttributeId.ShieldKineticResonance,
                DogmaAttributeId.ShieldThermalResonance,
            }
        )
        {
            double value =
                attribute == DogmaAttributeId.ShieldCapacity ? Shield
                : attribute == DogmaAttributeId.SignatureRadius ? 1000
                : 1;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (30380,{(int)attribute},{value})");
        }
        return f;
    }

    internal static IPilotMissiles Missiles(Fixture f) => f.Db.Services.GetRequiredService<IPilotMissiles>();

    private static IPilotAmmunition Ammo(Fixture f) => f.Db.Services.GetRequiredService<IPilotAmmunition>();

    internal static long Load(Fixture f, int type, int charge, int quantity = 3)
    {
        long module = f.Fit(type);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            foreach (var (skill, _) in InventoryDefinitions.RequiredSkills)
            {
                int required = (int)(catalog.Set<TypeAttribute>().Find(charge, (int)skill)?.Value ?? 0);
                if (required != 0)
                {
                    f.Train(required, 5);
                }
            }
        }
        long stack = f.Grant(charge, quantity);
        Ammo(f).Load(f.PilotId, f.ShipId, [module], [stack], f.StationId, f.Clock.Now);
        f.Clock.Milliseconds += 35000;
        return module;
    }

    internal static NpcContact Space(Fixture f, double distance = 1000)
    {
        var npc = f.Db.Services.GetRequiredService<INpcEncounters>().List(30000144, f.Clock.Now)[0];
        f.Db.Store.SetPosition(f.ShipId, npc.Position + new SpacePosition(distance, 0, 0), npc.SystemId);
        return npc;
    }

    internal static MissileLaunch Launch(Fixture f, long module, long npc) =>
        Missiles(f).Launch(f.PilotId, module, npc, new HashSet<long> { npc }, new(SpacePosition.Zero, 295, new(0, 0, -1), 42), f.Clock.Now);

    [Fact]
    public void LaunchPaysOneRoundAndCooldownWhileDamageWaitsForPhysicalContact()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var projectiles = Missiles(f);
        Assert.Empty(projectiles.List(30000144));
        projectiles.Advance(30000144, f.Clock.Now);
        Assert.Empty(projectiles.TakeImpacts(f.PilotId));
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f, 10000);
        var launch = Launch(f, launcher, npc.Id);
        Assert.Equal(2, launch.Magazine.Quantity);
        Assert.Equal(14400, launch.Launcher.CycleMilliseconds, 8);
        Assert.True(launch.Projectile.Id >= MissileDefinitions.FirstProjectileId);
        Assert.Single(Missiles(f).List(npc.SystemId));
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(Shield, world.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
            Assert.Equal(f.Clock.Now + 14400 * TimeSpan.TicksPerMillisecond, world.Set<ModuleCooldown>().Find(launcher)!.ReadyAt);
        }
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher, npc.Id));
        f.Clock.Milliseconds += 1000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
        var moving = Assert.Single(Missiles(f).List(npc.SystemId));
        Assert.True(moving.Flight.Position.DistanceTo(npc.Position) < 10000);
        f.Clock.Milliseconds += 2000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        var impact = Assert.Single(Missiles(f).TakeImpacts(f.PilotId));
        Assert.Equal(103.75, impact.Applied, 7);
        Assert.Empty(Missiles(f).List(npc.SystemId));
        Assert.True(Missiles(f).Impacted(launch.Projectile.Id));
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
    }

    [Theory]
    [InlineData(10629, 266, 45.375)]
    [InlineData(10631, 24471, 70.5375)]
    [InlineData(16061, 27361, 130.625)]
    [InlineData(1875, 212, 103.75)]
    [InlineData(503, 2510, 562.5)]
    [InlineData(20539, 17857, 735.625)]
    [InlineData(32444, 32436, 1088.5416666667)]
    [InlineData(8089, 212, 103.75)]
    [InlineData(20595, 212, 103.75)]
    [InlineData(14516, 203, 515.625)]
    [InlineData(34290, 24471, 70.5375)]
    public void DifferentSizesAndTiersUseRealWarheadsAndSignatureApplication(int launcherType, int charge, double expected)
    {
        using Fixture f = Prepare(launcherType, charge);
        long launcher = Load(f, launcherType, charge);
        var npc = Space(f);
        Launch(f, launcher, npc.Id);
        f.Clock.Milliseconds += 1000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        var impact = Assert.Single(Missiles(f).TakeImpacts(f.PilotId));
        Assert.Equal(expected, impact.Applied, 6);
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void AutomaticMissilesSelectOnlyActualAggressorsWithoutALock()
    {
        using Fixture f = Prepare(LightLauncher, 269);
        long launcher = Load(f, LightLauncher, 269);
        var npc = Space(f);
        var firing = new MissileFiringState(SpacePosition.Zero, 295, new(0, 0, -1), 42);
        Assert.Throws<InvalidDataException>(() =>
            Missiles(f).Launch(f.PilotId, launcher, npc.Id, new HashSet<long>(), firing, f.Clock.Now)
        );
        Assert.Equal(3, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var innocent = world.Set<WorldNpc>().Single(n => n.Id != npc.Id);
            (innocent.X, innocent.Y, innocent.Z) = (npc.Position.X + 1100, npc.Position.Y, npc.Position.Z);
            world.Set<NpcAggression>().Add(NpcAggression.Start(npc.Id, f.ShipId, 1000, f.Clock.Now));
            world.SaveChanges();
        }
        var result = Missiles(f).Launch(f.PilotId, launcher, null, new HashSet<long>(), firing, f.Clock.Now);
        Assert.True(result.Launcher.Automatic);
        Assert.Equal(npc.Id, result.Projectile.TargetId);
    }

    [Fact]
    public void RestartAndExplicitCancellationKeepPaymentsButNeverReplayOfflineImpacts()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f);
        Launch(f, launcher, npc.Id);
        WorldInitialization.Open(f.Db.Path);
        Assert.Empty(Missiles(f).List(npc.SystemId));
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        Assert.Throws<InvalidDataException>(() => Launch(f, launcher, npc.Id));
        f.Clock.Milliseconds += 15000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
        Launch(f, launcher, npc.Id);
        Missiles(f).Cancel(f.PilotId, f.ShipId);
        Assert.Empty(Missiles(f).List(npc.SystemId));
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(Shield, world.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingAdvancersResolveOneKillBountyAndWreckWithoutDuplicatePayment(bool resident)
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        using LiveWorld? live = resident ? new(new(f.Db.Path), f.Clock, _ => { }) : null;
        long first = Load(f, LightLauncher, LightMissile);
        long second = Load(f, LightLauncher, LightMissile);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            foreach (
                var attribute in new[]
                {
                    DogmaAttributeId.ShieldCapacity,
                    DogmaAttributeId.ArmorHitPoints,
                    DogmaAttributeId.StructureHitPoints,
                }
            )
            {
                double value = attribute == DogmaAttributeId.StructureHitPoints ? 1 : 0;
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (30380,{(int)attribute},{value})");
            }
        }
        var npc = Space(f);
        Launch(f, first, npc.Id);
        Launch(f, second, npc.Id);
        f.Clock.Milliseconds += 1000;
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => Missiles(f).Advance(npc.SystemId, f.Clock.Now))));
        var impact = Assert.Single(Missiles(f).TakeImpacts(f.PilotId));
        Assert.True(impact.Destroyed);
        var report = Assert.Single(f.Db.Services.GetRequiredService<IPilotInteractionHistory>().Combat(f.PilotId, 25, null));
        Assert.Equal(f.PilotId, report.FinalBlow.CharacterId);
        Assert.Equal(LightLauncher, report.FinalBlow.WeaponTypeId);
        Assert.Equal(601, report.FinalBlow.ShipTypeId);
        Assert.Equal(npc.Id, report.Victim.EntityId);
        Assert.Equal(impact.Applied, report.FinalBlow.Damage, 8);
        Assert.Equal(300000, impact.Reward!.AmountCents);
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
        using WorldDbContext world = new(f.Db.Path);
        Assert.Single(world.Set<NpcBounty>());
        var wreck = Assert.Single(world.Set<WorldWreck>());
        Assert.Equal(f.PilotId, wreck.OwnerId);
        Assert.Equal(300000, world.Set<PilotWallet>().Single(w => w.PilotId == f.PilotId).Cents);
        Assert.Equal(100, world.Set<OwnedItem>().Single(i => i.LocationId == wreck.Id).Quantity);
        Assert.Equal(4, world.Set<OwnedItem>().Where(i => i.TypeId == LightMissile && i.OwnerId == f.PilotId).Sum(i => i.Quantity));
    }

    [Theory]
    [InlineData("unlocked")]
    [InlineData("offline")]
    [InlineData("foreign")]
    [InlineData("docked")]
    [InlineData("range")]
    [InlineData("skill")]
    public void RejectedLaunchesNeverSpendRoundsOrReplaceTheOldDeadline(string reason)
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f, reason == "range" ? 100000 : 1000);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(launcher)!;
            if (reason == "offline")
            {
                item.Online = false;
            }
            if (reason == "foreign")
            {
                item.OwnerId++;
            }
            world.SaveChanges();
        }
        if (reason == "docked")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
        }
        if (reason == "skill")
        {
            f.Train((int)MissileSkill.LightMissiles, 0);
        }
        var locks = reason == "unlocked" ? new HashSet<long>() : new HashSet<long> { npc.Id };
        Assert.Throws<InvalidDataException>(() =>
            Missiles(f).Launch(f.PilotId, launcher, npc.Id, locks, new(SpacePosition.Zero, 295, new(0, 0, -1), 42), f.Clock.Now)
        );
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<WorldMissile>());
        Assert.Equal(3, saved.Set<OwnedItem>().Single(i => i.LocationId == launcher).Quantity);
        Assert.Equal(ModuleCooldownKind.Reload, saved.Set<ModuleCooldown>().Find(launcher)!.Kind);
    }

    [Fact]
    public void ProductionDispatchRetainsOneRepeatRingAndStreamsProjectilesToObservers()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f, 10000);
        using var scope = f.Db.Services.CreateScope();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create(p),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.IsType<object?[]>(targeting.CallWire(context, "dogmaIM", "AddTarget", [npc.Id]));
        f.Clock.Milliseconds += 5000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(npc.Id));
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, pilot.Position! + new SpacePosition(100, 0, 0), npc.SystemId);
        wing = f.Db.Store.FindPilot(1000002)!;
        using FlightSession observer = new(
            f.Db.Store,
            wing,
            f.Clock,
            f.Db.Services.GetRequiredService<SharedSpace>(),
            [],
            encounters: f.Db.Services.GetRequiredService<INpcEncounters>(),
            missiles: Missiles(f)
        );
        observer.Snapshot();
        context.Notifications.Clear();
        context.NotificationsBeforeFlight.Clear();
        var activation = scope.ServiceProvider.GetRequiredService<ModuleActivationHandler>();
        var controller = scope.ServiceProvider.GetRequiredService<MissileHandler>();
        long started = f.Clock.Milliseconds;
        object? Activate() => activation.CallWire(context, "dogmaIM", "Activate", [launcher, "useMissiles", npc.Id, 100L]);
        Assert.Null(Activate());
        Assert.Null(Activate());
        Assert.Single(Missiles(f).List(npc.SystemId));
        Assert.Empty(context.NotificationsBeforeFlight);
        Assert.Single(context.Notifications, n => n.Name == "OnGodmaShipEffect");
        observer.RefreshMissiles();
        long projectile = Assert.Single(observer.VisibleMissiles).Id;
        Assert.Contains(
            observer.Updates,
            u => u.Command is DestinyCommand.AddBalls add && add.Addition.Slims.OfType<MissileSlimItem>().Any()
        );
        f.Clock.Milliseconds = started + 3000;
        controller.Advance(context);
        var message = Assert.IsType<Dictionary<string, object?>>(Assert.Single(context.NotificationsBeforeFlight).Arguments[0]);
        Assert.Equal(103.75, Assert.IsType<double>(message["damage"]), 7);
        Assert.Equal(LightMissile, message["weapon"]);
        observer.RefreshMissiles();
        Assert.Empty(observer.VisibleMissiles);
        Assert.Contains(observer.Updates, u => u.Command is DestinyCommand.RemoveBall remove && remove.Id == projectile && remove.Explode);
        f.Clock.Milliseconds = started + 14400;
        controller.Advance(context);
        Assert.Single(context.Notifications, n => n.Name == "OnGodmaShipEffect");
        Assert.NotNull(controller.ActiveEffect(context.Pilot, launcher));
        Assert.Null(activation.CallWire(context, "dogmaIM", "Deactivate", [launcher, "useMissiles"]));
        f.Clock.Milliseconds = started + 17400;
        controller.Advance(context);
        f.Clock.Milliseconds = started + 28800;
        controller.Advance(context);
        Assert.Null(controller.ActiveEffect(context.Pilot, launcher));
        Assert.Equal(2, context.Notifications.Count(n => n.Name == "OnGodmaShipEffect"));
        Assert.Equal(1, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }

    [Fact]
    public void OperationalMissilesUseMixedTierBallisticDiminishingReturnsAtEmptyCapacitor()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        PassiveCatalogFixture.LoadTiers(f.CatalogPath, new HashSet<int> { 22291, 14684 });
        long launcher = Load(f, LightLauncher, LightMissile);
        f.Fit(22291);
        f.Fit(14684);
        var npc = Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var energy = world.Set<ShipEnergy>().Find(f.ShipId) ?? world.Set<ShipEnergy>().Add(new() { ShipId = f.ShipId }).Entity;
            energy.Charge = 0;
            energy.UpdatedAt = f.Clock.Now;
            world.SaveChanges();
        }
        var launched = Launch(f, launcher, npc.Id);
        const double SecondWeight = .8691199808003975;
        Assert.Equal(14400 * .89 * (1 - .105 * SecondWeight), launched.Launcher.CycleMilliseconds, 7);
        f.Clock.Milliseconds += 1000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Equal(103.75 * 1.13 * (1 + .1 * SecondWeight), Assert.Single(Missiles(f).TakeImpacts(f.PilotId)).Applied, 7);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(0, saved.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
    }

    [Fact]
    public void MotorAndWarheadSnapshotsRetainLaunchSkillsWhileFlightIsInProgress()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        f.Train((int)MissileSkill.LightMissiles, 3);
        var npc = Space(f, 10000);
        Launch(f, launcher, npc.Id);
        f.Train((int)MissileSkill.LightMissiles, 4);
        f.Train((int)MissileSkill.MissileProjection, 5);
        f.Clock.Milliseconds += 1000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Equal(3750f, Assert.Single(Missiles(f).List(npc.SystemId)).Flight.Drive.MaxVelocity);
        f.Clock.Milliseconds += 2000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Equal(83 * 1.15, Assert.Single(Missiles(f).TakeImpacts(f.PilotId)).Applied, 7);
    }

    [Fact]
    public void LostTargetsAndExpiredUnobservedFlightsCannotCauseRetrospectiveDamage()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f, 10000);
        Launch(f, launcher, npc.Id);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldNpc>().Find(npc.Id)!.DestroyedAt = f.Clock.Now;
            world.SaveChanges();
        }
        f.Clock.Milliseconds += 1000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.True(Assert.Single(Missiles(f).List(npc.SystemId)).Flight.Orphaned);
        f.Clock.Milliseconds += 10000;
        Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        Assert.Empty(Missiles(f).List(npc.SystemId));
        Assert.Empty(Missiles(f).TakeImpacts(f.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(Shield, saved.Set<WorldNpc>().Find(npc.Id)!.ShieldCharge);
        Assert.Empty(saved.Set<NpcBounty>());
    }

    [Fact]
    public void VersionThirtyThreeUpgradePreservesTheFitMagazineAndDeadline()
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        var before = Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(TestWorld.DropSafetySchema + "DROP TABLE WorldMissile; PRAGMA user_version=33");
        }
        WorldInitialization.Open(f.Db.Path);
        Assert.Equal(before, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)));
        var npc = Space(f);
        Assert.True(Launch(f, launcher, npc.Id).Projectile.Id >= MissileDefinitions.FirstProjectileId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("warp")]
    [InlineData("loss")]
    [InlineData("docked-store")]
    public void LeavingTheSourceFlightEndsPendingProjectilesWithoutRefundingAmmunition(string transition)
    {
        using Fixture f = Prepare(LightLauncher, LightMissile);
        long launcher = Load(f, LightLauncher, LightMissile);
        var npc = Space(f, 10000);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        const long Destination = 40009114;
        StaticSpaceObject planet = new(Destination, 11, 7, 2, 1, npc.Position + new SpacePosition(1e9, 0, 0), 1e6f);
        using FlightSession flight = new(f.Db.Store, pilot, f.Clock, new(), [planet], missiles: Missiles(f));
        flight.Snapshot();
        Launch(f, launcher, npc.Id);
        if (transition == "disconnect")
        {
            flight.Dispose();
        }
        else if (transition == "loss")
        {
            flight.BeginShipLoss();
        }
        else if (transition == "docked-store")
        {
            f.Db.Store.SetPosition(f.ShipId, null);
            f.Clock.Milliseconds += 1000;
            Missiles(f).Advance(npc.SystemId, f.Clock.Now);
        }
        else
        {
            Assert.Same(PilotCallResult.None, flight.Execute(new FlightCommand.WarpTo(Destination, 0)));
        }
        Assert.Empty(Missiles(f).List(npc.SystemId));
        Assert.Equal(2, Assert.Single(Ammo(f).Read(f.PilotId, f.ShipId)).Quantity);
    }
}
