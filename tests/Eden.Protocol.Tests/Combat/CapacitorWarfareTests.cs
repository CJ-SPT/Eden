using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Content;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Combat;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class CapacitorWarfareTests
{
    private static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadCapacitorModules(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000142','{{\"securityStatus\":0}}')"
        );
        // Test-only budgets exercise capital modules without admitting capital hulls.
        foreach (
            var attribute in new[]
            {
                DogmaAttributeId.HighSlots,
                DogmaAttributeId.CpuOutput,
                DogmaAttributeId.PowerOutput,
                DogmaAttributeId.CapacitorCapacity,
            }
        )
        {
            double value = attribute == DogmaAttributeId.HighSlots ? 8 : 1000000;
            catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES (601,{(int)attribute},{value})");
        }
        return f;
    }

    private static long Target(Fixture f) => f.Db.Store.FindPilot(1000002)!.ShipId;

    private static IPilotCapacitorWarfare Store(Fixture f) => f.Db.Services.GetRequiredService<IPilotCapacitorWarfare>();

    private static void Space(Fixture f, double source = 10000, double target = 50000)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        SpacePosition origin = new(pilot.X + pilot.Flight.StationRadius + 50000, pilot.Y, pilot.Z);
        f.Db.Store.SetPosition(f.ShipId, origin);
        f.Db.Store.SetPosition(Target(f), origin + new SpacePosition(1000, 0, 0));
        using WorldDbContext world = new(f.Db.Path);
        world.AddRange(
            new ShipEnergy
            {
                ShipId = f.ShipId,
                Charge = source,
                UpdatedAt = f.Clock.Now,
            },
            new ShipEnergy
            {
                ShipId = Target(f),
                Charge = target,
                UpdatedAt = f.Clock.Now,
            }
        );
        world.SaveChanges();
    }

    private static CapacitorActivation Activate(Fixture f, long module, DogmaEffect effect) =>
        Store(f).Activate(f.PilotId, module, effect, Target(f), new HashSet<long> { Target(f) }, f.Clock.Now);

    [Fact]
    public void EveryStaticCatalogProfileIsAdmittedAndAllEightMutatedProfilesRemainRejected()
    {
        using Fixture f = Prepare();
        using CatalogDbContext catalog = new(f.CatalogPath);
        Dictionary<int, int> counts = [];
        int rejected = 0;
        foreach (var type in catalog.Set<ItemType>().Where(t => t.GroupId == 67 || t.GroupId == 68 || t.GroupId == 71).ToArray())
        {
            bool abyssal = catalog.Require(CatalogData.Client.Types, (long)type.Id).MetaGroupID == 15;
            Assert.Equal(!abyssal, CapacitorModuleCatalog.Supported(catalog, type.Id, type.GroupId));
            Assert.Equal(!abyssal, FittingCatalog.Definition(catalog, type.Id) is not null);
            if (abyssal)
            {
                rejected++;
            }
            else
            {
                counts[type.GroupId] = counts.GetValueOrDefault(type.GroupId) + 1;
            }
        }
        Assert.Equal(37, counts[67]);
        Assert.Equal(55, counts[68]);
        Assert.Equal(54, counts[71]);
        Assert.Equal(8, rejected);
        var report = ShipContentAudit.Read(catalog);
        Assert.All(
            report.Entries.Where(e => e.Admitted && e.GroupId is 68 or 71),
            entry =>
                Assert.Contains(
                    entry.Checks,
                    c =>
                        c.Operation == ContentOperation.ConsumerCoverage
                        && c.Milestone == ShipContentMilestone.EnergyAndRemoteSupport
                        && !c.Implemented
                )
        );
    }

    [Theory]
    [InlineData(529)]
    [InlineData(12223)]
    [InlineData(3575)]
    [InlineData(530)]
    [InlineData(40668)]
    [InlineData(97710)]
    [InlineData(533)]
    [InlineData(40662)]
    [InlineData(97711)]
    public void EffectiveCatalogAmountsSkillsPaymentAndBothSavedPoolsAreExact(int type)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        int group = catalog.FittingType(type)!.GroupId;
        var raw = catalog.FittingAttributes(type);
        var effect = CapacitorWarfareRules.Effect(group)!.Value;
        var result = Activate(f, module, effect);
        double cost = raw.GetValueOrDefault(DogmaAttributeId.CapacitorNeed) * .75;
        if (
            InventoryDefinitions.RequiredSkills.Any(r =>
                raw.GetValueOrDefault(r.Skill) == (int)CapacitorEmissionSkill.CapitalCapacitorEmissionSystems
            )
        )
        {
            cost *= .75;
        }
        Assert.Equal(cost, result.Cycle.Module.CapacitorNeed, 7);
        Assert.Equal(raw[DogmaAttributeId.Duration], result.Cycle.Module.DurationMilliseconds);
        double amount = raw.GetValueOrDefault(
            group == 71 ? DogmaAttributeId.EnergyNeutralizerAmount : DogmaAttributeId.PowerTransferAmount
        );
        if (raw.GetValueOrDefault(DogmaAttributeId.EnergyNeutralizerSignatureResolution) > 0)
        {
            double signature = f.Fitting.ShipAttributes(f.Db.Store.FindPilot(1000002)!.PilotId)[DogmaAttributeId.SignatureRadius];
            amount *= Math.Min(1, signature / raw[DogmaAttributeId.EnergyNeutralizerSignatureResolution]);
        }
        Assert.Equal(amount, result.Applied, 7);
        Assert.Equal(10000 - cost + (group == 68 ? amount : 0), result.Capacitor, 7);
        Assert.Equal(50000 + (group == 67 ? amount : -amount), result.TargetCapacitor, 7);
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(result.Capacitor, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(result.TargetCapacitor, verify.Set<ShipEnergy>().Find(Target(f))!.Charge, 7);
        Assert.Equal(result.Cycle.ExpiresAt, verify.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        Assert.Equal(
            result.TargetCapacitor,
            f.Db.Services.GetRequiredService<IPilotShipResources>().Capacitor(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now),
            7
        );
    }

    [Theory]
    [InlineData("missing-lock")]
    [InlineData("self")]
    [InlineData("non-pilot")]
    [InlineData("wrong-effect")]
    [InlineData("owner")]
    [InlineData("offline")]
    [InlineData("skill")]
    [InlineData("capacitor")]
    [InlineData("different-system")]
    [InlineData("destroyed")]
    [InlineData("range")]
    public void RejectionsLeaveBothPoolsCooldownAggressionAndDroneTriggersUnchanged(string rejection)
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        Space(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            switch (rejection)
            {
                case "owner":
                    world.Set<OwnedItem>().Find(module)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
                    break;
                case "offline":
                    world.Set<OwnedItem>().Find(module)!.Online = false;
                    break;
                case "skill":
                    world.Set<PilotSkill>().Find(f.PilotId, (int)CapacitorEmissionSkill.CapacitorEmissionSystems)!.Points = 0;
                    break;
                case "capacitor":
                    world.Set<ShipEnergy>().Find(f.ShipId)!.Charge = 0;
                    break;
                case "different-system":
                    world.Positions.Find(Target(f))!.SystemId = 30000144;
                    break;
                case "destroyed":
                    world.Add(
                        new ShipHealthState
                        {
                            ShipId = Target(f),
                            StructureDamage = 1000000,
                            UpdatedAt = f.Clock.Now,
                        }
                    );
                    break;
                case "range":
                    world.Positions.Find(Target(f))!.X += 100000;
                    break;
            }
            world.SaveChanges();
        }
        long target =
            rejection == "self" ? f.ShipId
            : rejection == "non-pilot" ? long.MaxValue
            : Target(f);
        var effect = rejection == "wrong-effect" ? DogmaEffect.EnergyNosferatu : DogmaEffect.EnergyNeutralizer;
        Assert.Throws<InvalidDataException>(() =>
            Store(f)
                .Activate(f.PilotId, module, effect, target, rejection == "missing-lock" ? [] : new HashSet<long> { target }, f.Clock.Now)
        );
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(rejection == "capacitor" ? 0 : 10000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(50000, verify.Set<ShipEnergy>().Find(Target(f))!.Charge);
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Empty(verify.Set<SavedCrimewatch>());
        Assert.Empty(verify.Set<SavedPilotHostility>());
        Assert.Empty(verify.Set<Eden.Data.World.Drones.WorldDroneCombatTriggers>());
    }

    [Theory]
    [InlineData(529, false)]
    [InlineData(530, true)]
    [InlineData(533, true)]
    public void OnlyOffensiveCapacitorModulesPublishAggressionAndOrderedDroneTriggers(int type, bool offensive)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using CatalogDbContext catalog = new(f.CatalogPath);
        Activate(f, module, CapacitorWarfareRules.Effect(catalog.FittingType(type)!.GroupId)!.Value);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(offensive ? 1 : 0, world.Set<SavedPilotHostility>().Count());
        Assert.Equal(offensive ? 2 : 0, world.Set<Eden.Data.World.Drones.WorldDroneCombatTriggers>().Count());
        Assert.Equal(offensive, world.Set<SavedCrimewatch>().Find(f.PilotId)!.WeaponsUntil > f.Clock.Now);
    }

    [Fact]
    public async Task ResidentCapacitorChangesCheckpointAndRestartDoesNotReplayThePaidPulse()
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        Space(f);
        CapacitorActivation result;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var resource = f.Db.Services.GetRequiredService<IPilotShipResources>();
            _ = resource.Capacitor(f.PilotId, f.Clock.Now);
            _ = resource.Capacitor(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now);
            result = Activate(f, module, DogmaEffect.EnergyNeutralizer);
            Assert.Equal(result.TargetCapacitor, resource.Capacitor(f.Db.Store.FindPilot(1000002)!.PilotId, f.Clock.Now), 7);
            Assert.True(Store(f).IsOnline(f.PilotId, f.ShipId, module));
            long ship = f.ShipId;
            using (
                var metrics = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin("warm capacitor membership", _ => { }, force: true)
            )
            {
                Assert.True(Store(f).IsOnline(f.PilotId, ship, module));
                Assert.Equal(0, metrics!.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(result.Capacitor, reopened.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(result.TargetCapacitor, reopened.Set<ShipEnergy>().Find(Target(f))!.Charge, 7);
        Assert.Equal(result.Cycle.ExpiresAt, reopened.Set<ModuleCooldown>().Find(module)!.ReadyAt);
        Assert.Empty(reopened.Set<ActiveFittedModule>());
    }

    [Theory]
    [InlineData(620)]
    [InlineData(625)]
    [InlineData(643)]
    [InlineData(17920)]
    [InlineData(17922)]
    [InlineData(17926)]
    [InlineData(32305)]
    [InlineData(32874)]
    [InlineData(33079)]
    [InlineData(47269)]
    [InlineData(47270)]
    [InlineData(47271)]
    [InlineData(49710)]
    [InlineData(49711)]
    [InlineData(49712)]
    [InlineData(73789)]
    [InlineData(91775)]
    [InlineData(95741)]
    public void AdmittedEnergyHullBonusesConsumeTheirEarnedModifiersOnce(int hull)
    {
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadSmallHull(f.CatalogPath, hull);
        PassiveCatalogFixture.LoadCombatHull(f.CatalogPath, hull);
        Eden.Protocol.Tests.Persistence.SmallHullBonusTests.VerifyBonuses(f, hull);
    }

    [Fact]
    public void CruorNosferatuDrainsALowerCapacitorTargetThroughItsActualHullOverride()
    {
        using Fixture f = Prepare();
        Eden.Protocol.Tests.Persistence.SmallHullTests.Board(f, 17926);
        long module = f.Fit(530);
        Assert.Equal(1, f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.NosOverride]);
        Space(f, source: 100, target: 1);
        var result = Activate(f, module, DogmaEffect.EnergyNosferatu);
        Assert.True(result.Cycle.Module.NosOverride);
        Assert.Equal(1, result.Applied, 7);
        Assert.Equal(101, result.Capacitor, 7);
        Assert.Equal(0, result.TargetCapacitor);
    }

    [Theory]
    [InlineData(529)]
    [InlineData(530)]
    [InlineData(533)]
    public void UnsafeAssistanceAndAggressionRejectBeforePaymentOrPublishingEvents(int type)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        Space(f);
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"securityStatus\":1}}' WHERE Dataset='client/systems' AND Key='30000142'"
            );
        }
        if (type == 529)
        {
            long targetPilot = f.Db.Store.FindPilot(1000002)!.PilotId;
            using WorldDbContext world = new(f.Db.Path);
            CrimewatchPersistence.State(world, targetPilot).SuspectUntil = f.Clock.Now + TimeSpan.FromMinutes(15).Ticks;
            world.SaveChanges();
        }
        List<CapacitorChanged> published = [];
        f.Db.Services.GetRequiredService<CapacitorSignal>().Changed += published.Add;
        var effect =
            type == 529 ? DogmaEffect.RemoteCapacitorTransfer
            : type == 530 ? DogmaEffect.EnergyNosferatu
            : DogmaEffect.EnergyNeutralizer;
        Assert.Throws<InvalidDataException>(() => Activate(f, module, effect));
        using WorldDbContext verify = new(f.Db.Path);
        Assert.Equal(10000, verify.Set<ShipEnergy>().Find(f.ShipId)!.Charge);
        Assert.Equal(50000, verify.Set<ShipEnergy>().Find(Target(f))!.Charge);
        Assert.Null(verify.Set<ModuleCooldown>().Find(module));
        Assert.Empty(verify.Set<ActiveFittedModule>());
        Assert.Empty(verify.Set<SavedPilotHostility>());
        Assert.Empty(published);
    }

    [Theory]
    [InlineData(529, 15)]
    [InlineData(530, 4)]
    [InlineData(533, 22.5)]
    public void TransfersAndHostileDrainsUseTheirOwnImpedanceRatherThanRepairImpedance(int type, double amount)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        int targetType = f.Db.Store.FindPilot(1000002)!.ShipTypeId;
        using (CatalogDbContext catalog = new(f.CatalogPath, writable: true))
        {
            foreach (
                var attribute in new[]
                {
                    DogmaAttributeId.EnergyWarfareResistance,
                    DogmaAttributeId.RemoteCapacitorImpedance,
                    DogmaAttributeId.RemoteRepairImpedance,
                }
            )
            {
                int id = (int)attribute;
                double value = attribute == DogmaAttributeId.RemoteRepairImpedance ? 0 : .5;
                catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO DogmaAttribute VALUES ({id},{id.ToString()})");
                catalog.Database.ExecuteSql($"INSERT OR REPLACE INTO TypeAttribute VALUES ({targetType},{id},{value})");
            }
        }
        Space(f);
        var effect =
            type == 529 ? DogmaEffect.RemoteCapacitorTransfer
            : type == 530 ? DogmaEffect.EnergyNosferatu
            : DogmaEffect.EnergyNeutralizer;
        var result = Activate(f, module, effect);
        Assert.Equal(amount, result.Applied, 7);
    }

    [Theory]
    [InlineData(529, 1, 5, .95)]
    [InlineData(529, 3, 5, .85)]
    [InlineData(529, 5, 5, .75)]
    [InlineData(12219, 5, 1, .7125)]
    [InlineData(12219, 5, 3, .6375)]
    [InlineData(12219, 5, 5, .5625)]
    public void OrdinaryAndCapitalEmissionSkillsReduceOnlyModulesThatRequireThem(int type, int ordinary, int capital, double factor)
    {
        using Fixture f = Prepare();
        long module = f.Fit(type);
        f.Train((int)CapacitorEmissionSkill.CapacitorEmissionSystems, ordinary);
        f.Train((int)CapacitorEmissionSkill.CapitalCapacitorEmissionSystems, capital);
        var item = f.Inventory.List(f.PilotId, f.ShipId).Single(i => i.Id == module);
        var actual = f.Fitting.ModuleAttributes(f.PilotId, item);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var raw = catalog.FittingAttributes(type);
        Assert.Equal(raw[DogmaAttributeId.CapacitorNeed] * factor, actual[DogmaAttributeId.CapacitorNeed], 7);
        Assert.Equal(raw[DogmaAttributeId.PowerTransferAmount], actual[DogmaAttributeId.PowerTransferAmount]);
        Assert.Equal(raw[DogmaAttributeId.Duration], actual[DogmaAttributeId.Duration]);
    }

    [Fact]
    public async Task CompetingActivationsOfOneModulePayAndDrainExactlyOnce()
    {
        using Fixture f = Prepare();
        long module = f.Fit(533);
        Space(f);
        long target = Target(f);
        long now = f.Clock.Now;
        int successes = 0;
        int rejections = 0;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            Store(f).Activate(f.PilotId, module, DogmaEffect.EnergyNeutralizer, target, new HashSet<long> { target }, now);
                            Interlocked.Increment(ref successes);
                        }
                        catch (InvalidDataException)
                        {
                            Interlocked.Increment(ref rejections);
                        }
                    })
                )
        );
        Assert.Equal(1, successes);
        Assert.Equal(1, rejections);
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(10000 - 45 * .75, world.Set<ShipEnergy>().Find(f.ShipId)!.Charge, 7);
        Assert.Equal(50000 - 45, world.Set<ShipEnergy>().Find(target)!.Charge, 7);
        Assert.Single(world.Set<ModuleCooldown>());
    }

    [Fact]
    public void TwoSameTickModulesSeeTheFirstCommittedResourceChangeInResidentState()
    {
        using Fixture f = Prepare();
        long first = f.Fit(533);
        long second = f.Fit(533);
        Space(f);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var resources = f.Db.Services.GetRequiredService<IPilotShipResources>();
        _ = resources.Capacitor(f.PilotId, f.Clock.Now);
        long recipient = f.Db.Store.FindPilot(1000002)!.PilotId;
        _ = resources.Capacitor(recipient, f.Clock.Now);
        var a = Activate(f, first, DogmaEffect.EnergyNeutralizer);
        var b = Activate(f, second, DogmaEffect.EnergyNeutralizer);
        Assert.Equal(a.Capacitor - b.Cycle.Module.CapacitorNeed, b.Capacitor, 7);
        Assert.Equal(a.TargetCapacitor - b.Applied, b.TargetCapacitor, 7);
        Assert.Equal(b.TargetCapacitor, resources.Capacitor(recipient, f.Clock.Now), 7);
    }

    [Fact]
    public void EqualSavedGJAcrossDifferentCapacitorCapacitiesNeverPermitsANosferatuDrain()
    {
        using Fixture f = Prepare();
        long module = f.Fit(530);
        long target = Target(f);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Ships.Find(target)!.TypeId = 620;
            world.SaveChanges();
        }
        Space(f, source: 1000, target: 1000);
        var result = Activate(f, module, DogmaEffect.EnergyNosferatu);
        Assert.Equal(0, result.Applied);
        Assert.Equal(1000, result.Capacitor);
        Assert.Equal(1000, result.TargetCapacitor);
    }
}
