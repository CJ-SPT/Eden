using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Combat;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Structures;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureAttackTests
{
    [Fact]
    public void PaidTurretDamagesTheStructureAndRejectedReinforcementCannotPayAgain()
    {
        using Fixture f = Prepare();
        using ResidentFitCache cache = new(new(f.Db.Path));
        var structure = f.Online();
        long module = Gun(f);
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var shot = combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.True(shot.Applied > 0);
        Assert.Equal(structure.Id, shot.Health.ShipId);
        Assert.Equal(shot.Health.Remaining, f.Structures.Read(f.PilotId, structure.Id)!.Battle!.Health);
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        using (var metrics = SessionPerformance.Begin("warm structure volley", _ => { }, force: true)!)
        {
            Assert.True(combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5).Applied > 0);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using (WorldActionScope action = Scope(f))
        {
            Assert.True(action.World.Set<ModuleCooldown>().Find(module)!.ReadyAt > f.Clock.Now);
            Assert.True(action.World.Set<SavedCrimewatch>().Find(f.PilotId)!.WeaponsUntil > f.Clock.Now);
            var target = action.World.Set<SavedStructure>().Find(structure.Id)!;
            target.State = StructureState.ArmorReinforced;
            target.StateEndsAt = f.Clock.Now + TimeSpan.TicksPerDay;
            action.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidDataException>(() => combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5));
        using WorldActionScope check = Scope(f);
        Assert.True(check.World.Set<ModuleCooldown>().Find(module)!.ReadyAt < f.Clock.Now);
    }

    [Fact]
    public async Task FatalPaidShotSettlesPersonalAndCorporationAssetsEvacuatesBoardedShipsAndDropsTheCoreOnce()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long module = Gun(f);
        long personal = f.Item(34, 20);
        long corporation = f.Item(35, 40);
        var visitor = f.Db.Store.FindPilot(1000002)!;
        f.Structures.SetAccess(f.PilotId, structure.Id, true, []);
        f.Db.Store.SetPosition(visitor.ShipId, Fixture.Point, Fixture.SystemId);
        f.Db.Services.GetRequiredService<Eden.Gameplay.Docking.IStationDocking>().Dock(visitor.PilotId, visitor.ShipId, structure.Id);
        using (WorldActionScope prepare = Scope(f))
        {
            prepare.World.Set<OwnedItem>().Find(personal)!.LocationId = structure.Id;
            var corpItem = prepare.World.Set<OwnedItem>().Find(corporation)!;
            corpItem.LocationId = structure.Id;
            corpItem.OwnerId = f.CorporationId;
            corpItem.FlagId = Eden.Gameplay.Corporations.CorporationStorageRules.FirstFlag;
            prepare.World.Add(new SavedStructureBattle { StructureId = structure.Id, Hull = 0.1 });
            prepare.World.Set<SavedStructure>().Find(structure.Id)!.State = StructureState.HullVulnerable;
            prepare.Commit();
        }
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var shot = combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.True(shot.Destroyed);
        Assert.Null(f.Structures.Read(f.PilotId, structure.Id));
        Assert.Empty(((IStructureContacts)f.Structures).Contacts(Fixture.SystemId));
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var personalWrap = Assert.Single(safety.List(f.PilotId, false));
        var corpWrap = Assert.Single(safety.List(f.PilotId, true));
        using (WorldActionScope check = Scope(f))
        {
            Assert.Equal(personalWrap.Id, check.World.Set<OwnedItem>().Find(personal)!.LocationId);
            Assert.Equal(corpWrap.Id, check.World.Set<OwnedItem>().Find(corporation)!.LocationId);
            var evacuated = check.World.Ships.Find(visitor.ShipId)!;
            Assert.Null(evacuated.StructureId);
            Assert.NotNull(evacuated.Position);
            Assert.Equal(Fixture.SystemId, evacuated.Position!.SystemId);
            var core = Assert.Single(check.World.Filter<OwnedItem>(i => i.TypeId == structure.Profile.CoreTypeId));
            Assert.NotNull(check.World.Set<SavedStructureLoot>().Find(core.LocationId));
            Assert.Equal((int)InventoryFlag.Cargo, core.FlagId);
            Assert.Empty(check.World.Filter<SavedStructureService>(i => i.StructureId == structure.Id));
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        Assert.Throws<InvalidDataException>(() => combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5));
        Assert.Single(safety.List(f.PilotId, false));
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(StructureState.Destroyed, saved.Set<SavedStructure>().Find(structure.Id)!.State);
        Assert.Equal(2, saved.Set<SavedAssetSafety>().Count());
        Assert.NotNull(saved.Positions.Find(visitor.ShipId));
    }

    [Fact]
    public void HighSecurityAttackRejectsBeforeWeaponCostsAndUnanchoringSettlesAtTheExactDeadline()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long module = Gun(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string high = "{\"security\":0.9}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{high})");
        }
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        Assert.Contains(
            "war",
            Assert
                .Throws<InvalidDataException>(() => combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5))
                .Message
        );
        using (WorldActionScope check = Scope(f))
        {
            Assert.Null(check.World.Set<ModuleCooldown>().Find(module));
            Assert.Null(check.World.Set<SavedStructureBattle>().Find(structure.Id));
        }
        long personal = f.Item(34, 20);
        using (WorldActionScope prepare = Scope(f))
        {
            prepare.World.Set<OwnedItem>().Find(personal)!.LocationId = structure.Id;
            prepare.Commit();
        }
        f.Structures.Unanchor(f.PilotId, structure.Id);
        long due = f.Structures.Read(f.PilotId, structure.Id)!.UnanchorAt!.Value;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(due - f.Clock.Now - 1));
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotAssetSafety>().List(f.PilotId, false));
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(StructureState.Unanchored, f.Structures.Read(f.PilotId, structure.Id)!.State);
        var wrap = Assert.Single(f.Db.Services.GetRequiredService<IPilotAssetSafety>().List(f.PilotId, false));
        Assert.Equal(due, wrap.CreatedAt);
        using WorldActionScope custody = Scope(f);
        Assert.Equal(wrap.Id, custody.World.Set<OwnedItem>().Find(personal)!.LocationId);
    }

    [Fact]
    public void ActiveWarLetsAHighSecurityTurretHitTheOpposingWarHeadquarters()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long module = Gun(f);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            const string high = "{\"security\":0.9}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{high})");
            const string system = "{\"securityStatus\":0.9}";
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{system})");
        }
        long opposingCeo = f.Db.Store.FindPilot(1000002)!.PilotId;
        using (var prepare = Scope(f))
        {
            const int opposing = 98000719;
            var owner = prepare.World.Set<SavedStructure>().Find(structure.Id)!.CorporationId;
            prepare.World.Add(
                new SavedCorporation
                {
                    Id = opposing,
                    Name = "Opposition",
                    Ticker = "OPP",
                    CeoId = opposingCeo,
                    CreatorId = opposingCeo,
                }
            );
            prepare.World.Pilots.Find(f.PilotId)!.CorporationId = opposing;
            prepare.World.Add(
                new SavedCorporationWar
                {
                    Id = 1,
                    AttackerId = owner,
                    DefenderId = opposing,
                    HeadquartersId = structure.Id,
                    DeclaredAt = f.Clock.Now - TimeSpan.TicksPerDay,
                    StartsAt = f.Clock.Now,
                    BillDueAt = f.Clock.Now + TimeSpan.TicksPerDay,
                }
            );
            prepare.World.Add(
                new SavedStructureBattle
                {
                    StructureId = structure.Id,
                    Shield = 0.1,
                    Armor = structure.Profile.Armor,
                    Hull = structure.Profile.Hull,
                }
            );
            prepare.Commit();
        }
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        var shot = combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5);
        Assert.True(shot.Applied > 0);
        using var read = Scope(f);
        var headquarters = read.World.Set<SavedStructure>().Find(structure.Id)!;
        Assert.Equal(StructureState.ArmorReinforced, headquarters.State);
        Assert.InRange(
            headquarters.StateEndsAt!.Value - f.Clock.Now,
            TimeSpan.TicksPerDay - StructureCombatRules.MediumExitJitter,
            2 * TimeSpan.TicksPerDay + StructureCombatRules.MediumExitJitter
        );
        Assert.Equal(CrimewatchFlag.None, read.World.Set<SavedCrimewatch>().Find(f.PilotId)!.Timers().Flag(f.Clock.Now));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task StructureMissilesPayAtLaunchPersistTheirTargetKindAndApplyOnlyAtContact(
        bool pendingRepair,
        bool subsecondRead,
        bool insideRangeBall
    )
    {
        using Fixture f = Prepare();
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        const int launcherType = 499;
        const int missileType = 212;
        PassiveCatalogFixture.LoadMissileTiers(path, MissileDefinitions.Skills.Append(launcherType).Append(missileType).ToHashSet());
        var structure = f.Online();
        f.Db.Store.SetPosition(
            f.ShipId,
            Fixture.Point + new SpacePosition(structure.Profile.Radius + (insideRangeBall ? -1000 : 1000), 0, 0),
            Fixture.SystemId
        );
        long launcher;
        long charge;
        using (WorldActionScope prepare = Scope(f))
        {
            launcher = InventoryPersistence.NextId(prepare.World);
            charge = InventoryPersistence.NextId(prepare.World);
            prepare.World.Add(
                new OwnedItem
                {
                    Id = launcher,
                    OwnerId = f.PilotId,
                    TypeId = launcherType,
                    LocationId = f.ShipId,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    Quantity = 1,
                    Singleton = true,
                    Online = true,
                }
            );
            prepare.World.Add(
                new OwnedItem
                {
                    Id = charge,
                    OwnerId = f.PilotId,
                    TypeId = missileType,
                    LocationId = launcher,
                    FlagId = (int)InventoryFlag.HighSlot0,
                    Quantity = 3,
                }
            );
            foreach (int type in MissileDefinitions.Skills)
            {
                var skill = prepare.World.Set<PilotSkill>().Find(f.PilotId, type);
                if (skill is null)
                {
                    prepare.World.Add(
                        new PilotSkill
                        {
                            PilotId = f.PilotId,
                            TypeId = type,
                            Points = 256_000_000,
                        }
                    );
                }
                else
                {
                    skill.Points = 256_000_000;
                }
            }
            prepare.Commit();
        }
        if (pendingRepair)
        {
            using CatalogDbContext tune = new(path, writable: true);
            const int shieldPauseThreshold = 3354;
            tune.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({structure.Profile.TypeId},{shieldPauseThreshold},1)");
        }
        var missiles = f.Db.Services.GetRequiredService<IPilotMissiles>();
        var launched = missiles.Launch(
            f.PilotId,
            launcher,
            structure.Id,
            new HashSet<long> { structure.Id },
            new(SpacePosition.Zero, 300, new(0, 0, -1), 1),
            f.Clock.Now
        );
        long contactAt = 0;
        if (pendingRepair)
        {
            using WorldActionScope seed = Scope(f);
            var flight = seed.World.Set<WorldMissile>().Find(launched.Projectile.Id)!.Payload();
            var predicted = MissileFlightRules.Tick(flight.Flight, flight.Target);
            Assert.Equal(MissileFlightStatus.Impact, predicted.Status);
            contactAt = f.Clock.Now + (long)Math.Round(predicted.ElapsedSeconds * TimeSpan.TicksPerSecond);
            if (insideRangeBall)
            {
                Assert.Equal(f.Clock.Now, contactAt);
            }
            using CatalogDbContext inputs = new(path);
            var profile = StructureBattlePersistence.Profile(inputs, structure.Profile.TypeId);
            seed.World.Add(
                new SavedStructureBattle
                {
                    StructureId = structure.Id,
                    Shield = profile.Defense.Shield - 100,
                    Armor = profile.Defense.Armor,
                    Hull = profile.Defense.Structure,
                    RepairAt = contactAt + TimeSpan.TicksPerMillisecond,
                }
            );
            seed.Commit();
        }
        else
        {
            Assert.Null(f.Structures.Read(f.PilotId, structure.Id)!.Battle!.RepairAt);
        }
        using (WorldActionScope read = Scope(f))
        {
            Assert.Equal(2, read.World.Set<OwnedItem>().Find(charge)!.Quantity);
            Assert.Equal(MissileTargetKind.Structure, read.World.Set<WorldMissile>().Find(launched.Projectile.Id)!.TargetKind);
        }
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(MissileTargetKind.Structure, saved.Set<WorldMissile>().Find(launched.Projectile.Id)!.TargetKind);
        }
        string copy = Path.Combine(Path.GetDirectoryName(f.Db.Path)!, "structure-flight.sqlite");
        File.Copy(f.Db.Path, copy);
        var reopenedClock = new TestClock(f.Clock.UtcNow.AddSeconds(1));
        using (LiveWorld reopened = new(new(copy), reopenedClock))
        using (WorldActionScope resume = new(new(copy), new(systems: [Fixture.SystemId])))
        using (CatalogDbContext inputs = new(path))
        {
            Assert.True(resume.World.HasStructureMissiles(Fixture.SystemId));
            StructureMissileSettlement.Advance(resume.World, inputs, Fixture.SystemId, reopenedClock.Now);
            var resumed = resume.World.Set<WorldMissile>().Find(launched.Projectile.Id)!;
            Assert.Equal(MissileFlightStatus.Impact, resumed.Status);
            Assert.NotNull(resumed.Outcome);
            resume.Commit();
        }
        List<MissileImpact> impacts = [];
        if (subsecondRead)
        {
            if (contactAt > f.Clock.Now)
            {
                f.Clock.AdvanceWallTime(TimeSpan.FromTicks(contactAt - f.Clock.Now - 1));
                Assert.Equal(contactAt + TimeSpan.TicksPerMillisecond, f.Structures.Read(f.PilotId, structure.Id)!.Battle!.RepairAt);
                Assert.Empty(missiles.TakeImpacts(f.PilotId));
                f.Clock.AdvanceWallTime(TimeSpan.FromTicks(2));
            }
            var observed = f.Structures.Read(f.PilotId, structure.Id)!;
            Assert.True(observed.Battle!.RepairAt > contactAt + TimeSpan.TicksPerSecond);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
            Assert.Single(impacts);
        }
        for (int step = 0; step < 10 && impacts.Count == 0; step++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            if (pendingRepair)
            {
                var observed = f.Structures.Read(f.PilotId, structure.Id)!;
                Assert.True(observed.DamageFractions!.Shield < 1);
                Assert.True(observed.Battle!.RepairAt > f.Clock.Now);
            }
            missiles.Advance(Fixture.SystemId, f.Clock.Now);
            impacts.AddRange(missiles.TakeImpacts(f.PilotId));
        }
        var impact = Assert.Single(impacts);
        Assert.True(impact.Applied > 0);
        Assert.Equal(structure.Id, impact.TargetId);
        Assert.Equal(impact.Health.Remaining, f.Structures.Read(f.PilotId, structure.Id)!.Battle!.Health);
        missiles.Advance(Fixture.SystemId, f.Clock.Now);
        Assert.Empty(missiles.TakeImpacts(f.PilotId));
    }

    [Fact]
    public void OrderedCombatDronesDamageStructuresAndStopWhenReinforcementBegins()
    {
        using Fixture f = Prepare();
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.LoadCombatDrones(path);
        var structure = f.Online();
        f.Db.Store.SetPosition(f.ShipId, Fixture.Point + new SpacePosition(structure.Profile.Radius + 1000, 0, 0), Fixture.SystemId);
        long stack = f.Item(2488);
        using (WorldActionScope prepare = Scope(f))
        {
            prepare.World.Set<OwnedItem>().Find(stack)!.FlagId = (int)InventoryFlag.DroneBay;
            foreach (int type in Enum.GetValues<DroneSkill>().Select(skill => (int)skill))
            {
                var skill = prepare.World.Set<PilotSkill>().Find(f.PilotId, type);
                if (skill is null)
                {
                    prepare.World.Add(
                        new PilotSkill
                        {
                            PilotId = f.PilotId,
                            TypeId = type,
                            Points = 256_000_000,
                        }
                    );
                }
                else
                {
                    skill.Points = 256_000_000;
                }
            }
            prepare.Commit();
        }
        var drones = f.Db.Services.GetRequiredService<IPilotDrones>();
        var launched = drones.Launch(f.PilotId, [new(stack, 1)], f.Clock.Now);
        var ids = Assert.Single(launched.Launches).DroneIds;
        drones.Order(f.PilotId, ids, DroneOrder.Engage, structure.Id, new HashSet<long> { structure.Id }, f.Clock.Now);
        List<DroneHit> hits = [];
        for (int step = 0; step < 30 && hits.Count == 0; step++)
        {
            f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
            hits.AddRange(drones.Advance(f.PilotId, f.Clock.Now, step + 1).Hits);
        }
        var hit = Assert.Single(hits);
        Assert.Equal(structure.Id, hit.TargetId);
        Assert.True(hit.Applied > 0);
        Assert.Equal(hit.Health.Remaining, f.Structures.Read(f.PilotId, structure.Id)!.Battle!.Health);
        using (WorldActionScope reinforce = Scope(f))
        {
            var row = reinforce.World.Set<SavedStructure>().Find(structure.Id)!;
            row.State = StructureState.ArmorReinforced;
            row.StateEndsAt = f.Clock.Now + TimeSpan.TicksPerDay;
            reinforce.Commit();
        }
        f.Clock.AdvanceWallTime(TimeSpan.FromSeconds(10));
        Assert.Empty(drones.Advance(f.PilotId, f.Clock.Now, 50).Hits);
        Assert.Equal(DroneActivity.Idle, Assert.Single(drones.List(Fixture.SystemId, f.Clock.Now)).Activity);
    }

    [Fact]
    public void AbandonedLossEjectsAssetsAndAFailedSettlementLeavesNoCommittedCustody()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long personal = f.Item(34, 20);
        using (WorldActionScope prepare = Scope(f))
        {
            prepare.World.Set<OwnedItem>().Find(personal)!.LocationId = structure.Id;
            var row = prepare.World.Set<SavedStructure>().Find(structure.Id)!;
            row.State = StructureState.HullVulnerable;
            row.LowPowerAt = f.Clock.Now - StructureRules.AbandonmentDuration;
            prepare.World.Add(new SavedStructureBattle { StructureId = structure.Id, Hull = 0.1 });
            prepare.Commit();
        }
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using (WorldActionScope rollback = Scope(f))
        {
            var result = StructureCombatTargets.Apply(
                rollback.World,
                catalog,
                rollback.World.Set<SavedStructure>().Find(structure.Id)!,
                new(10, 0, 0, 0),
                f.Clock.Now
            );
            Assert.True(result.Destroyed);
        }
        using (WorldActionScope unchanged = Scope(f))
        {
            Assert.Equal(structure.Id, unchanged.World.Set<OwnedItem>().Find(personal)!.LocationId);
            Assert.Empty(unchanged.World.Set<SavedStructureLoot>());
            Assert.Equal(0.1, unchanged.World.Set<SavedStructureBattle>().Find(structure.Id)!.Hull);
        }
        var combat = f.Db.Services.GetRequiredService<IPilotCombat>();
        long module = Gun(f);
        Assert.True(combat.Fire(f.PilotId, module, structure.Id, SpacePosition.Zero, f.Clock.Now, 0.5).Destroyed);
        Assert.Empty(f.Db.Services.GetRequiredService<IPilotAssetSafety>().List(f.PilotId, false));
        using WorldActionScope check = Scope(f);
        var item = check.World.Set<OwnedItem>().Find(personal)!;
        Assert.NotNull(check.World.Set<SavedStructureLoot>().Find(item.LocationId));
        Assert.Equal((int)InventoryFlag.Cargo, item.FlagId);
    }

    [Fact]
    public void ConnectedDockedPilotReceivesItsEvacuatedShipAndLosesStructureBindings()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        var docked = f.Db.Services.GetRequiredService<Eden.Gameplay.Docking.IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);
        using var services = f.Db.Services.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, docked.Account, [docked], factory.Create, 199, SignedSettings.Empty);
        context.Select(docked);
        context.Flight.Snapshot();
        var binding = context.Reference("@hangar", PilotBindingLifetime.Location);
        f.Structures.Unanchor(f.PilotId, structure.Id);
        long deadline = f.Structures.Read(f.PilotId, structure.Id)!.UnanchorAt!.Value;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - f.Clock.Now));
        services.ServiceProvider.GetRequiredService<StructureControlHandler>().Advance(context);
        Assert.False(context.Pilot.InStructure);
        Assert.NotNull(context.Pilot.Position);
        Assert.Null(context.ResolveReference(binding.Id));
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.SetState);
        Assert.NotNull(f.Db.Store.FindPilot(1000001)!.Position);
    }

    [Fact]
    public void NativeLocksAndTurretActivationPublishStructureDamageAndLoseTheLockOnReinforcement()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long module = Gun(f);
        f.Db.Store.SetPosition(f.ShipId, Fixture.Point + new SpacePosition(structure.Profile.Radius + 1000, 0, 0), Fixture.SystemId);
        var pilot = f.Db.Store.FindPilot(1000001)!;
        using var services = f.Db.Services.CreateScope();
        var factory = services.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        using PilotCallContext context = new(1000001, pilot.Account, [pilot], factory.Create, 201, SignedSettings.Empty);
        context.Select(pilot);
        context.Flight.Snapshot();
        var targeting = services.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.NotNull(context.Flight.FindTarget(structure.Id));
        Assert.IsType<PilotCallResult.Success>(targeting.Call(context, "dogmaIM", "AddTarget", [structure.Id]));
        f.Clock.Milliseconds += 60_000;
        targeting.Advance(context);
        Assert.True(targeting.IsLocked(structure.Id));
        context.Flight.Updates.Clear();
        Assert.IsType<PilotCallResult.Success>(
            services
                .ServiceProvider.GetRequiredService<CombatHandler>()
                .Call(context, "dogmaIM", "Activate", [module, CombatWireDefinitions.CivilianWeaponEffect, structure.Id, 0L])
        );
        Assert.Contains(context.Flight.Updates, update => update.Command is DestinyCommand.OnDamageStateChange);
        Assert.Contains(context.NotificationsBeforeFlight, note => note is DamageMessageNotification);
        Assert.True(f.Structures.Read(f.PilotId, structure.Id)!.DamageFractions!.Shield < 1);
        using (WorldActionScope reinforce = Scope(f))
        {
            var row = reinforce.World.Set<SavedStructure>().Find(structure.Id)!;
            row.State = StructureState.ArmorReinforced;
            row.StateEndsAt = f.Clock.Now + TimeSpan.TicksPerDay;
            reinforce.Commit();
        }
        context.Flight.RefreshStructures(((IStructureContacts)f.Structures).Contacts(Fixture.SystemId));
        targeting.Advance(context);
        Assert.False(targeting.IsLocked(structure.Id));
    }

    [Fact]
    public void DeliveredWrapReentersSafetyWhenItsDestinationUnanchorsWithoutWrappingItself()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long personal = f.Item(34, 20);
        using (WorldActionScope prepare = Scope(f))
        {
            prepare.World.Set<OwnedItem>().Find(personal)!.LocationId = structure.Id;
            prepare.Commit();
        }
        var safety = f.Db.Services.GetRequiredService<IPilotAssetSafety>();
        var first = safety.Move(f.PilotId, Fixture.SystemId, structure.Id, false)!;
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(5));
        safety.Deliver(f.PilotId, first.Id, Fixture.SystemId, structure.Id);
        f.Structures.Unanchor(f.PilotId, structure.Id);
        long deadline = f.Structures.Read(f.PilotId, structure.Id)!.UnanchorAt!.Value;
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(deadline - f.Clock.Now));
        Assert.Equal(StructureState.Unanchored, f.Structures.Read(f.PilotId, structure.Id)!.State);
        var second = Assert.Single(safety.List(f.PilotId, false));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(deadline, second.CreatedAt);
        using WorldActionScope read = Scope(f);
        Assert.Null(read.World.Set<OwnedItem>().Find(first.Id));
        Assert.Equal(first.Id, read.World.Set<OwnedItem>().Find(personal)!.LocationId);
    }

    [Fact]
    public void DepartureCancelsInstalledJobsReleasesBlueprintsAndPublishesSurvivingInputsOrCompletedOutputOnce()
    {
        using Fixture f = Prepare();
        var structure = f.Online();
        long copy = f.Item(1130);
        long original = f.Item(1130);
        using (WorldActionScope prepare = Scope(f))
        {
            var blueprint = prepare.World.Set<OwnedItem>().Find(copy)!;
            blueprint.LocationId = structure.Id;
            blueprint.BlueprintRuns = 7;
            blueprint.Singleton = true;
            prepare.World.Set<OwnedItem>().Find(original)!.LocationId = structure.Id;
            prepare.World.Add(
                new IndustryJob
                {
                    PilotId = f.PilotId,
                    BlueprintId = copy,
                    BlueprintTypeId = 1130,
                    StationId = structure.Id,
                    ProductTypeId = 35,
                    Runs = 2,
                    OutputQuantity = 4,
                    DurationSeconds = 3600,
                    StartTime = f.Clock.Now,
                    EndTime = f.Clock.Now + TimeSpan.TicksPerHour,
                    BlueprintCopy = true,
                    Status = IndustryJobStatus.Installed,
                    MaterialsJson = "{\"34\":40}",
                }
            );
            prepare.World.Add(
                new IndustryJob
                {
                    PilotId = f.PilotId,
                    BlueprintId = original,
                    BlueprintTypeId = 1130,
                    StationId = structure.Id,
                    ProductTypeId = 35,
                    Runs = 2,
                    OutputQuantity = 4,
                    DurationSeconds = 3600,
                    StartTime = f.Clock.Now - TimeSpan.TicksPerHour,
                    EndTime = f.Clock.Now,
                    Status = IndustryJobStatus.Installed,
                    MaterialsJson = "{\"34\":80}",
                }
            );
            prepare.Commit();
        }
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        using (WorldActionScope settle = Scope(f))
        {
            var row = settle.World.Set<SavedStructure>().Find(structure.Id)!;
            row.State = StructureState.Destroyed;
            StructureDeparture.Settle(settle.World, catalog, row, f.Clock.Now, destroyed: true, new SurvivingLoot());
            settle.Commit();
        }
        using (WorldActionScope retry = Scope(f))
        {
            StructureDeparture.Settle(
                retry.World,
                catalog,
                retry.World.Set<SavedStructure>().Find(structure.Id)!,
                f.Clock.Now,
                destroyed: true,
                new SurvivingLoot()
            );
            retry.Commit();
        }
        var wrap = Assert.Single(f.Db.Services.GetRequiredService<IPilotAssetSafety>().List(f.PilotId, false));
        using WorldActionScope check = Scope(f);
        Assert.Equal(wrap.Id, check.World.Set<OwnedItem>().Find(copy)!.LocationId);
        Assert.Equal(5, check.World.Set<OwnedItem>().Find(copy)!.BlueprintRuns);
        Assert.Equal(wrap.Id, check.World.Set<OwnedItem>().Find(original)!.LocationId);
        Assert.All(
            check.World.Set<IndustryJob>(),
            job =>
            {
                Assert.Equal(IndustryJobStatus.Cancelled, job.Status);
                Assert.Equal(f.Clock.Now, job.CompletedAt);
                Assert.Null(job.SuspendedAt);
            }
        );
        var loot = check.World.Set<SavedStructureLoot>().Select(c => c.Id).ToArray();
        Assert.Equal(
            40,
            check.World.Filter<OwnedItem>(i => i.TypeId == 34 && Enumerable.Contains(loot, i.LocationId)).Sum(i => i.Quantity)
        );
        Assert.Equal(4, check.World.Filter<OwnedItem>(i => i.TypeId == 35 && Enumerable.Contains(loot, i.LocationId)).Sum(i => i.Quantity));
    }

    private sealed class SurvivingLoot : IShipLootRoll
    {
        public bool Survives() => true;
    }

    internal static Fixture Prepare()
    {
        Fixture f = new(liveWorld: true);
        string path = new WorldDatabase(f.Db.Path).CatalogPath;
        PassiveCatalogFixture.Load(path);
        using CatalogDbContext catalog = new(path, writable: true);
        const string low = "{\"security\":0.4}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/solarsystemcontent','30001392',{low})");
        const string system = "{\"securityStatus\":0.4}";
        catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{system})");
        return f;
    }

    internal static long Gun(Fixture f)
    {
        using WorldActionScope action = Scope(f);
        var module = action.World.Filter<OwnedItem>(i => i.OwnerId == f.PilotId && i.TypeId == 3638).First();
        module.LocationId = f.ShipId;
        module.FlagId = (int)InventoryFlag.HighSlot0;
        module.Singleton = true;
        module.Online = true;
        var skill = action.World.Set<PilotSkill>().Find(f.PilotId, 3300);
        if (skill is null)
        {
            action.World.Add(
                new PilotSkill
                {
                    PilotId = f.PilotId,
                    TypeId = 3300,
                    Points = 250,
                }
            );
        }
        else
        {
            skill.Points = Math.Max(skill.Points, 250);
        }
        action.Commit();
        return module.Id;
    }

    internal static WorldActionScope Scope(Fixture f) => new(new(f.Db.Path), new(systems: [Fixture.SystemId]));
}
