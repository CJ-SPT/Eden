using Eden.Data.World.Exploration;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class IncursionCampaignTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SitePayoutAndDelayedLoyaltySurviveCheckpointAndSettleOnlyOnVictory(bool repel)
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadIncursions(fixture.CatalogPath);
        WorldDatabase database = new(fixture.Db.Path);
        var clock = fixture.Clock;
        var opens = IncursionRules.OpensAt(IncursionRules.TaleId(clock.Now));
        clock.UtcNow = DateTimeOffset.FromFileTime(opens);
        var directory = fixture.Db.Services.GetRequiredService<IIncursionDirectory>();
        var sites = fixture.Db.Services.GetRequiredService<ICombatAnomalies>();
        var wallet = fixture.Db.Services.GetRequiredService<IPilotWalletReader>();
        var loyalty = fixture.Db.Services.GetRequiredService<IPilotLoyaltyWallet>();
        long pilotId = fixture.PilotId;
        long shipId = fixture.ShipId;
        long initialBalance;
        int systemId;
        int assaultSystem;
        int headquartersSystem;
        int scoutSystem;
        long expectedVictoryLoyalty = 0;
        const long SoloIskCents = 314_344_800;
        const long SoloPoints = 423;

        database.Catalog.PrepareRuntime();
        using (CatalogDbContext catalog = new(fixture.CatalogPath))
        {
            ExplorationCatalogPreparation.Prepare(catalog);
        }
        using (LiveWorld live = new(database, clock, _ => { }))
        {
            IncursionFocus focus;
            using (GameplaySqlGuard.Begin())
            {
                fixture.Db.Services.GetRequiredService<IIncursionMaintenance>().Advance(clock.Now);
                focus = Assert.Single(directory.Read(clock.Now));
            }
            Assert.Equal(1, focus.Influence);
            Assert.Equal(opens + IncursionRules.ActiveTicks, focus.EndsAt);
            Assert.Empty(directory.PendingLoyalties(pilotId, clock.Now));
            systemId = focus.Systems.First(system => system.SceneType == IncursionScene.Vanguard).SystemId;
            assaultSystem = Assert.Single(focus.Systems, system => system.SceneType == IncursionScene.Assault).SystemId;
            headquartersSystem = Assert.Single(focus.Systems, system => system.SceneType == IncursionScene.Headquarters).SystemId;
            scoutSystem = focus.StagingSystemId;
            Assert.False(focus.HasFinalEncounter);
            initialBalance = wallet.Balance(pilotId);

            Clear(live, systemId, IncursionSiteKind.Vanguard);
            Assert.Equal(initialBalance + SoloIskCents, wallet.Balance(pilotId));
            Assert.Contains(
                wallet.Journal(pilotId).ServicePayments!,
                payment =>
                    payment.Kind == WalletEntryType.CorporateRewardPayout
                    && payment.AmountCents == SoloIskCents
                    && payment.CounterpartyId == LoyaltyStoreRules.ConcordCorporation
                    && payment.ContextId == systemId
            );
            Assert.Equal(SoloPoints, Assert.Single(directory.PendingLoyalties(pilotId, clock.Now)).Amount);
            using var scope = fixture.Db.Services.CreateScope();
            var selected = fixture.Db.Store.FindPilot(1000001)!;
            using PilotCallContext context = new(
                1000001,
                selected.Account,
                [selected],
                pilot => new(fixture.Db.Store, pilot, clock, new(), fixture.Db.Objects),
                1,
                SignedSettings.Empty
            );
            context.Select(selected);
            var handler = scope.ServiceProvider.GetRequiredService<WalletHandler>();
            var result = Assert.IsType<PilotCallResult.Success>(
                handler.Call(context, "account", "GetTransactions", [(long)WalletAccountKey.Cash, null, null, false])
            );
            var entries = Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
            var payout = Assert.Single(
                entries.Select(entry => BlueRead.Fields(Assert.IsType<BlueInstance>(entry).State)),
                fields => Equals(fields["entryTypeID"], (long)WalletEntryType.CorporateRewardPayout)
            );
            Assert.Equal((long)LoyaltyStoreRules.ConcordCorporation, payout["ownerID1"]);
            Assert.Equal(pilotId, payout["ownerID2"]);
            Assert.Empty(loyalty.Read(pilotId));
            await live.FlushAsync();
        }

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            Assert.Equal(IncursionRules.Influence(1), Assert.Single(directory.Read(clock.Now)).Influence);
            Assert.Equal(initialBalance + SoloIskCents, wallet.Balance(pilotId));
            Assert.Equal(SoloPoints, Assert.Single(directory.PendingLoyalties(pilotId, clock.Now)).Amount);
            if (repel)
            {
                Assert.DoesNotContain(
                    sites.Incursions(headquartersSystem, clock.Now),
                    site => IncursionSiteCatalog.Kind(site.DungeonId) == IncursionSiteKind.Mothership
                );
                Clear(live, scoutSystem, IncursionSiteKind.Scout);
                Clear(live, assaultSystem, IncursionSiteKind.Assault);
                for (int total = 3; total < IncursionRules.ClearsToUnlockFinal; total++)
                {
                    clock.AdvanceWallTime(TimeSpan.FromSeconds(CombatAnomalyRules.ReplacementSeconds));
                    Clear(live, systemId, IncursionSiteKind.Vanguard);
                }

                var contested = Assert.Single(directory.Read(clock.Now));
                Assert.Equal(0, contested.Influence);
                Assert.False(contested.HasFinalEncounter);
                Assert.DoesNotContain(
                    sites.Incursions(headquartersSystem, clock.Now),
                    site => IncursionSiteCatalog.Kind(site.DungeonId) == IncursionSiteKind.Mothership
                );
                Assert.Empty(loyalty.Read(pilotId));

                Clear(live, headquartersSystem, IncursionSiteKind.Headquarters);
                expectedVictoryLoyalty = (IncursionRules.ClearsToUnlockFinal - 2) * SoloPoints + 51;
                Assert.Equal(expectedVictoryLoyalty, Assert.Single(directory.PendingLoyalties(pilotId, clock.Now)).Amount);
                Assert.True(Assert.Single(directory.AgencyUpdates).HasFinalEncounter);
                Assert.True(Assert.Single(directory.Read(clock.Now)).HasFinalEncounter);
                Assert.Equal(
                    IncursionSiteCatalog.KundaliniManifest,
                    Assert
                        .Single(
                            sites.Incursions(headquartersSystem, clock.Now),
                            site => site.DungeonId == IncursionSiteCatalog.KundaliniManifest
                        )
                        .DungeonId
                );
            }
            else
            {
                clock.AdvanceWallTime(TimeSpan.FromSeconds(CombatAnomalyRules.ReplacementSeconds));
                Clear(live, systemId, IncursionSiteKind.Vanguard, onGrid: false);
                Assert.Equal(SoloPoints, Assert.Single(directory.PendingLoyalties(pilotId, clock.Now)).Amount);
                clock.UtcNow = DateTimeOffset.FromFileTime(opens + IncursionRules.ActiveTicks);
                fixture.Db.Services.GetRequiredService<IIncursionMaintenance>().Advance(clock.Now);
                Assert.Empty(loyalty.Read(pilotId));
                Assert.Equal(initialBalance + SoloIskCents, wallet.Balance(pilotId));
                Assert.Empty(live.Economy.Inspect(world => world.Set<SavedIncursionContribution>().ToArray()));
            }
            await live.FlushAsync();
        }

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            if (repel)
            {
                Assert.True(Assert.Single(directory.Read(clock.Now)).HasFinalEncounter);
                Assert.Equal(expectedVictoryLoyalty, Assert.Single(directory.PendingLoyalties(pilotId, clock.Now)).Amount);
                Clear(live, headquartersSystem, IncursionSiteKind.Mothership);
                Assert.False(Assert.Single(directory.AgencyUpdates).IsActive);
                Assert.Empty(sites.Incursions(systemId, clock.Now));
                Assert.Empty(sites.Incursions(headquartersSystem, clock.Now));
                await live.FlushAsync();
            }
        }

        using (LiveWorld live = new(database, clock, _ => { }))
        {
            Assert.Empty(directory.Read(clock.Now));
            Assert.Empty(directory.PendingLoyalties(pilotId, clock.Now));
            Assert.Equal(
                expectedVictoryLoyalty,
                loyalty
                    .Read(pilotId)
                    .Where(balance => balance.IssuerCorporationId == LoyaltyStoreRules.ConcordCorporation)
                    .Sum(balance => balance.Points)
            );
            clock.UtcNow = DateTimeOffset.FromFileTime(opens + IncursionRules.RotationTicks);
            var replacement = Assert.Single(directory.Read(clock.Now));
            Assert.DoesNotContain(replacement.Systems, system => system.SystemId == systemId);
        }

        void Clear(LiveWorld live, int targetSystem, IncursionSiteKind kind, bool onGrid = true)
        {
            var contact = Assert.Single(
                sites.Incursions(targetSystem, clock.Now),
                site =>
                    site.DungeonId
                    == (
                        kind switch
                        {
                            IncursionSiteKind.Scout => IncursionSiteCatalog.DistressBeacon,
                            IncursionSiteKind.Vanguard => IncursionSiteCatalog.NationCommanderOutpost,
                            IncursionSiteKind.Assault => IncursionSiteCatalog.NationCommanderStronghold,
                            IncursionSiteKind.Headquarters => IncursionSiteCatalog.NationRebirthFacility,
                            IncursionSiteKind.Mothership => IncursionSiteCatalog.KundaliniManifest,
                            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                        }
                    )
            );
            var position = contact.Position + (kind == IncursionSiteKind.Scout ? SpacePosition.Zero : IncursionSiteCatalog.CombatOffset);
            if (!onGrid)
            {
                position += new SpacePosition(0, NpcEncounterDefinitions.VisibilityRange + 1_000, 0);
            }
            fixture.Db.Store.SetPosition(shipId, position, targetSystem);
            using var guard = GameplaySqlGuard.Begin();
            Assert.Contains(sites.Incursions(targetSystem, clock.Now), candidate => candidate.Id == contact.Id);
            using var world = live.Economy.Begin(new(pilots: [pilotId], systems: [targetSystem]));
            using CatalogDbContext catalog = new(fixture.CatalogPath);
            var site = world.Set<WorldCombatAnomaly>().Find(contact.Id)!;
            CombatParty attacker = new(shipId, pilotId, 0, null, null, null, null, 0);
            PilotInteractionHistoryStore.Damage(world, site.Id, attacker, 500);
            IncursionStore.RecordKill(world, site, site.Id, clock.Now);
            world.Remove(world.Set<Eden.Data.World.Characters.SavedCombatDamage>().Find(site.Id, shipId)!);
            site.CompletedAt = clock.Now;
            site.ReplaceAt = clock.Now + TimeSpan.FromSeconds(CombatAnomalyRules.ReplacementSeconds).Ticks;
            CombatAnomalyStore.Invalidate(world, targetSystem);
            IncursionStore.CompleteSite(world, catalog, site, clock.Now);
            world.Commit();
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(8, true)]
    public void Schema223KeepsPartialFocusAndDoesNotReopenAnAlreadyPaidCampaign(int clears, bool defeated)
    {
        using Fixture fixture = new();
        PassiveCatalogFixture.LoadIncursions(fixture.CatalogPath);
        WorldDatabase database = new(fixture.Db.Path);
        var clock = fixture.Clock;
        long taleId = IncursionRules.TaleId(clock.Now);
        long opens = IncursionRules.OpensAt(taleId);
        clock.UtcNow = DateTimeOffset.FromFileTime(opens).AddMinutes(1);
        using (WorldDbContext file = new(database.Path))
        {
            file.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldIncursionCampaign DROP COLUMN HeadquartersCleared;
                ALTER TABLE WorldIncursionCampaign DROP COLUMN DefeatedAt;
                ALTER TABLE WorldIncursionContribution DROP COLUMN Repair;
                PRAGMA user_version=223;
                """
            );
            file.Database.ExecuteSql($"INSERT INTO WorldIncursionCampaign(TaleId,Clears,LastUpdated) VALUES ({taleId},{clears},{opens})");
            if (!defeated)
            {
                file.Database.ExecuteSql(
                    $"""
                    INSERT INTO WorldIncursionContribution(SiteId,PilotId,TaleId,Damage,PendingLoyalty)
                    VALUES (1,{fixture.PilotId},{taleId},6,423)
                    """
                );
            }
        }

        WorldInitialization.Open(database);
        using LiveWorld live = new(database, clock, _ => { });
        var directory = fixture.Db.Services.GetRequiredService<IIncursionDirectory>();
        if (defeated)
        {
            Assert.Empty(directory.Read(clock.Now));
            Assert.Empty(directory.PendingLoyalties(fixture.PilotId, clock.Now));
        }
        else
        {
            var focus = Assert.Single(directory.Read(clock.Now));
            Assert.Equal(IncursionRules.Influence(clears), focus.Influence);
            Assert.False(focus.HasFinalEncounter);
            Assert.Equal(423, Assert.Single(directory.PendingLoyalties(fixture.PilotId, clock.Now)).Amount);
        }
    }
}
