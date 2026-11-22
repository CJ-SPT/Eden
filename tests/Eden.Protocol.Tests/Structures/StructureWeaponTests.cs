using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Drones;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Structures;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureWeaponTests
{
    private const long TargetId = 59000001;
    private const int WarriorII = 2488;

    [Fact]
    public async Task PaidMissilesSurviveControlReleaseAndReportPhysicalDamageOnce()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        var control = f.Db.Services.GetRequiredService<IPilotStructureControl>();
        using var lease = control.Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, launcher, TargetId, 0, 1));
        Lock(f, targeting, lease.Token);
        var first = weapons.Activate(lease.Token, launcher, TargetId, 4, 1);
        Assert.NotNull(first.Launch);
        Assert.Equal(structure.Id, first.Launch.Projectile.SourceShipId);
        Assert.Equal(49, first.Launch.Magazine.Quantity);
        Assert.Equal(3000, first.Cycle.DurationMilliseconds);
        Assert.Null(weapons.Activate(lease.Token, launcher, TargetId, 4, 1).Launch);
        using (var metrics = SessionPerformance.Begin("warm structure firing", _ => { }, force: true)!)
        {
            Assert.Empty(weapons.Advance(lease.Token, 1).Started);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        lease.Dispose();
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, launcher, TargetId, 0, 2));
        using var replacement = control.Acquire(structure.Id, f.PilotId, 102);
        Assert.Empty(weapons.Read(replacement.Token));
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        missiles.Cancel(f.PilotId, f.ShipId);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var row = saved.Set<WorldMissile>().Find(first.Launch.Projectile.Id)!;
            Assert.Equal(MissileSourceKind.Structure, row.SourceKind);
            Assert.Equal(structure.Profile.TypeId, row.SourceTypeId);
            Assert.Equal(MissileFlightStatus.Flying, row.Status);
            Assert.Equal(first.Cycle.ReadyAt, saved.Set<ModuleCooldown>().Find(launcher)!.ReadyAt);
        }
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-weapon-reopen.sqlite");
        File.Copy(f.Db.Path, copy);
        var resumedClock = new TestClock(f.Clock.UtcNow);
        using (var reopened = ServerServices.Build(new(copy), resumedClock, population: new TestNpcPopulationSeeder(), liveWorld: true))
        {
            var resumed = reopened.GetRequiredService<IPilotMissiles>();
            Assert.Null(reopened.GetRequiredService<IPilotStructureControl>().Pilot(structure.Id));
            List<MissileImpact> reports = [];
            for (int index = 0; index < 120 && reports.Count == 0; index++)
            {
                resumedClock.AdvanceWallTime(TimeSpan.FromSeconds(1));
                resumed.Advance(Fixture.SystemId, resumedClock.Now);
                reports.AddRange(resumed.TakeImpacts(f.PilotId));
            }
            Assert.True(Assert.Single(reports).Applied > 0);
            Assert.Empty(resumed.TakeImpacts(f.PilotId));
        }
        List<MissileImpact> impacts = [];
        for (int index = 0; index < 120 && impacts.Count == 0; index++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        var hit = Assert.Single(impacts);
        Assert.Equal(structure.Id, hit.SourceShipId);
        Assert.True(hit.Applied > 0);
        Assert.False(hit.Destroyed);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        Assert.Equal(
            49,
            Assert.Single(f.Db.Services.GetRequiredService<IPilotStructureAmmunition>().Read(f.PilotId, structure.Id)).Quantity
        );
        missiles.Advance(Fixture.SystemId, f.Clock.Now);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
    }

    [Fact]
    public void LateRepeatsPayOneCycleAndStopWithoutRefundingThePaidProjectile()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, f.Db.Services.GetRequiredService<IPilotStructureTargeting>(), lease.Token);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        var first = weapons.Activate(lease.Token, launcher, TargetId, 5, 1);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(30));
        var next = Assert.Single(weapons.Advance(lease.Token, 2).Started);
        Assert.Equal(f.Clock.Now, next.Cycle.StartedAt);
        Assert.Equal(4, next.Cycle.Repeats);
        Assert.Equal(48, next.Launch!.Magazine.Quantity);
        Assert.Empty(weapons.Advance(lease.Token, 2).Started);
        weapons.Deactivate(lease.Token, launcher);
        Assert.True(Assert.Single(weapons.Read(lease.Token)).Stopping);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(3000));
        var stopped = weapons.Advance(lease.Token, 3);
        Assert.Empty(stopped.Started);
        Assert.Single(stopped.Stopped);
        Assert.Empty(weapons.Read(lease.Token));
        using var action = StructureAttackTests.Scope(f);
        Assert.Equal(2, action.World.Set<WorldMissile>().Count(row => row.SourceShipId == structure.Id));
        Assert.Equal(48, action.World.Set<OwnedItem>().Single(item => item.LocationId == launcher).Quantity);
        Assert.Equal(first.Cycle.ReadyAt + 30 * TimeSpan.TicksPerSecond, next.Cycle.ReadyAt);
    }

    [Fact]
    public async Task NativeActivationPrimesEffectsAndKeepsPersonalSafetySeparate()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        var safety = f.Db.Services.GetRequiredService<IPilotSafety>();
        safety.Set(f.PilotId, ShipSafetyLevel.Disabled);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var scope = f.Db.Services.CreateScope();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            scope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            101,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        Success("structureControl", "TakeControl", [structure.Id]);
        var state = Assert.IsType<CrimewatchClientView>(Success("crimewatch", "GetClientStates", []).Reply);
        Assert.Equal(ShipSafetyLevel.Full, state.Safety);
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "crimewatch", "SetSafetyLevel", new object?[] { 0L }, _ => { }));
        Success("crimewatch", "SetSafetyLevel", [2L]);
        Assert.Equal(ShipSafetyLevel.Disabled, safety.Read(f.PilotId));
        Lock(f, f.Db.Services.GetRequiredService<IPilotStructureTargeting>(), context.StructureControl!.Token);
        Success("dogmaIM", "Activate", [launcher, "useMissiles", TargetId, 2L]);
        var active = Assert.Single(
            context.Notifications.OfType<ShipEffectChanged>(),
            effect => effect.Effect.Effect == Eden.Gameplay.Inventory.DogmaEffect.UseMissiles
        );
        Assert.True(active.Active);
        Assert.Equal(structure.Id, active.Effect.ShipId);
        Assert.Equal(TargetId, active.Effect.TargetId);
        var info = Assert.IsType<DogmaAllInfo>(Success("dogmaIM", "GetAllInfo", [true, true, true]).Reply);
        Assert.Contains(Eden.Gameplay.Inventory.DogmaEffect.UseMissiles, info.StructureInfo![launcher].ActiveEffects.Keys);
        var contact = f
            .Db.Services.GetRequiredService<IStructureContacts>()
            .Contacts(Fixture.SystemId)
            .Single(body => body.Id == structure.Id);
        Assert.Equal(launcher, Assert.Single(contact.Structure!.Equipment).ItemId);
        var fields = BlueRead.Fields(
            Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new StaticSlimItem(contact)))).State
        );
        Assert.Single(Assert.IsType<List<object?>>(fields["modules"]));
        PilotSessionTick tick = new(
            f.Clock,
            scope.ServiceProvider.GetRequiredService<PilotUpdateCoordinator>(),
            scope.ServiceProvider.GetRequiredService<SkillHandler>(),
            f.Db.Services.GetRequiredService<IAsteroidField>(),
            structures: f.Db.Services.GetRequiredService<IStructureContacts>()
        );
        PilotSessionOutput output = new(context, 1000001, 101, _ => Task.CompletedTask, _ => { });
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(3000));
        await tick.AdvanceAsync(context, output, _ => { }, 1000001, 101);
        Assert.Equal(
            48,
            Assert.Single(f.Db.Services.GetRequiredService<IPilotStructureAmmunition>().Read(f.PilotId, structure.Id)).Quantity
        );
        await tick.AdvanceAsync(context, output, _ => { }, 1000001, 101);
        Assert.Equal(
            48,
            Assert.Single(f.Db.Services.GetRequiredService<IPilotStructureAmmunition>().Read(f.PilotId, structure.Id)).Quantity
        );
        Success("structureControl", "ReleaseControl", []);
        scope.ServiceProvider.GetRequiredService<StructureWeaponHandler>().Advance(context);
        Assert.Contains(
            context.Notifications.OfType<ShipEffectChanged>(),
            effect => !effect.Active && effect.Effect.ShipId == structure.Id
        );
        Assert.Equal(
            ShipSafetyLevel.Disabled,
            Assert.IsType<CrimewatchClientView>(Success("crimewatch", "GetClientStates", []).Reply).Safety
        );

        PilotCallResult.Success Success(string service, string method, object?[] args)
        {
            var result = router.Call(context, service, method, args, _ => { });
            Assert.True(result is PilotCallResult.Success, $"{service}.{method}: {result}");
            return (PilotCallResult.Success)result;
        }
    }

    [Fact]
    public void ConcordRejectionPreservesAmmoAndCooldownAndOnlyStructureSkillChangesWarhead()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, f.Db.Services.GetRequiredService<IPilotStructureTargeting>(), lease.Token);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Set<WorldNpc>().Find(TargetId)!.Role = NpcRole.Concord;
            action.Commit();
        }
        long priorDeadline;
        using (var read = StructureAttackTests.Scope(f))
        {
            priorDeadline = read.World.Set<ModuleCooldown>().Find(launcher)!.ReadyAt;
        }
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, launcher, TargetId, 0, 1));
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Empty(action.World.Set<WorldMissile>());
            Assert.Equal(50, action.World.Set<OwnedItem>().Single(item => item.LocationId == launcher).Quantity);
            Assert.Equal(priorDeadline, action.World.Set<ModuleCooldown>().Find(launcher)!.ReadyAt);
            action.World.Set<WorldNpc>().Find(TargetId)!.Role = NpcRole.Pirate;
            action.Commit();
        }
        var first = weapons.Activate(lease.Token, launcher, TargetId, 0, 1);
        double basis;
        using (var read = StructureAttackTests.Scope(f))
        {
            basis = read.World.Set<WorldMissile>().Find(first.Launch!.Projectile.Id)!.Payload().Warhead.Damage.Total;
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(3000));
        weapons.Advance(lease.Token, 2);
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = StructureWeaponRules.MissileSystemsSkill,
                    Points = 256000000,
                }
            );
            action.Commit();
        }
        var second = weapons.Activate(lease.Token, launcher, TargetId, 0, 2);
        using var current = StructureAttackTests.Scope(f);
        double improved = current.World.Set<WorldMissile>().Find(second.Launch!.Projectile.Id)!.Payload().Warhead.Damage.Total;
        Assert.Equal(basis * 1.1, improved, 8);
    }

    [Fact]
    public void PlayerRetaliationBelongsToTheStructureAndAllowsHighSecurityReturnFire()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        var other = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(
            other.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 10000, 0, 0),
            Fixture.SystemId
        );
        other = f.Db.Store.FindPilot(1000002)!;
        using var peerScope = f.Db.Services.CreateScope();
        using PilotCallContext peer = new(
            1000002,
            other.Account,
            [other],
            peerScope.ServiceProvider.GetRequiredService<FlightSessionFactory>().Create,
            102,
            SignedSettings.Empty
        );
        peer.Select(other);
        peer.Flight.Snapshot();
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targets = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        var scan = Assert.Single(targets.Add(lease.Token, other.ShipId).Targets);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(scan.ReadyAt - f.Clock.Now));
        targets.RequireLocked(lease.Token, other.ShipId);
        using (var action = StructureAttackTests.Scope(f))
        {
            action.World.Add(
                new SavedEngagement
                {
                    LowerPilotId = f.PilotId,
                    HigherPilotId = other.PilotId,
                    ExpiresAt = f.Clock.Now + TimeSpan.TicksPerMinute,
                }
            );
            action.Commit();
        }
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, launcher, other.ShipId, 0, 1));
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Empty(action.World.Set<WorldMissile>());
            Assert.Equal(50, action.World.Set<OwnedItem>().Single(item => item.LocationId == launcher).Quantity);
            StructureCombatTargets.Record(action.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            action.Commit();
        }
        var shot = weapons.Activate(lease.Token, launcher, other.ShipId, 0, 1);
        Assert.NotNull(shot.Launch);
        using (CatalogDbContext edit = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string high = "{\"security\":0.8}";
            const string system = "{\"securityStatus\":0.8}";
            edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{high})");
            edit.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{system})");
        }
        using (var action = StructureAttackTests.Scope(f))
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            var source = action.World.Set<SavedStructure>().Find(structure.Id)!;
            StructureCombatTargets.Check(action.World, catalog, source, f.Clock.Now, other.PilotId);
            Assert.Throws<InvalidDataException>(() => StructureCombatTargets.Check(action.World, catalog, source, f.Clock.Now, f.PilotId));
        }
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        List<MissileImpact> impacts = [];
        for (int index = 0; index < 120 && impacts.Count == 0; index++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        Assert.True(Assert.Single(impacts).Applied > 0);
        Assert.Equal(other.ShipId, impacts[0].Health.ShipId);
        Assert.Equal(structure.Id, impacts[0].SourceShipId);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(16));
        using var expired = StructureAttackTests.Scope(f);
        using CatalogDbContext inputs = new(new WorldDatabase(f.Db.Path).CatalogPath);
        Assert.Throws<InvalidDataException>(() =>
            StructureCombatTargets.Check(
                expired.World,
                inputs,
                expired.World.Set<SavedStructure>().Find(structure.Id)!,
                f.Clock.Now,
                other.PilotId
            )
        );
    }

    [Fact]
    public void StructureTargetsSettleThroughTheSharedPhysicalBattleBoundary()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        long targetId;
        using (var action = StructureAttackTests.Scope(f))
        {
            targetId = InventoryPersistence.NextId(action.World);
            action.World.Add(
                new SavedStructure
                {
                    Id = targetId,
                    TypeId = structure.Profile.TypeId,
                    Name = "Target",
                    CorporationId = f.CorporationId,
                    SystemId = Fixture.SystemId,
                    State = StructureState.ShieldVulnerable,
                    HasCore = true,
                    X = structure.Position.X + 50000,
                    StateStartedAt = f.Clock.Now,
                    DeployedAt = f.Clock.Now,
                }
            );
            // A structure's own outgoing attack grants this exact defender retaliation without a private pilot engagement.
            StructureCombatTargets.Record(action.World, targetId, structure.Id, f.Clock.Now, f.Clock.Now);
            action.Commit();
        }
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targets = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        var scan = Assert.Single(targets.Add(lease.Token, targetId).Targets);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(scan.ReadyAt - f.Clock.Now));
        var shot = f.Db.Services.GetRequiredService<IPilotStructureWeapons>().Activate(lease.Token, launcher, targetId, 0, 1);
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        List<MissileImpact> impacts = [];
        for (int index = 0; index < 120 && impacts.Count == 0; index++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            // Reads settle physical contacts before structure repair and reinforcement timers.
            _ = f.Structures.Read(f.PilotId, targetId);
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        Assert.True(Assert.Single(impacts).Applied > 0);
        Assert.Equal(shot.Launch!.Projectile.Id, impacts[0].ProjectileId);
        Assert.True(f.Structures.Read(f.PilotId, targetId)!.Battle!.Health.Shield < structure.Profile.Shield);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructureMissilesAgainstDronesRespectOwnerRightsAndPersistDamage(bool fragile)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        var other = f.Db.Store.FindPilot(1000002)!;
        long id = LaunchDrone(f, structure, other);
        long secondLauncher = 0;
        if (fragile)
        {
            secondLauncher = f.Item(35922);
            f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(secondLauncher)]);
            var ammo = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
            var loaded = ammo.Load(f.PilotId, structure.Id, [secondLauncher], [f.Item(37846, 50)], f.ShipId);
            f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(loaded.ReloadMilliseconds + 1));
        }
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        Lock(f, targeting, lease.Token, id);
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        Assert.Throws<InvalidDataException>(() => weapons.Activate(lease.Token, launcher, id, 0, 1));
        var defense = Assert.Single(f.Db.Services.GetRequiredService<IPilotDrones>().List(Fixture.SystemId, f.Clock.Now)).Profile.Defense;
        using (var action = StructureAttackTests.Scope(f))
        {
            Assert.Equal(50, action.World.Set<OwnedItem>().Single(item => item.LocationId == launcher).Quantity);
            Assert.Empty(action.World.Set<WorldMissile>());
            StructureCombatTargets.Record(action.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            if (fragile)
            {
                var drone = action.World.Set<WorldDrone>().Find(id)!;
                drone.Shield = 0;
                drone.ArmorDamage = defense.Armor;
                drone.StructureDamage = defense.Structure - 0.01;
            }
            action.Commit();
        }
        var paid = weapons.Activate(lease.Token, launcher, id, 0, 1);
        Assert.Equal(49, paid.Launch!.Magazine.Quantity);
        long? secondProjectile = fragile ? weapons.Activate(lease.Token, secondLauncher, id, 0, 1).Launch!.Projectile.Id : null;
        using (var read = StructureAttackTests.Scope(f))
        {
            Assert.True(StructureCombatTargets.Hostile(read.World, structure.Id, other.ShipId, f.Clock.Now));
            Assert.Equal(MissileTargetKind.Drone, read.World.Set<WorldMissile>().Find(paid.Launch.Projectile.Id)!.TargetKind);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-drone-flight.sqlite");
        File.Copy(f.Db.Path, copy);
        var resumedClock = new TestClock(f.Clock.UtcNow);
        using var reopened = ServerServices.Build(new(copy), resumedClock, population: new TestNpcPopulationSeeder(), liveWorld: true);
        var missiles = reopened.GetRequiredService<IPilotMissiles>();
        var resumedDrones = reopened.GetRequiredService<IPilotDrones>();
        var before = Assert.Single(resumedDrones.List(Fixture.SystemId, resumedClock.Now));
        List<MissileImpact> hits = [];
        for (int index = 0; index < 120 && hits.Count == 0; index++)
        {
            resumedClock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            missiles.Advance(Fixture.SystemId, resumedClock.Now);
            hits.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        var hit = Assert.Single(hits);
        Assert.Equal(id, hit.TargetId);
        Assert.Equal(structure.Id, hit.SourceShipId);
        Assert.Equal(fragile, hit.Destroyed);
        Assert.True(hit.Applied > 0);
        Assert.Null(hit.Reward);
        Assert.Equal(fragile, resumedDrones.WasDestroyed(id));
        var contacts = resumedDrones.List(Fixture.SystemId, resumedClock.Now);
        if (fragile)
        {
            Assert.Empty(contacts);
            var survivingFlight = Assert.Single(missiles.List(Fixture.SystemId));
            Assert.Equal(secondProjectile, survivingFlight.Id);
            Assert.True(survivingFlight.Flight.Orphaned);
        }
        else
        {
            Assert.True(hit.Health.Remaining.Shield < before.Health.Remaining.Shield);
            Assert.Equal(hit.Health.Remaining, Assert.Single(contacts).Health.Remaining);
        }
        missiles.Advance(Fixture.SystemId, resumedClock.Now);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StowingAndRelaunchingADroneBreaksTheLockAndPaidMissile(bool scoop)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        var other = f.Db.Store.FindPilot(1000002)!;
        long id = LaunchDrone(f, structure, other);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        var targeting = f.Db.Services.GetRequiredService<IPilotStructureTargeting>();
        Lock(f, targeting, lease.Token, id);
        using (var action = StructureAttackTests.Scope(f))
        {
            StructureCombatTargets.Record(action.World, other.ShipId, structure.Id, f.Clock.Now, f.Clock.Now);
            action.Commit();
        }
        var weapons = f.Db.Services.GetRequiredService<IPilotStructureWeapons>();
        var paid = weapons.Activate(lease.Token, launcher, id, 1, 1);
        var drones = f.Db.Services.GetRequiredService<IPilotDrones>();
        if (scoop)
        {
            drones.Order(other.PilotId, [id], DroneOrder.Abandon, null, new HashSet<long>(), f.Clock.Now);
            Assert.Single(drones.Scoop(other.PilotId, [id], InventoryFlag.DroneBay, f.Clock.Now));
        }
        else
        {
            drones.Order(other.PilotId, [id], DroneOrder.ReturnToBay, null, new HashSet<long>(), f.Clock.Now);
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            Assert.Single(drones.Advance(other.PilotId, f.Clock.Now, 2).Changes);
        }
        var relaunched = drones.Launch(other.PilotId, [new(id, 1)], f.Clock.Now);
        Assert.Equal(id, Assert.Single(Assert.Single(relaunched.Launches).DroneIds));
        Assert.Empty(targeting.Read(lease.Token).Targets);
        Assert.Single(weapons.Advance(lease.Token, 3).Stopped);
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        for (int step = 0; step < 120 && missiles.List(Fixture.SystemId).Count > 0; step++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
        }
        Assert.Empty(missiles.List(Fixture.SystemId));
        Assert.False(missiles.Impacted(paid.Launch!.Projectile.Id));
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        var contact = Assert.Single(drones.List(Fixture.SystemId, f.Clock.Now));
        Assert.Equal(contact.Profile.Defense.Undamaged, contact.Health.Remaining);
    }

    private static long LaunchDrone(Fixture f, StructureSnapshot structure, DockedPilot owner)
    {
        PassiveCatalogFixture.LoadCombatDrones(new WorldDatabase(f.Db.Path).CatalogPath);
        f.Db.Store.SetPosition(
            owner.ShipId,
            structure.Position + new SpacePosition(structure.Profile.Radius + 10000, 0, 0),
            Fixture.SystemId
        );
        long stack = f.Item(WarriorII);
        using (var action = StructureAttackTests.Scope(f))
        {
            var item = action.World.Set<OwnedItem>().Find(stack)!;
            item.OwnerId = owner.PilotId;
            item.LocationId = owner.ShipId;
            item.FlagId = (int)InventoryFlag.DroneBay;
            foreach (int type in Enum.GetValues<DroneSkill>().Select(skill => (int)skill))
            {
                var skill =
                    action.World.Set<PilotSkill>().Find(owner.PilotId, type)
                    ?? action.World.Add(new PilotSkill { PilotId = owner.PilotId, TypeId = type }).Entity;
                skill.Points = 256_000_000;
            }
            action.Commit();
        }
        var drones = f.Db.Services.GetRequiredService<IPilotDrones>();
        return Assert.Single(Assert.Single(drones.Launch(owner.PilotId, [new(stack, 1)], f.Clock.Now).Launches).DroneIds);
    }

    [Fact]
    public void FatalNpcImpactKeepsStructureAttributionAndPaysOneBounty()
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f);
        Eden.Protocol.Tests.Combat.NpcEncounterTests.Prepare(f.Db);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            foreach (int attribute in new[] { 9, 263, 265 })
            {
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (30380,{attribute},1)");
            }
            catalog.Database.ExecuteSqlRaw("INSERT OR IGNORE INTO DogmaAttribute VALUES (481,'entityKillBounty')");
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (30380,481,1000)");
        }
        using (var action = StructureAttackTests.Scope(f))
        {
            var npc = action.World.Set<WorldNpc>().Find(TargetId)!;
            npc.TypeId = 30380;
            npc.ShieldCharge = 1;
            action.Commit();
        }
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, f.Db.Services.GetRequiredService<IPilotStructureTargeting>(), lease.Token);
        var launch = f.Db.Services.GetRequiredService<IPilotStructureWeapons>().Activate(lease.Token, launcher, TargetId, 0, 1);
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        List<MissileImpact> impacts = [];
        for (int index = 0; index < 120 && impacts.Count == 0; index++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        var hit = Assert.Single(impacts);
        Assert.True(hit.Destroyed);
        Assert.Null(hit.Reward);
        var report = Assert.Single(f.Db.Services.GetRequiredService<IPilotInteractionHistory>().Combat(f.PilotId, 10, null));
        Assert.Equal(structure.Id, report.FinalBlow.EntityId);
        Assert.Equal(structure.Profile.TypeId, report.FinalBlow.ShipTypeId);
        Assert.Equal(f.CorporationId, report.FinalBlow.CorporationId);
        Assert.Equal(f.PilotId, report.FinalBlow.CharacterId);
        Assert.Equal(launcher, launch.Cycle.ModuleId);
        missiles.Advance(Fixture.SystemId, f.Clock.Now);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(ActivityRewardTickRules.DefaultIntervalSeconds));
        f.Db.Services.GetRequiredService<IActivityRewardTicks>().Advance();
        using var read = StructureAttackTests.Scope(f);
        Assert.True(Assert.Single(read.World.Set<NpcBounty>()).AmountCents > 0);
    }

    [Theory]
    [InlineData(35921, 37843, 15000)]
    [InlineData(47323, 37843, 12000)]
    [InlineData(35922, 37846, 3000)]
    [InlineData(47298, 37846, 2400)]
    public void PublishedLauncherVariantsUsePinnedCyclesAndOnePaidRound(int type, int charge, double milliseconds)
    {
        using var f = StructureAttackTests.Prepare();
        var (structure, launcher) = Prepare(f, type, charge);
        var contacts = f.Db.Services.GetRequiredService<IStructureContacts>();
        var original = contacts.Contacts(Fixture.SystemId);
        var fitting = f.Db.Services.GetRequiredService<IPilotStructureFitting>();
        fitting.SetOnline(f.PilotId, structure.Id, launcher, false);
        Assert.False(
            Assert.Single(contacts.Contacts(Fixture.SystemId).Single(body => body.Id == structure.Id).Structure!.Equipment).Online
        );
        fitting.SetOnline(f.PilotId, structure.Id, launcher, true);
        var current = contacts.Contacts(Fixture.SystemId);
        Assert.NotSame(original, current);
        Assert.True(Assert.Single(current.Single(body => body.Id == structure.Id).Structure!.Equipment).Online);
        using var lease = f.Db.Services.GetRequiredService<IPilotStructureControl>().Acquire(structure.Id, f.PilotId, 101);
        Lock(f, f.Db.Services.GetRequiredService<IPilotStructureTargeting>(), lease.Token);
        var ammunition = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        int quantity = Assert.Single(ammunition.Read(f.PilotId, structure.Id)).Quantity;
        var launch = f.Db.Services.GetRequiredService<IPilotStructureWeapons>().Activate(lease.Token, launcher, TargetId, 0, 1);
        Assert.Equal(milliseconds, launch.Cycle.DurationMilliseconds);
        Assert.Equal(quantity - 1, launch.Launch!.Magazine.Quantity);
        using var read = StructureAttackTests.Scope(f);
        var payload = read.World.Set<WorldMissile>().Find(launch.Launch.Projectile.Id)!.Payload();
        Assert.Equal(structure.Id, payload.Source!.EntityId);
        Assert.Equal(structure.Profile.TypeId, payload.Source.ShipTypeId);
        Assert.Equal(f.CorporationId, payload.Source.CorporationId);
        Assert.True(payload.Warhead.Damage.Total > 0);
    }

    [Theory]
    [InlineData(0, CrimewatchFlag.None, false, false, true)]
    [InlineData(.4, CrimewatchFlag.None, false, false, false)]
    [InlineData(.8, CrimewatchFlag.None, false, false, false)]
    [InlineData(.8, CrimewatchFlag.Suspect, false, false, true)]
    [InlineData(.8, CrimewatchFlag.None, true, false, true)]
    [InlineData(.8, CrimewatchFlag.None, false, true, true)]
    public void StructureGreenSafetyRequiresActualStructureRights(
        double security,
        CrimewatchFlag flag,
        bool outlaw,
        bool retaliation,
        bool allowed
    )
    {
        if (allowed)
        {
            StructureWeaponRules.CheckPlayer(security, flag, outlaw, retaliation);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => StructureWeaponRules.CheckPlayer(security, flag, outlaw, retaliation));
        }
    }

    private static void Lock(Fixture f, IPilotStructureTargeting targeting, StructureControlToken token, long target = TargetId)
    {
        var scan = Assert.Single(targeting.Add(token, target).Targets);
        if (scan.ReadyAt > f.Clock.Now)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromTicks(scan.ReadyAt - f.Clock.Now));
        }
        targeting.RequireLocked(token, target);
    }

    private static (StructureSnapshot Structure, long Launcher) Prepare(Fixture f, int launcherType = 35922, int chargeType = 37846)
    {
        var structure = f.Online();
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        long launcher = f.Item(launcherType);
        f.Db.Services.GetRequiredService<IPilotStructureFitting>().Fit(f.PilotId, structure.Id, [new(launcher)]);
        var ammo = f.Db.Services.GetRequiredService<IPilotStructureAmmunition>();
        var loaded = ammo.Load(f.PilotId, structure.Id, [launcher], [f.Item(chargeType, 100)], f.ShipId);
        f.Clock.AdvanceWallTime(TimeSpan.FromMilliseconds(loaded.ReloadMilliseconds + 1));
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            foreach (int attribute in new[] { 9, 263, 265 })
            {
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{attribute},1000000)");
            }
            catalog.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO TypeAttribute VALUES (601,552,400)");
        }
        using var action = StructureAttackTests.Scope(f);
        action.World.Add(
            new WorldNpc
            {
                Id = TargetId,
                TypeId = 601,
                SystemId = Fixture.SystemId,
                X = structure.Position.X + structure.Profile.Radius + 1000,
                HomeX = structure.Position.X + structure.Profile.Radius + 1000,
                HomeY = 0,
                HomeZ = 0,
                ShieldCharge = 1000000,
                MotionAt = f.Clock.Now,
                UpdatedAt = f.Clock.Now,
            }
        );
        action.Commit();
        return (structure, launcher);
    }
}
