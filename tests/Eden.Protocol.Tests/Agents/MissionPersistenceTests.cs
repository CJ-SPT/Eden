using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionPersistenceTests
{
    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    [InlineData(5, 7)]
    public void EveryHigherAgentLevelEnforcesSavedThresholdAndIndependentNegativeCutoff(int level, double threshold)
    {
        using MissionFixture f = new(level);
        Assert.False(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = 1000044,
                    Value = threshold,
                }
            );
            world.SaveChanges();
        }
        Assert.True(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = MissionFixture.AgentId,
                    Value = -2,
                }
            );
            world.SaveChanges();
        }
        Assert.False(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<SavedMission>());
    }

    [Fact]
    public async Task ConcurrentRequestsReturnOneOfferAndConcurrentAcceptsIssueCargoOnce()
    {
        using MissionFixture f = new();
        var offers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => f.Request())));
        Assert.Single(offers.Select(o => o.Id).Distinct());
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            f.Accept(offers[0]);
                            return true;
                        }
                        catch (InvalidDataException)
                        {
                            return false;
                        }
                    })
                )
        );
        Assert.Single(results, success => success);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Single(check.Set<SavedMission>());
        Assert.Equal(offers[0].Cargo!.Quantity, check.Set<OwnedItem>().Single(i => i.TypeId == offers[0].Cargo!.TypeId).Quantity);
    }

    [Fact]
    public void AlphaCannotUseInactiveOmegaSocialSkillsOrLevelFourAgents()
    {
        using MissionFixture f = new(level: 2);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var pilot = world.Pilots.Include(p => p.Account).Single(p => p.Id == MissionFixture.PilotId);
            pilot.Account.CloneGrade = Eden.Gameplay.Docking.AccountCloneGrade.Alpha;
            world.Add(
                new PilotSkill
                {
                    PilotId = pilot.Id,
                    TypeId = (int)MissionSocialSkill.Connections,
                    Points = TrainedSkill.PointsForLevel(3, 5),
                }
            );
            world.SaveChanges();
        }
        // This fixture's pinned Alpha profile admits no Connections levels.
        Assert.False(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        Assert.Throws<InvalidDataException>(() => f.Request());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Pilots.Include(p => p.Account).Single(p => p.Id == MissionFixture.PilotId).Account.CloneGrade = Eden.Gameplay
                .Docking
                .AccountCloneGrade
                .Omega;
            world.SaveChanges();
        }
        Assert.True(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
    }

    [Fact]
    public void CourierOfferCargoCompletionAndRewardsSurviveReopenAndRejectDuplicatePayout()
    {
        using MissionFixture f = new();
        var offer = f.Request();
        Assert.Equal(2912, offer.MissionId);
        Assert.Equal(MissionState.Offered, offer.State);
        Assert.Equal(200_000_00, offer.Rewards.IskCents);
        Assert.Equal(1000, offer.Rewards.LoyaltyPoints);
        var accepted = f.Accept(offer);
        Assert.Equal(MissionState.Accepted, accepted.State);
        Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = Assert.Single(world.Set<OwnedItem>().Where(i => i.TypeId == accepted.Cargo!.TypeId));
            Assert.Equal(accepted.Cargo!.Quantity, item.Quantity);
            Assert.Equal(MissionFixture.StationId, item.LocationId);
            item.LocationId = MissionFixture.DestinationId;
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        var paid = missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id);
        Assert.Equal(MissionState.Completed, paid.Offer!.State);
        Assert.Equal(offer.Rewards.IskCents + offer.Bonus!.IskCents, paid.BalanceCents);
        Assert.Equal(1000, paid.PointsAfter);
        Assert.Equal(2, paid.Standings.Count);
        Assert.All(paid.Standings, s => Assert.Equal(0.0075, s.After, 12));
        Assert.Throws<InvalidDataException>(() =>
            missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == accepted.Cargo!.TypeId));
        Assert.Single(check.Set<SavedMarketPayment>().Where(p => p.Kind == Eden.Gameplay.Market.WalletEntryType.AgentMissionReward));
        Assert.Equal(1000, check.Set<SavedLoyaltyBalance>().Single().Points);
        Assert.Empty(missions.Journal(MissionFixture.PilotId));
    }

    [Fact]
    public void MissingCargoWrongLocationStaleActionsAndOtherPilotsCannotChangeAssetsOrPayRewards()
    {
        using MissionFixture f = new();
        var offer = f.Request();
        f.Accept(offer);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Accept, offer.Id)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, offer.Id + 1)
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId + 1, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Equal(offer.Cargo!.Quantity, world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo.TypeId).Quantity);
        Assert.Empty(world.Set<SavedMarketPayment>().Where(p => p.Kind == Eden.Gameplay.Market.WalletEntryType.AgentMissionReward));
        Assert.Empty(world.Set<SavedNpcStanding>());
    }

    [Fact]
    public void FailedRewardTransactionRollsBackCargoWalletLpAndStandingTogether()
    {
        using MissionFixture f = new();
        var offer = f.Accept(f.Request());
        using (WorldDbContext world = new(f.Db.Path))
        {
            var cargo = world.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo!.TypeId);
            cargo.LocationId = MissionFixture.DestinationId;
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER Reject_Mission_Reward BEFORE INSERT ON WorldStandingTransaction
                    BEGIN SELECT RAISE(ABORT,'injected mission payout failure'); END;
                """
            );
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        Assert.Throws<DbUpdateException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(MissionState.Accepted, check.Set<SavedMission>().Single().State);
        Assert.Equal(offer.Cargo!.Quantity, check.Set<OwnedItem>().Single(i => i.TypeId == offer.Cargo.TypeId).Quantity);
        Assert.Empty(check.Set<SavedMarketPayment>());
        Assert.Empty(check.Set<SavedLoyaltyBalance>());
        Assert.Empty(check.Set<SavedNpcStanding>());
    }

    [Fact]
    public void OfferAndAcceptedDeadlinesAndFreeDeclineWindowPersistAtExactBoundaries()
    {
        using MissionFixture f = new();
        var offer = f.Request();
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Decline, offer.Id);
        var next = f.Request();
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Decline, next.Id)
        );
        f.Clock.AdvanceWallTime(MissionRules.FreeDeclineInterval);
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Decline, next.Id);
        var expiring = f.Request();
        f.Clock.AdvanceWallTime(MissionRules.OfferLifetime);
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        Assert.Equal(MissionState.Failed, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.State);
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Accept, expiring.Id)
        );
        var accepted = f.Accept(f.Request());
        f.Clock.AdvanceWallTime(MissionRules.MissionLifetime);
        Assert.Empty(f.Missions.Journal(MissionFixture.PilotId));
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, accepted.Id)
        );
        using WorldDbContext world = new(f.Db.Path);
        Assert.Empty(world.Set<SavedMarketPayment>());
    }

    [Fact]
    public void SixteenCompletionsOfferSavedFactionStorylineAtTheOriginalMissionLevel()
    {
        using MissionFixture f = new();
        for (int i = 0; i < MissionRules.StorylineInterval; i++)
        {
            f.Dock(MissionFixture.StationId);
            var offer = f.Accept(f.Request());
            using (WorldDbContext world = new(f.Db.Path))
            {
                var item = world.Set<OwnedItem>().Single(item => item.TypeId == offer.Cargo!.TypeId);
                item.LocationId = MissionFixture.DestinationId;
                world.SaveChanges();
            }
            f.Dock(MissionFixture.DestinationId);
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id);
        }
        var storyline = Assert.Single(f.Missions.Journal(MissionFixture.PilotId));
        Assert.True(storyline.Storyline);
        Assert.Equal(MissionFixture.StorylineId, storyline.Agent.Id);
        Assert.Equal(1, storyline.Level);
        Assert.Equal(0, storyline.Rewards.LoyaltyPoints);
        f.Dock(MissionFixture.StationId);
        f.Accept(storyline);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Single(item => item.TypeId == storyline.Cargo!.TypeId);
            item.LocationId = MissionFixture.DestinationId;
            world.SaveChanges();
        }
        f.Dock(MissionFixture.DestinationId);
        var completed = f.Missions.Act(MissionFixture.PilotId, MissionFixture.StorylineId, MissionAction.Complete, storyline.Id);
        Assert.Contains(completed.Standings, s => s.SourceId == 500001 && Math.Abs(s.After - 0.175) < 1e-10);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(0, check.Set<SavedMissionProgress>().Single().Completed);
    }

    [Fact]
    public void AgentAccessUsesSavedEffectiveStandingsAndRechecksAtAcceptance()
    {
        using MissionFixture f = new(level: 2);
        Assert.False(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        Assert.Throws<InvalidDataException>(() => f.Request());
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new PilotSkill
                {
                    PilotId = MissionFixture.PilotId,
                    TypeId = (int)MissionSocialSkill.Connections,
                    Points = TrainedSkill.PointsForLevel(3, 3),
                }
            );
            world.SaveChanges();
        }
        Assert.True(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        var offer = f.Request();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = 500001,
                    Value = -2,
                }
            );
            world.SaveChanges();
        }
        Assert.False(f.Missions.Access(MissionFixture.PilotId, MissionFixture.AgentId).Allowed);
        Assert.Throws<InvalidDataException>(() => f.Accept(offer));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(MissionState.Offered, check.Set<SavedMission>().Single().State);
        Assert.Empty(check.Set<OwnedItem>().Where(i => i.TypeId == offer.Cargo!.TypeId));
    }

    [Fact]
    public void CombatArrivalAndLossesAreSharedAndDoNotRespawnAcrossReopen()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        var offer = f.Accept(f.Request());
        var site = Assert.Single(f.Missions.Sites(offer.SystemId));
        Assert.False(site.Started);
        f.Missions.Arrive(offer.SystemId, offer.Position);
        f.Missions.Arrive(offer.SystemId, offer.Position);
        long[] ids;
        using (WorldDbContext world = new(f.Db.Path))
        {
            ids = world.Set<SavedMissionMember>().Select(m => m.NpcId).ToArray();
            Assert.Equal(9, ids.Length);
        }
        Assert.Throws<InvalidDataException>(() =>
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id)
        );
        foreach (long id in ids)
        {
            f.Kill(id);
        }
        using var reopened = ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        Assert.True(missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        missions.Arrive(offer.SystemId, offer.Position);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(ids.Length, world.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Mission));
            Assert.Equal(ids.Length, world.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Mission && n.DestroyedAt != null));
        }
        var paid = missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, offer.Id);
        Assert.True(paid.BalanceCents >= offer.Rewards.IskCents);
    }

    [Fact]
    public void MissionSchemaUpgradeIsIdempotentAndPreservesExistingPilotAndWallet()
    {
        using MissionFixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldMissionMember; DROP TABLE WorldMission; DROP TABLE WorldMissionProgress;
                DROP TABLE WorldAgentDecline; PRAGMA user_version=122;
                """
            );
        }
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.NotNull(f.Db.Store.FindPilot(1000001));
        Assert.Equal(MissionState.Offered, f.Request().State);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
