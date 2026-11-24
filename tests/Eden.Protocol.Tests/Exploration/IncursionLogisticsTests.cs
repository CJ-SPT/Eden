using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Protocol.Tests.Combat;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Exploration;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionLogisticsTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public async Task ModuleAndDroneCreditOnlyEffectiveIncursionDamageRepairs(bool useDrone, bool sameFleet, bool onGrid)
    {
        using var fixture = UtilityDroneTests.Prepare();
        var setup = fixture.Base;
        PassiveCatalogFixture.LoadIncursions(setup.CatalogPath);
        PassiveCatalogFixture.LoadRemoteRepairs(setup.CatalogPath);
        long item = useDrone ? fixture.Bay(23711, 1) : setup.Fit(11355);
        var clock = setup.Clock;
        clock.UtcNow = DateTimeOffset.FromFileTime(IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now))).ToUniversalTime();
        var database = new WorldDatabase(setup.Db.Path);
        var source = setup.Db.Store.FindPilot(1000001)!;
        var target = setup.Db.Store.FindPilot(1000002)!;
        var fleets = setup.Db.Services.GetRequiredService<ICooperativeFleets>();
        var directory = setup.Db.Services.GetRequiredService<IIncursionDirectory>();
        var sites = setup.Db.Services.GetRequiredService<ICombatAnomalies>();
        var space = setup.Db.Services.GetRequiredService<SharedSpace>();
        long siteId;
        double credit;

        using (LiveWorld world = new(database, clock, _ => { }, fleets: fleets))
        {
            int system = Assert
                .Single(directory.Read(clock.Now))
                .Systems.First(entry => entry.SceneType == IncursionScene.Vanguard)
                .SystemId;
            var site = Assert.Single(sites.Incursions(system, clock.Now));
            siteId = site.Id;
            var center = site.Position + IncursionSiteCatalog.CombatOffset;
            var sourcePosition = onGrid ? center : center + new SpacePosition(NpcEncounterDefinitions.VisibilityRange + 100, 0, 0);
            var targetPosition = onGrid ? center + new SpacePosition(200, 0, 0) : sourcePosition - new SpacePosition(200, 0, 0);
            setup.Db.Store.SetPosition(source.ShipId, sourcePosition, system);
            setup.Db.Store.SetPosition(target.ShipId, targetPosition, system);
            foreach (long account in new long[] { 1000001, 1000002 })
            {
                var pilot = setup.Db.Store.FindPilot(account)!;
                fleets.Update(Guid.NewGuid(), new(pilot.PilotId, pilot.CorpId, pilot.ShipId, pilot.ShipTypeId, system, pilot.Position));
                space.Publish(new(PilotContact.From(pilot), SpacePosition.Zero, null, 0, null));
            }
            if (sameFleet)
            {
                var fleet = fleets.Create(source.PilotId);
                fleets.Invite(source.PilotId, fleet.Id, target.PilotId, new(FleetRules.NoGroup, FleetRules.NoGroup, FleetRole.Member));
                fleets.Accept(target.PilotId, fleet.Id);
            }
            sites.Arrive(system, center, clock.Now);

            var repairer = setup.Db.Services.GetRequiredService<IPilotRemoteRepairing>();
            long? droneId = useDrone
                ? Assert.Single(fixture.Drones.Launch(source.PilotId, [new(item, 1)], clock.Now).Launches).DroneIds[0]
                : null;
            if (droneId.HasValue)
            {
                Repair();
            }
            using (GameplaySqlGuard.Begin())
            {
                SetDamage(10);
                Repair();
                Assert.Equal(0, ReadCredit(world));

                SetDamage(0);
                using (var action = world.Economy.Begin(new(ships: [target.ShipId], systems: [system])))
                {
                    using CatalogDbContext catalog = new(setup.CatalogPath);
                    var victim = action.Pilots.Find(target.PilotId)!;
                    var npc = action.Filter<WorldNpc>(candidate => candidate.EncounterId == siteId && candidate.TypeId == 2859).Single();
                    var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(action, catalog, victim));
                    var impact = NpcCombatStore.ApplyPlayerDamage(action, catalog, npc, victim, defense, new(8, 0, 0, 0), clock.Now);
                    Assert.InRange(defense.Armor - impact.Remaining.Armor, 1, 8);
                    action.Commit();
                }
                double armor = world.Economy.Inspect(action => action.Set<SavedIncursionRepairDamage>().Find(siteId, target.ShipId)!.Armor);
                Assert.InRange(armor, 1, 8);
                Repair();
                credit = ReadCredit(world);
                Assert.Equal(sameFleet && onGrid ? armor : 0, credit, 6);
                Assert.Equal(
                    0,
                    world.Economy.Inspect(action => action.Set<SavedIncursionRepairDamage>().Find(siteId, target.ShipId)!.Armor),
                    6
                );

                // Healing unrelated damage after the Incursion deficit has already been restored adds no credit.
                SetDamage(10);
                Repair();
                Assert.Equal(credit, ReadCredit(world), 6);
            }
            await world.FlushAsync();

            void SetDamage(double armorDamage)
            {
                using var action = world.Economy.Begin(new(ships: [target.ShipId]));
                var health =
                    action.Set<ShipHealthState>().Find(target.ShipId) ?? action.Add(new ShipHealthState { ShipId = target.ShipId }).Entity;
                health.ShieldCharge = 0;
                health.ArmorDamage = armorDamage;
                health.UpdatedAt = clock.Now;
                action.Commit();
            }

            void Repair()
            {
                var locks = new HashSet<long> { target.ShipId };
                if (droneId is long drone)
                {
                    fixture.Drones.Order(source.PilotId, [drone], DroneOrder.Engage, target.ShipId, locks, clock.Now);
                    fixture.Tick();
                    fixture.Tick(5);
                }
                else
                {
                    var paid = repairer.Start(source.PilotId, item, DogmaEffect.RemoteArmorRepair, target.ShipId, locks, clock.Now);
                    clock.AdvanceWallTime(TimeSpan.FromTicks(paid.Cycle.CompletesAt - clock.Now));
                    repairer.Complete(source.PilotId, item, clock.Now);
                    repairer.Complete(source.PilotId, item, clock.Now);
                }
            }
        }

        using LiveWorld reopened = new(database, clock, _ => { }, fleets: fleets);
        Assert.Equal(credit, ReadCredit(reopened), 6);
        using (var action = reopened.Economy.Begin(new(pilots: [source.PilotId, target.PilotId])))
        {
            using CatalogDbContext catalog = new(setup.CatalogPath);
            var site = action.Set<WorldCombatAnomaly>().Find(siteId)!;
            IncursionStore.Contribution(action, site, target.PilotId).Damage = 100;
            site.CompletedAt = clock.Now;
            IncursionStore.CompleteSite(action, catalog, site, clock.Now);
            action.Commit();
        }
        long targetPoints = Assert.IsType<IncursionPendingLoyalty>(directory.PendingLoyalty(target.PilotId, clock.Now)).Amount;
        Assert.Equal(sameFleet && onGrid ? targetPoints : 0, directory.PendingLoyalty(source.PilotId, clock.Now)?.Amount ?? 0);

        double ReadCredit(LiveWorld world) =>
            world.Economy.Inspect(action => action.Set<SavedIncursionContribution>().Find(siteId, source.PilotId)?.Repair ?? 0);
    }
}
