using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Economy;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerAmmunitionSupplyTests
{
    [Fact]
    public async Task PilotBuildsAnUnavailableSpareFromOwnedMaterialsAndDeliversItAfterRestart()
    {
        const int industrySkill = 3380;
        const int factorySystem = 30000142;
        const int hull = (int)TechOneHullType.Merlin;
        const int gun = (int)CombatWeaponType.GatlingRailgun75mm;
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true);
        CapsuleerCorporationTests.PrepareExistingSave(fixture);
        PrepareCatalog(fixture);
        UseLocalOrders(fixture);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/systems','30000142','{{"securityStatus":-0.2}}');
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);

        long pilot;
        long jobId;
        long due;
        long charge;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using (var setup = live.Economy.Begin())
            {
                setup.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = false;
                setup.Set<SavedEconomicPolicy>().Single().ChargeServiceCosts = true;
                pilot = setup.Set<SavedCapsuleerAgent>().First(agent => agent.Career == CapsuleerCareer.Hauling).PilotId;
                var ship = setup.Pilots.Find(pilot)!.ActiveShip;
                setup.Filter<SavedCapsuleerHauler>(row => row.PilotId == pilot).Single().Progress = null;
                ship.StationId = FactoryStation;
                ship.TypeId = hull;
                if (setup.Positions.Find(ship.Id) is { } position)
                {
                    setup.Remove(position);
                }
                setup.RemoveRange(setup.Filter<OwnedItem>(item => item.OwnerId == pilot).ToArray());
                TrafficFitting.Apply(
                    setup,
                    catalog,
                    pilot,
                    ship.Id,
                    TrafficSnapshotTests.Profile() with
                    {
                        HullTypeId = hull,
                        Equipment = [new(gun, (int)InventoryFlag.HighSlot0, 1), new(Rules.ChargeType, (int)InventoryFlag.HighSlot0, 20)],
                    },
                    fixture.Clock.Now
                );
                var skill =
                    setup.Set<PilotSkill>().Find(pilot, industrySkill)
                    ?? setup.Add(new PilotSkill { PilotId = pilot, TypeId = industrySkill }).Entity;
                skill.Points = Math.Max(skill.Points, 250);
                setup.Set<PilotWallet>().Find(pilot)!.Cents = 100_000_000_000;
                foreach (int type in new[] { Tritanium, Pyerite })
                {
                    (setup.Set<SavedAdjustedPrice>().Find(type) ?? setup.Add(new SavedAdjustedPrice { TypeId = type }).Entity).PriceIsk =
                        1000;
                }
                AddStock(setup, pilot, FactoryStation, hull, 1);
                AddStock(setup, pilot, FactoryStation, gun, 1);
                AddStock(setup, pilot, FactoryStation, Rules.BlueprintType, 1);
                var blueprint = setup.Filter<OwnedItem>(item => item.OwnerId == pilot).Single(item => item.TypeId == Rules.BlueprintType);
                blueprint.Singleton = true;
                blueprint.BlueprintRuns = -1;
                AddStock(setup, pilot, FactoryStation, Tritanium, 27);
                AddStock(setup, pilot, FactoryStation, Pyerite, 21);
                setup.Commit();
            }
            using (var work = live.Economy.Begin())
            {
                var docked = host.GetRequiredService<SqliteWorld>().FindPilot(work, CapsuleerCommunityStore.MinerAccountId, pilot)!;
                long balance = work.Set<PilotWallet>().Find(pilot)!.Cents;
                var supply = host.GetRequiredService<CapsuleerIndustry>();
                Assert.Equal(0, CapsuleerIndustry.ReadyHuntReplacements(work, catalog, docked));
                Assert.False(supply.TryReplenishHuntReserve(work, catalog, docked, fixture.Clock.Now));
                var job = Assert.Single(work.Filter<IndustryJob>(job => job.PilotId == pilot));
                (jobId, due, charge) = (job.Id, Assert.IsType<long>(job.CapsuleerEndTicks), job.CostCents);
                Assert.True(charge > 0);
                Assert.Equal(balance - charge, work.Set<PilotWallet>().Find(pilot)!.Cents);
                Assert.Equal(0, Stock(work, pilot, FactoryStation, Tritanium));
                Assert.Equal(0, Stock(work, pilot, FactoryStation, Pyerite));
                Assert.Equal(0, CapsuleerIndustry.ReadyHuntReplacements(work, catalog, docked));
                Assert.Empty(work.Filter<SavedPersonalOrder>(order => order.PilotId == pilot));
                work.Commit();
            }
            await live.FlushAsync();
        }

        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(jobId, restored.Economy.Read(world => CapsuleerCorporations.Organization(world, pilot).HuntReserveJobId));
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(Customer, factorySystem);
        var maintenance = reopened.GetRequiredService<CapsuleerMaintenance>();
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        for (int turn = 0; turn < 60 && restored.HaulerCheckpoints.GetValueOrDefault(pilot)?.Work.DueTicks != due; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        var waiting = Assert.IsType<HaulerObservation>(scheduler.Hauler(pilot));
        Assert.Null(waiting.Pilot.Position);
        Assert.Equal(FactoryStation, waiting.Pilot.StationId);
        Assert.Equal(due, waiting.Work.DueTicks);
        fixture.Clock.Seconds += Math.Max(0, due - restored.CapsuleerActivity.Read().ElapsedTicks) / TimeSpan.TicksPerSecond + 1;
        for (int turn = 0; turn < 15 && restored.Economy.Peek<IndustryJob>(jobId)!.Status != IndustryJobStatus.Delivered; turn++)
        {
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        Assert.True(scheduler.Read().Failures == 0, scheduler.Read().LastFailure);
        restored.Economy.Inspect(world =>
        {
            var job = world.Set<IndustryJob>().Find(jobId)!;
            Assert.Equal(IndustryJobStatus.Delivered, job.Status);
            Assert.Equal(charge, job.CostCents);
            Assert.Equal(100, job.OutputQuantity);
            Assert.True(
                world.Filter<OwnedItem>(item => item.OwnerId == pilot && item.TypeId == Rules.ChargeType).Sum(item => item.Quantity) >= 100
            );
            Assert.Null(CapsuleerCorporations.Organization(world, pilot).HuntReserveJobId);
            return 0;
        });
    }
}
