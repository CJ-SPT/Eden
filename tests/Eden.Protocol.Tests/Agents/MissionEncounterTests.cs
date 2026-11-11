using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionEncounterTests
{
    [Fact]
    public void EveryAuthoredNpcAlternativeHasACompletePinnedCombatProfile()
    {
        using MissionFixture f = new();
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var types = MissionCatalog
            .Read(catalog)
            .SelectMany(m => m.Definition.Waves)
            .SelectMany(w => w.Groups)
            .SelectMany(g => g.Types)
            .Distinct()
            .ToArray();
        Assert.NotEmpty(types);
        foreach (int type in types)
        {
            var profile = new NpcProfileCatalog().Read(catalog, type);
            Assert.True(profile.Defense.Structure > 0, $"Mission NPC {type} has no hull health.");
            Assert.All(profile.ElectronicWarfare, ability => Assert.InRange(ability.Chance, 0, 1));
        }
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 1)]
    [InlineData(50, 0.5)]
    [InlineData(100, 1)]
    public void SentryWebChancesNormalizeLegacyPercentagesWithoutChangingShipProbabilities(double raw, double expected)
    {
        var effects = new HashSet<DogmaEffect> { DogmaEffect.NpcWebifier };
        var attributes = new Dictionary<DogmaAttributeId, double>
        {
            [DogmaAttributeId.EntityWebChance] = raw,
            [DogmaAttributeId.SpeedFactor] = -75,
        };
        Assert.Equal(expected, Assert.Single(NpcEwarRules.Profile(effects, attributes, sentry: true)).Chance);
        if (raw > 1)
        {
            Assert.Throws<InvalidDataException>(() => NpcEwarRules.Profile(effects, attributes));
        }
    }

    [Theory]
    [InlineData(1, 9)]
    [InlineData(2, 8)]
    [InlineData(3, 7)]
    [InlineData(4, 10)]
    public void EachAdmittedCombatLevelSpawnsTheReportRosterAndHasWorkingCatalogProfiles(int level, int expected)
    {
        using MissionFixture f = new(level, MissionRules.SecurityDivision);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedNpcStanding
                {
                    PilotId = MissionFixture.PilotId,
                    SourceId = 1000044,
                    Value = 8,
                }
            );
            world.SaveChanges();
        }
        var mission = f.Accept(f.Request());
        f.Missions.Arrive(mission.SystemId, mission.Position);
        using WorldDbContext check = new(f.Db.Path);
        using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath);
        var npcs = check.Set<WorldNpc>().Where(n => n.AnchorKind == NpcAnchorKind.Mission).ToArray();
        Assert.Equal(expected, npcs.Length);
        Assert.All(
            npcs,
            npc =>
            {
                var profile = new NpcProfileCatalog().Read(catalog, npc.TypeId);
                Assert.True(profile.Attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints) > 0);
                Assert.True(double.IsFinite(npc.X) && double.IsFinite(npc.Y) && double.IsFinite(npc.Z));
            }
        );
    }

    [Fact]
    public void BlockadeTriggerSpawnsOnceAndFinalTargetCompletesWithEarlierEnemiesStillAlive()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision);
        for (int i = 0; i < 3; i++)
        {
            var skipped = f.Accept(f.Request());
            f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Quit, skipped.Id);
        }
        var mission = f.Accept(f.Request());
        Assert.Equal(2924, mission.MissionId);
        f.Missions.Arrive(mission.SystemId, mission.Position);
        for (int wave = 0; wave < 4; wave++)
        {
            long trigger;
            using (WorldDbContext world = new(f.Db.Path))
            {
                Assert.Equal(wave, world.Set<SavedMission>().Find(mission.Id)!.Wave);
                trigger = world.Set<SavedMissionMember>().Single(m => m.MissionId == mission.Id && m.Wave == wave && m.Trigger).NpcId;
            }
            f.Kill(trigger);
            f.Missions.Arrive(mission.SystemId, mission.Position);
        }
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(34, world.Set<SavedMissionMember>().Count(m => m.MissionId == mission.Id));
            Assert.False(world.Set<SavedMission>().Find(mission.Id)!.ObjectiveComplete);
        }
        long final;
        using (WorldDbContext world = new(f.Db.Path))
        {
            final = world.Set<SavedMissionMember>().Single(m => m.MissionId == mission.Id && m.CompletesMission).NpcId;
        }
        f.Kill(final);
        Assert.True(f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ObjectiveComplete);
        f.Missions.Act(MissionFixture.PilotId, MissionFixture.AgentId, MissionAction.Complete, mission.Id);
        using WorldDbContext check = new(f.Db.Path);
        Assert.DoesNotContain(
            check.Set<WorldNpc>(),
            n => n.AnchorKind == NpcAnchorKind.Mission && n.DestroyedAt == null && n.RetiredAt == null
        );
    }

    [Fact]
    public void WarmEmptyAndPopulatedMissionViewsUseNoSqlAndInvalidateAfterCommittedAcceptance()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        Assert.Empty(f.Missions.Sites(30000142));
        // Warm the system owner's effect deadlines as well as the shared mission view.
        f.Missions.Arrive(30000142, new(0, 0, 0));
        using (var measure = SessionPerformance.Begin("empty mission scenes", _ => { }, force: true)!)
        {
            Assert.Empty(f.Missions.Sites(30000142));
            f.Missions.Arrive(30000142, new(0, 0, 0));
            Assert.Equal(0, measure.Commands);
            Assert.Equal(0, measure.Connections);
        }
        var mission = f.Accept(f.Request());
        Assert.Single(f.Missions.Sites(30000142));
        f.Missions.Arrive(30000142, mission.Position);
        var warmed = f.Missions.Sites(30000142);
        Assert.True(Assert.Single(warmed).Started);
        // Committed NPC creation invalidates effect deadlines; warm that new generation.
        f.Missions.Arrive(30000142, mission.Position);
        using var metrics = SessionPerformance.Begin("warm mission scenes", _ => { }, force: true)!;
        Assert.Same(warmed, f.Missions.Sites(30000142));
        f.Missions.Arrive(30000142, mission.Position);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void MissionViewsStayCoherentAfterRollbackAndExpireAtTheExactDeadline()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = f.Accept(f.Request());
        var before = f.Missions.Sites(mission.SystemId);
        using (WorldDbContext world = new(f.Db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            world.Set<SavedMission>().Find(mission.Id)!.State = MissionState.Quit;
            world.SaveChanges();
            transaction.Rollback();
        }
        Assert.Single(f.Missions.Sites(mission.SystemId));
        Assert.Equal(mission.ExpiresAt, f.Missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!.ExpiresAt);
        f.Clock.AdvanceWallTime(MissionRules.MissionLifetime);
        Assert.Empty(f.Missions.Sites(mission.SystemId));
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(MissionState.Failed, check.Set<SavedMission>().Find(mission.Id)!.State);
    }

    [Fact]
    public async Task ResidentCheckpointReopenRetainsMissionIdentityProgressAndRewards()
    {
        using MissionFixture f = new(division: MissionRules.SecurityDivision, live: true);
        var mission = f.Accept(f.Request());
        f.Missions.Arrive(mission.SystemId, mission.Position);
        long killed;
        using (WorldDbContext world = new(f.Db.Path))
        {
            killed = world.Set<SavedMissionMember>().OrderBy(m => m.NpcId).First().NpcId;
        }
        f.Kill(killed);
        await LiveWorld.Find(f.Db.Path)!.FlushAsync();
        f.StopResident();
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        using var reopened = Eden.Server.ServerServices.Build(new(f.Db.Path), f.Clock);
        var missions = reopened.GetRequiredService<IPilotMissions>();
        var saved = missions.Current(MissionFixture.PilotId, MissionFixture.AgentId)!;
        Assert.Equal(mission.Id, saved.Id);
        Assert.Equal(mission.Rewards, saved.Rewards);
        Assert.Equal(mission.ExpiresAt, saved.ExpiresAt);
        missions.Arrive(saved.SystemId, saved.Position);
        using WorldDbContext check = new(f.Db.Path);
        Assert.Equal(9, check.Set<SavedMissionMember>().Count());
        Assert.NotNull(check.Set<WorldNpc>().Find(killed)!.DestroyedAt);
        Assert.Equal(8, check.Set<WorldNpc>().Count(n => n.AnchorKind == NpcAnchorKind.Mission && n.DestroyedAt == null));
    }
}
