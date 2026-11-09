using System.Text.Json;
using Eden.Data.World.Combat;
using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol.Docking;
using Eden.Protocol.Information;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Targeting;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Information;

public sealed partial class InformationTests
{
    [Fact]
    public void Version112UpgradeAddsSalvageOnceAndNeverRerollsAnExistingWreck()
    {
        using Fixture f = Prepare();
        Space(f);
        long wreck = Wreck(f);
        string manifest;
        using (WorldDbContext world = new(f.Db.Path))
        {
            manifest = world.Set<WorldWreck>().Find(wreck)!.SalvageJson!;
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldInformationCycle;
                ALTER TABLE WorldWreck DROP COLUMN SalvageJson;
                ALTER TABLE WorldWreck DROP COLUMN SalvagedAt;
                PRAGMA user_version=112;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path));
        WorldInitialization.Initialize(new(f.Db.Path));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(manifest, verify.Set<WorldWreck>().Find(wreck)!.SalvageJson);
        Assert.Equal(WorldMigration.CurrentVersion, verify.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Empty(verify.Set<SavedInformationCycle>());
    }

    [Fact]
    public void FleetTractorPermissionIsLostImmediatelyOnDepartureAndNeverChangesLootOwnership()
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        var source = f.Db.Store.FindPilot(1000001)!;
        var owner = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.OwnerId = owner.PilotId;
            world.SaveChanges();
        }
        var fleets = f.Db.Services.GetRequiredService<ICooperativeFleets>();
        fleets.Update(
            Guid.NewGuid(),
            new(source.PilotId, source.CorpId, source.ShipId, source.ShipTypeId, source.SystemId, source.Position)
        );
        fleets.Update(Guid.NewGuid(), new(owner.PilotId, owner.CorpId, owner.ShipId, owner.ShipTypeId, owner.SystemId, owner.Position));
        var fleet = fleets.Create(owner.PilotId);
        fleets.Invite(owner.PilotId, fleet.Id, source.PilotId, new(-1, -1, FleetRole.Member));
        fleets.Accept(source.PilotId, fleet.Id);
        var activation = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        fleets.Leave(source.PilotId, fleet.Id);
        Assert.Throws<InvalidDataException>(() => Store(f).AdvanceTractor(activation.Cycle, f.Clock.Now + TimeSpan.TicksPerSecond));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(owner.PilotId, verify.Set<WorldWreck>().Find(wreck)!.OwnerId);
    }

    [Fact]
    public void SurveyUsesItsCatalogSkillAndDoesNotMistakeCpuManagementForSurvey()
    {
        using Fixture f = Prepare();
        long module = f.Fit(442);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        Assert.Equal(5000, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Duration], 7);
        f.Train((int)InformationSkill.Survey, 1);
        Assert.Equal(4750, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Duration], 7);
        f.Train((int)InformationSkill.Survey, 5);
        Assert.Equal(3750, f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.Duration], 7);
    }

    [Fact]
    public void NoctisUsesActualBudgetsEarnedTractorAndSalvageBonusesAndAdditiveRigs()
    {
        using Fixture f = Prepare();
        Eden.Protocol.Tests.Persistence.SmallHullTests.Board(f, 2998);
        using (CatalogDbContext catalog = new(f.CatalogPath))
        {
            foreach (int skill in Eden.World.Fittings.HullCatalog.Skills(catalog, 2998))
            {
                f.Train(skill, 5);
            }
        }
        long tractor = f.Fit(24348);
        long salvager = f.Fit(25861);
        for (int i = 0; i < 3; i++)
        {
            _ = f.Fit(24348);
            _ = f.Fit(25861);
        }
        _ = f.Fit(31085, online: false);
        var items = f.Inventory.List(f.PilotId, f.ShipId);
        var pulling = f.Fitting.ModuleAttributes(f.PilotId, items.Single(i => i.Id == tractor));
        var salvaging = f.Fitting.ModuleAttributes(f.PilotId, items.Single(i => i.Id == salvager));
        Assert.Equal(80000, pulling[DogmaAttributeId.MaxRange], 7);
        Assert.Equal(2000, pulling[DogmaAttributeId.MaxTractorVelocity], 7);
        Assert.Equal(3750, pulling[DogmaAttributeId.Duration], 7);
        Assert.Equal(7500, salvaging[DogmaAttributeId.Duration], 7);
        Assert.Equal(35, salvaging[DogmaAttributeId.AccessDifficultyBonus], 7);
        var ship = f.Fitting.ShipAttributes(f.PilotId);
        Assert.True(ship[DogmaAttributeId.CpuLoad] <= ship[DogmaAttributeId.CpuOutput]);
        Assert.True(ship[DogmaAttributeId.PowerLoad] <= ship[DogmaAttributeId.PowerOutput]);
    }

    [Fact]
    public void FullCargoRejectsSalvageSettlementWithoutConsumingTheWreckOrManifest()
    {
        using Fixture f = Prepare();
        long module = f.Fit(25861);
        _ = f.Grant(34, 12500, cargo: true);
        Space(f);
        long wreck = Wreck(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value={1000d} WHERE ItemTypeId={25861} AND AttributeId={902}");
        }
        string manifest = JsonSerializer.Serialize(new[] { new ScanItem(25599, 2) });
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.SalvageJson = manifest;
            world.SaveChanges();
        }
        var cycle = Store(f).Begin(f.PilotId, module, DogmaEffect.Salvaging, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        f.Clock.Milliseconds += (long)cycle.Cycle.Module.DurationMilliseconds;
        Assert.Throws<CargoCapacityException>(() => Store(f).Complete(f.PilotId, module, cycle.Cycle.ReadyAt, f.Clock.Now));
        using WorldDbContext verify = new(f.Db.Path);
        var row = verify.Set<WorldWreck>().Find(wreck)!;
        Assert.Null(row.SalvagedAt);
        Assert.Equal(manifest, row.SalvageJson);
        Assert.DoesNotContain(verify.Set<OwnedItem>(), i => i.TypeId == 25599);
        Assert.Equal(12500, verify.Set<OwnedItem>().Single(i => i.TypeId == 34 && i.LocationId == f.ShipId).Quantity);
        Assert.NotNull(verify.Set<SavedInformationCycle>().Find(module));
    }

    [Fact]
    public void ForeignWreckSalvageDoesNotGrantLootRightsOrCreateHostility()
    {
        using Fixture f = Prepare();
        long module = f.Fit(25861);
        Space(f);
        long wreck = Wreck(f, contents: true);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql($"UPDATE TypeAttribute SET Value={1000d} WHERE ItemTypeId={25861} AND AttributeId={902}");
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldWreck>().Find(wreck)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
            world.Set<WorldWreck>().Find(wreck)!.SalvageJson = "[]";
            world.SaveChanges();
        }
        var cycle = Store(f).Begin(f.PilotId, module, DogmaEffect.Salvaging, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        f.Clock.Milliseconds += (long)cycle.Cycle.Module.DurationMilliseconds;
        Assert.True(Store(f).Complete(f.PilotId, module, cycle.Cycle.ReadyAt, f.Clock.Now).Salvage!.Success);
        Assert.Throws<InvalidDataException>(() => f.Db.Services.GetRequiredService<IWrecks>().Read(f.PilotId, wreck, f.Clock.Now));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Empty(verify.Set<SavedPilotHostility>());
        Assert.Equal(17, verify.Set<OwnedItem>().Single(i => i.LocationId == wreck).Quantity);
    }

    [Fact]
    public void DirectionalScanningFindsAnOffGridShipButNeverReturnsSelfOrRemoteCargo()
    {
        using Fixture f = Prepare();
        Space(f);
        var position = f.Db.Store.FindPilot(1000001)!.Position!;
        f.Db.Store.SetPosition(Target(f), position + new SpacePosition(1_000_000, 0, 0));
        var result = Store(f).Directional(f.PilotId, Math.PI / 4, 2_000_000, new(1, 0, 0), f.Clock.Now);
        Assert.Contains(result, r => r.Id == Target(f));
        Assert.Contains(Store(f).Directional(f.PilotId, Math.Tau, 2_000_000, new(1, 0, 0), f.Clock.Now), r => r.Id == f.StationId);
        Assert.DoesNotContain(result, r => r.Id == f.ShipId);
        Assert.DoesNotContain(Store(f).Directional(f.PilotId, Math.PI / 4, 2_000_000, new(-1, 0, 0), f.Clock.Now), r => r.Id == Target(f));
    }

    [Fact]
    public void AutomaticTargetingOnlyAcquiresActualAttackersAndUsesTheOnlineTargetBonus()
    {
        using Fixture f = Prepare();
        f.Train(3429, 5);
        f.Train(3430, 5);
        int before = (int)f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxLockedTargets];
        long module = f.Fit(1182);
        Assert.True(f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.MaxLockedTargets] > before);
        Space(f);
        using var source = Client(f, 1000001);
        using var recipient = Client(f, 1000002);
        using var scope = f.Db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<InformationHandler>();
        var targeting = scope.ServiceProvider.GetRequiredService<TargetingHandler>();
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "targetHostiles", null, 0L]));
        f.Clock.Seconds += 5;
        handler.Advance(source);
        Assert.False(targeting.IsLocked(Target(f)));
        Assert.False(targeting.IsPending(Target(f)));
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedPilotHostility
                {
                    SourceShipId = Target(f),
                    TargetShipId = f.ShipId,
                    LastAttackAt = f.Clock.Now,
                    ExpiresAt = f.Clock.Now + TimeSpan.TicksPerMinute,
                }
            );
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Success>(handler.CallModule(source, "Activate", [module, "targetHostiles", null, 0L]));
        f.Clock.Seconds += 5;
        handler.Advance(source);
        Assert.True(targeting.IsPending(Target(f)));
        f.Clock.Seconds += 30;
        targeting.Advance(source);
        Assert.True(targeting.IsLocked(Target(f)));
    }

    [Fact]
    public void RolledBackWreckMutationRetainsTheResidentTractorPositionAndContinuesOnce()
    {
        using Fixture f = Prepare();
        long module = f.Fit(24348);
        Space(f);
        long wreck = Wreck(f, 10000);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var cycle = Store(f).Begin(f.PilotId, module, DogmaEffect.TractorBeam, wreck, new HashSet<long> { wreck }, f.Clock.Now);
        f.Clock.Seconds++;
        Store(f).AdvanceTractor(cycle.Cycle, f.Clock.Now);
        var wrecks = f.Db.Services.GetRequiredService<IWrecks>();
        double before = Assert.Single(wrecks.List(30000142, f.Clock.Now)).Position.X;
        using (WorldDbContext world = new(f.Db.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Database.ExecuteSql($"UPDATE WorldWreck SET X={-1000d} WHERE Id={wreck}");
            transaction.Rollback();
        }
        Assert.Equal(before, Assert.Single(wrecks.List(30000142, f.Clock.Now)).Position.X, 5);
        Store(f).AdvanceTractor(cycle.Cycle, f.Clock.Now);
        f.Clock.Seconds++;
        Store(f).AdvanceTractor(cycle.Cycle, f.Clock.Now);
        Assert.Equal(before - cycle.Cycle.Module.TractorVelocity, Assert.Single(wrecks.List(30000142, f.Clock.Now)).Position.X, 5);
    }
}
