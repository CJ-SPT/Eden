using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Exploration;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Exploration;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionSecurityTests
{
    [Fact]
    public async Task LowVictoryPaysOnlyItsPoolAndLeavesTheFlagshipWreckThroughRestart()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadIncursions(fixture.CatalogPath);
        PassiveCatalogFixture.LoadIncursionSecurity(fixture.CatalogPath);
        WorldDatabase database = new(fixture.Db.Path);
        var clock = fixture.Clock;
        long first = IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now));
        clock.UtcNow = DateTimeOffset.FromFileTime(first);
        var directory = fixture.Db.Services.GetRequiredService<IIncursionDirectory>();
        var sites = fixture.Db.Services.GetRequiredService<ICombatAnomalies>();
        var loyalty = fixture.Db.Services.GetRequiredService<IPilotLoyaltyWallet>();
        var wallet = fixture.Db.Services.GetRequiredService<IPilotWalletReader>();
        long id = fixture.PilotId;
        long shipId = fixture.ShipId;
        long wreckId;
        int headquarterSystem;
        long lowId;
        long nullId;
        long blueprintId;

        database.Catalog.PrepareRuntime();
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        ExplorationCatalogPreparation.Prepare(catalog);
        using (LiveWorld live = new(database, clock, _ => { }))
        {
            using (GameplaySqlGuard.Begin())
            {
                fixture.Db.Services.GetRequiredService<IIncursionMaintenance>().Advance(clock.Now);
                var active = directory.Read(clock.Now);
                Assert.Equal(5, active.Count);
                Assert.Equal(30, active.SelectMany(focus => focus.Systems).Select(system => system.SystemId).Distinct().Count());
                var high = Assert.Single(active, focus => focus.Security == IncursionSecurity.High);
                Assert.Equal(IncursionRules.TaleId(clock.Now), high.TaleId);
                var low = Assert.Single(active, focus => focus.Security == IncursionSecurity.Low);
                var nullFocuses = active.Where(focus => focus.Security == IncursionSecurity.Null).ToArray();
                Assert.Equal(3, nullFocuses.Length);
                lowId = low.TaleId;
                var nullFocus = nullFocuses[0];
                nullId = nullFocus.TaleId;
                headquarterSystem = Headquarters(low);
                long before = wallet.Balance(id);

                ClearVanguard(live, low);
                Assert.Equal(before + 100_200, wallet.Balance(id));
                ClearVanguard(live, nullFocus);
                Assert.Equal(before + 200_500, wallet.Balance(id));
                Assert.Equal([lowId, nullId], directory.PendingLoyalties(id, clock.Now).Select(row => row.TaleId).ToArray());

                using (var world = live.Economy.Begin())
                {
                    foreach (long taleId in new[] { lowId, nullId })
                    {
                        var campaign = world.Set<SavedIncursionCampaign>().Find(taleId)!;
                        campaign.Clears = IncursionRules.ClearsToUnlockFinal;
                        campaign.HeadquartersCleared = true;
                    }
                    world.Commit();
                }

                Assert.DoesNotContain(
                    sites.Incursions(headquarterSystem, clock.Now),
                    site => site.DungeonId == IncursionSiteCatalog.Uroborus
                );
                Assert.Contains(
                    sites.Incursions(Headquarters(nullFocus), clock.Now),
                    site => site.DungeonId == IncursionSiteCatalog.KundaliniManifest
                );
                clock.UtcNow = DateTimeOffset.FromFileTime(first + TimeSpan.TicksPerDay - 1);
                Assert.DoesNotContain(
                    sites.Incursions(headquarterSystem, clock.Now),
                    site => site.DungeonId == IncursionSiteCatalog.Uroborus
                );
                clock.AdvanceWallTime(TimeSpan.FromTicks(1));
                fixture.Db.Services.GetRequiredService<IIncursionMaintenance>().Advance(clock.Now);
                Assert.True(Assert.Single(directory.Read(clock.Now), focus => focus.TaleId == lowId).HasFinalEncounter);
                var contact = Assert.Single(
                    sites.Incursions(headquarterSystem, clock.Now),
                    site => site.DungeonId == IncursionSiteCatalog.Uroborus
                );
                var position = contact.Position + IncursionSiteCatalog.CombatOffset;
                fixture.Db.Store.SetPosition(shipId, position, headquarterSystem);
                using (var world = live.Economy.Begin(new(pilots: [id], systems: [headquarterSystem])))
                {
                    var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
                    var flagship = world
                        .Add(
                            new WorldNpc
                            {
                                Id = world.AvailableNpcId(NpcEncounterDefinitions.FirstNpcId + 750),
                                SystemId = headquarterSystem,
                                TypeId = 3071,
                                Role = NpcRole.Pirate,
                                AnchorKind = NpcAnchorKind.Incursion,
                                CorporationId = 1000162,
                                FactionId = 500019,
                                EncounterId = site.Id,
                                HomeX = position.X,
                                HomeY = position.Y,
                                HomeZ = position.Z,
                                X = position.X,
                                Y = position.Y,
                                Z = position.Z,
                                UpdatedAt = clock.Now,
                                MotionAt = clock.Now,
                                DestroyedAt = clock.Now,
                            }
                        )
                        .Entity;
                    Complete(world, site);
                    var wreck = Assert.Single(world.Set<WorldWreck>().Local);
                    Assert.Equal(flagship.Id, wreck.NpcId);
                    Assert.Equal(3260, wreck.TypeId);
                    Assert.Equal(id, wreck.OwnerId);
                    wreckId = wreck.Id;
                    var cargo = world.Filter<OwnedItem>(item => item.LocationId == wreck.Id).ToArray();
                    Assert.InRange(cargo.Length, 2, 4);
                    var blueprint = Assert.Single(cargo, item => item.BlueprintRuns == 1 && item.Singleton);
                    Assert.Equal(1, blueprint.Quantity);
                    blueprintId = blueprint.Id;
                    world.Commit();
                }

                Assert.Equal(1104, Assert.Single(loyalty.Read(id)).Points);
                Assert.Equal(nullId, Assert.Single(directory.PendingLoyalties(id, clock.Now)).TaleId);
                Assert.DoesNotContain(directory.Read(clock.Now), focus => focus.TaleId == lowId);
                Assert.Equal(4, directory.Read(clock.Now).Count);
                Assert.False(Assert.Single(directory.AgencyUpdates, row => row.TaleId == lowId).IsActive);
            }
            await live.FlushAsync();
        }

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            Assert.Equal(4, directory.Read(clock.Now).Count);
            Assert.Empty(sites.Incursions(headquarterSystem, clock.Now));
            Assert.Equal(1104, Assert.Single(loyalty.Read(id)).Points);
            using var world = live.Economy.Begin(new(pilots: [id], systems: [headquarterSystem]));
            Assert.Single(WreckStore.Take(world, catalog, id, shipId, wreckId, [blueprintId], null, clock.Now));
            var blueprint = world.Set<OwnedItem>().Find(blueprintId)!;
            Assert.Equal(1, blueprint.BlueprintRuns);
            Assert.Equal(shipId, blueprint.LocationId);
            world.Commit();
        }

        void ClearVanguard(LiveWorld live, IncursionFocus focus)
        {
            int system = focus.Systems.First(entry => entry.SceneType == IncursionScene.Vanguard).SystemId;
            var contact = Assert.Single(
                sites.Incursions(system, clock.Now),
                site => site.DungeonId == IncursionSiteCatalog.NationCommanderOutpost
            );
            fixture.Db.Store.SetPosition(shipId, contact.Position + IncursionSiteCatalog.CombatOffset, system);
            using var world = live.Economy.Begin(new(pilots: [id], systems: [system]));
            Complete(world, world.Set<WorldCombatAnomaly>().Find(contact.Id)!);
            world.Commit();
        }

        void Complete(WorldAction world, WorldCombatAnomaly site)
        {
            IncursionStore.Contribution(world, site, id).Damage = 100;
            site.CompletedAt = clock.Now;
            IncursionStore.CompleteSite(world, catalog, site, clock.Now);
        }
    }

    [Fact]
    public void OnlyLowSecurityFlagshipLootCanContainARevenantCopy()
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadIncursionSecurity(fixture.CatalogPath);
        using CatalogDbContext catalog = new(fixture.CatalogPath);
        const int systemId = 30002718;
        const int revenant = 3515;
        foreach (var security in Enum.GetValues<IncursionSecurity>())
        {
            int rare = 0;
            for (int site = 1; site <= 500; site++)
            {
                var loot = IncursionLoot.Roll(catalog, systemId, site, security);
                Assert.Equal(loot, IncursionLoot.Roll(catalog, systemId, site, security));
                Assert.InRange(loot.Count, 2, 4);
                Assert.Equal(loot.Count, loot.Select(drop => drop.TypeId).Distinct().Count());
                if (Assert.Single(loot, drop => drop.BlueprintCopy).TypeId == revenant)
                {
                    rare++;
                }
            }
            if (security == IncursionSecurity.Low)
            {
                Assert.InRange(rare, 1, 60);
            }
            else
            {
                Assert.Equal(0, rare);
            }
        }
    }

    private static int Headquarters(IncursionFocus focus) =>
        Assert.Single(focus.Systems, system => system.SceneType == IncursionScene.Headquarters).SystemId;
}
