using System.Text.Json.Nodes;
using Eden.Data.World.Agents;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Configuration;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Reprocessing;
using Eden.Protocol.Tests.Agents;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Industry;
using Eden.Protocol.Tests.PilotSessions;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class WorldTuningTests
{
    [Theory]
    [InlineData("resources", "oreYieldMultiplier", false)]
    [InlineData("resources", "iceYieldMultiplier", false)]
    [InlineData("resources", "gasYieldMultiplier", false)]
    [InlineData("rewards", "bountyMultiplier", true)]
    [InlineData("rewards", "missionIskMultiplier", true)]
    [InlineData("rewards", "missionLpMultiplier", true)]
    [InlineData("rewards", "missionStandingMultiplier", true)]
    [InlineData("progression", "killSkillPointMultiplier", true)]
    [InlineData("progression", "miningSkillPointMultiplier", true)]
    [InlineData("industry", "manufacturingSpeed", false)]
    [InlineData("industry", "researchSpeed", false)]
    [InlineData("industry", "copyingSpeed", false)]
    [InlineData("industry", "inventionSpeed", false)]
    [InlineData("industry", "reprocessingEfficiency", false)]
    [InlineData("structures", "anchoringSpeed", false)]
    [InlineData("structures", "onliningSpeed", false)]
    [InlineData("structures", "unanchoringSpeed", false)]
    public void ImportAndCommandsShareBoundsForEveryRate(string group, string field, bool reward)
    {
        using TestWorld db = new();
        WorldConfigurationStore store = new(new(db.Path), TimeProvider.System);
        var initial = store.Read();
        var document = JsonNode.Parse(store.Export(initial.Active.Id))!;
        foreach (decimal invalid in new[] { reward ? -0.001m : 0.099m, 100.001m })
        {
            document["settings"]![group]![field] = invalid;
            string json = document.ToJsonString();
            Assert.Throws<InvalidDataException>(() => store.Import(initial.Generation, json, "invalid"));
            Assert.Throws<InvalidDataException>(() => store.Validate(ConfigurationValidation.Decode(json)));
        }
        document["settings"]![group]![field] = reward ? 0m : 0.1m;
        store.Validate(ConfigurationValidation.Decode(document.ToJsonString()));
        document["settings"]![group]![field] = 100m;
        store.Validate(ConfigurationValidation.Decode(document.ToJsonString()));
        Assert.Equal(initial, store.Read());
        Assert.Single(store.History());
    }

    [Fact]
    public void InvalidPendingRateRetainsActivePoliciesAndItsHistoricalDocument()
    {
        using TestWorld db = new();
        WorldDatabase database = new(db.Path);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var original = store.Read();
        string invalid = ConfigurationValidation.Encode(
            original.Active.Draft with
            {
                Settings = original.Active.Draft.Settings with { Industry = new(ManufacturingSpeed: 0) },
            }
        );
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new SavedConfigurationRevision
                {
                    Id = 2,
                    Author = "invalid fixture",
                    Document = invalid,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("UPDATE WorldConfigurationState SET PendingRevisionId=2");
        }
        WorldConfigurationStore.Activate(database);
        var rejected = store.Read();
        Assert.Equal(original.Active.Id, rejected.Active.Id);
        Assert.Equal(original.Active.Draft.Settings, database.Settings);
        Assert.Null(rejected.Pending);
        Assert.Contains("Revision 2 rejected", rejected.ActivationFailure);
        Assert.Equal(invalid, store.Export(2));
    }

    [Fact]
    public void LegacyDocumentsDefaultToOneAndEveryRateRejectsInvalidValues()
    {
        var legacy = ConfigurationValidation.Decode("""{"settings":{"resources":{"renewBelts":false},"progression":{}}}""");
        Assert.Equal(new ResourcePolicy(false), legacy.Settings.Resources);
        Assert.Equal(new RewardPolicy(), legacy.Settings.Rewards);
        Assert.Equal(new IndustryPolicy(), legacy.Settings.Industry);
        Assert.Equal(1, legacy.Settings.Progression.KillSkillPointMultiplier);
        Assert.Equal(1, legacy.Settings.Progression.MiningSkillPointMultiplier);
        WorldTuningRules.Validate(legacy.Settings);
        foreach (decimal value in new[] { 0.1m, 1m, 100m })
        {
            WorldTuningRules.ValidateRate(value);
            WorldTuningRules.ValidateRate(value, true);
        }
        WorldTuningRules.ValidateRate(0, true);
        foreach (decimal value in new[] { -1m, 0m, 0.099m, 100.001m, decimal.MaxValue })
        {
            Assert.Throws<InvalidDataException>(() => WorldTuningRules.ValidateRate(value));
        }
        Assert.Throws<InvalidDataException>(() => WorldTuningRules.ValidateRate(-0.001m, true));
        Assert.Throws<InvalidDataException>(() => WorldTuningRules.ValidateRate(100.001m, true));
    }

    [Fact]
    public void RestartExportImportRestoreAndInvalidDraftsPreserveWorldIsolationAndHistory()
    {
        using TestWorld db = new();
        using TestWorld other = new();
        WorldDatabase database = new(db.Path);
        WorldConfigurationStore store = new(database, TimeProvider.System);
        var original = store.Read();
        string originalDocument = store.Export(original.Active.Id);
        var settings = original.Active.Draft.Settings with
        {
            Resources = new(OreYieldMultiplier: 2, IceYieldMultiplier: 3, GasYieldMultiplier: 4),
            Rewards = new(5, 6, 7, 8),
            Progression = new(KillSkillPointMultiplier: 9, MiningSkillPointMultiplier: 2.5m),
            Industry = new(10, 11, 12, 13, 14),
        };
        var pending = store.Save(original.Generation, original.Active.Draft with { Settings = settings }, "tuning-test");
        Assert.Equal(1, database.Settings.Resources.OreYieldMultiplier);
        Assert.Throws<InvalidOperationException>(() => store.Save(original.Generation, pending.Pending!.Draft, "stale"));
        Assert.Equal(1, other.Services.GetRequiredService<IWorldTuning>().Settings.Resources.OreYieldMultiplier);
        WorldConfigurationStore.Activate(database);
        Assert.Equal(settings, db.Services.GetRequiredService<IWorldTuning>().Settings);
        WorldConfigurationStore.LoadActive(new(db.Path));
        var active = store.Read();
        string exported = store.Export(active.Active.Id);
        Assert.Equal(settings, ConfigurationValidation.Decode(exported).Settings);
        Assert.Equal(originalDocument, store.Export(original.Active.Id));
        var invalid = JsonNode.Parse(exported)!;
        invalid["settings"]!["resources"]!["oreYieldMultiplier"] = 0;
        Assert.Throws<InvalidDataException>(() => store.Import(active.Generation, invalid.ToJsonString(), "invalid"));
        invalid["settings"]!["resources"]!["oreYieldMultiplier"] = 2;
        invalid["settings"]!["industry"] = null;
        Assert.Throws<InvalidDataException>(() => store.Import(active.Generation, invalid.ToJsonString(), "invalid"));
        Assert.Equal(active, store.Read());
        var restored = store.Restore(active.Generation, original.Active.Id, "restore");
        Assert.Equal(settings, database.Settings);
        WorldConfigurationStore.Activate(database);
        Assert.Equal(original.Active.Draft.Settings, database.Settings);
        var imported = store.Import(store.Read().Generation, exported, "import");
        Assert.Equal(settings, imported.Pending!.Draft.Settings);
        Assert.True(restored.Pending!.Id < imported.Pending.Id);
    }

    [Fact]
    public void ScaledRewardsRoundOnceAndKeepBountiesAndSkillPointsIndependent()
    {
        Assert.Equal(2, WorldTuningRules.Reward(1, 1.5m));
        Assert.Equal(2, WorldTuningRules.Reward(5, 0.5m));
        Assert.Equal(2, WorldTuningRules.Reward(5, 0.5m, true));
        Assert.Equal(15, NpcSkillPointRewards.FromBounty(300_000, 2));
        Assert.Equal(0, NpcSkillPointRewards.FromBounty(300_000, 0));
        Assert.Throws<OverflowException>(() => WorldTuningRules.Reward(long.MaxValue, 100));
        var reward = new MissionRewards(101, 11, 0.02, 0.5);
        Assert.Equal(new MissionRewards(202, 5, 1, 1), WorldTuningRules.Mission(reward, new(1, 2, 0.5m, 100)));
        Assert.Equal(new MissionRewards(0, 0, 0, 0), WorldTuningRules.Mission(reward, new(0, 0, 0, 0)));
        Assert.Equal(-0.1, WorldTuningRules.Mission(reward with { FactionFraction = -0.1 }, new()).FactionFraction);
    }

    [Theory]
    [InlineData(0, 0, 15)]
    [InlineData(2, 600000, 15)]
    [InlineData(3, 900000, 15)]
    public void FatalShotsScaleIskAndOriginalBountySpOnceAndSurviveReopen(int factor, long cents, long sp)
    {
        using PilotCombatTests.Fixture f = new();
        Activate(f.Db, settings => settings with { Rewards = new(factor), Progression = new(KillSkillPointMultiplier: 2) });
        f.AlmostDefeated();
        var shot = f.Fire();
        Assert.True(shot.Destroyed);
        Assert.Equal(cents, shot.Reward?.AmountCents ?? 0);
        Assert.Throws<InvalidDataException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(cents, saved.Set<PilotWallet>().Sum(w => w.Cents));
        var points = Assert.Single(saved.Set<SavedFreeSkillPoints>());
        Assert.Equal(sp, points.Points);
        Assert.Equal(0, points.Revision);
        Assert.Equal(factor == 0 ? 0 : 1, saved.Set<NpcBounty>().Count());
        WorldConfigurationStore.LoadActive(new(f.Db.Path));
        Assert.Equal(factor, new WorldDatabase(f.Db.Path).Settings.Rewards.BountyMultiplier);
    }

    [Fact]
    public void ScaledGrossBountyIsTaxedBeforeTheNetWalletCredit()
    {
        using PilotCombatTests.Fixture f = new();
        var pilot = f.Db.Store.FindPilot(1000001)!;
        f.Db.Store.SetPosition(pilot.ShipId, null);
        PlayerCorporationTests.Prepare(f.Db);
        f.Db.Services.GetRequiredService<IPlayerCorporations>().Create(pilot.PilotId, PlayerCorporationTests.Details());
        f.Db.Store.SetPosition(pilot.ShipId, f.Npc.Position + new Eden.Gameplay.Flight.SpacePosition(1000, 0, 0), f.Npc.SystemId);
        Activate(f.Db, settings => settings with { Rewards = new(100) });
        long before;
        using (WorldDbContext world = new(f.Db.Path))
        {
            before = world.Set<PilotWallet>().Find(pilot.PilotId)!.Cents;
        }
        f.AlmostDefeated();
        var reward = f.Fire().Reward!;
        Assert.Equal(30_000_000, reward.AmountCents);
        Assert.Equal(3_000_000, reward.TaxCents);
        Assert.Equal(before + 27_000_000, reward.BalanceCents);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(7, Assert.Single(saved.Set<SavedFreeSkillPoints>()).Points);
    }

    [Fact]
    public void OverflowDuringScaledSettlementRollsBackTheKillWalletAndSkillPoints()
    {
        using PilotCombatTests.Fixture f = new();
        Activate(f.Db, settings => settings with { Rewards = new(2), Progression = new(KillSkillPointMultiplier: 2) });
        f.AlmostDefeated();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(new PilotWallet { PilotId = 90000001, Cents = long.MaxValue });
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => f.Fire());
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<WorldNpc>().Find(f.Npc.Id)!.DestroyedAt);
        Assert.Empty(saved.Set<NpcBounty>());
        Assert.Empty(saved.Set<WorldWreck>());
        Assert.Empty(saved.Set<SavedFreeSkillPoints>());
        Assert.Equal(long.MaxValue, saved.Set<PilotWallet>().Find(90000001L)!.Cents);
    }

    [Fact]
    public void SavedMissionTermsRemainWhileNewOffersUseTheActivatedRates()
    {
        using MissionFixture f = new();
        var offer = f.Request();
        Activate(f.Db, settings => settings with { Rewards = new(1, 2, 3, 4) });
        Assert.Equal(offer.Rewards, f.Request().Rewards);
        f.Accept(offer);
        using (WorldDbContext world = new(f.Db.Path))
        {
            Assert.Equal(offer.Rewards.IskCents, world.Set<SavedMission>().Find(offer.Id)!.IskCents);
        }
        // A different pilot receives a fresh offer under the new world policy.
        var next = f.Missions.Act(90000002, MissionFixture.AgentId, MissionAction.Request).Offer!;
        Assert.Equal(offer.Rewards.IskCents * 2, next.Rewards.IskCents);
        Assert.Equal(offer.Rewards.LoyaltyPoints * 3, next.Rewards.LoyaltyPoints);
        Assert.Equal(offer.Rewards.CorporationFraction * 4, next.Rewards.CorporationFraction, 10);
    }

    [Fact]
    public void ManufacturingUsesScaledClientBonusesRejectsOldQuotesAndRetainsInstalledDeadlines()
    {
        using IndustryTests.Fixture f = new();
        var job = f.Store.Install(f.Pilot.PilotId, f.Request()).Job;
        Activate(f.Db, settings => settings with { Industry = new(ManufacturingSpeed: 2) });
        Assert.Equal(job.EndTime, Assert.Single(f.Store.Jobs(f.Pilot.PilotId, true)).EndTime);
        var bonuses = f.Store.CharacterBonuses(f.Pilot.PilotId);
        Assert.Contains(bonuses, b => b.SkillId == null && b.Attribute == DogmaAttributeId.ManufactureTimeMultiplier && b.Value == -50);
        f.Store.Cancel(f.Pilot.PilotId, job.Id, f.Pilot.SystemId);
        long before = f.Quantity(34);
        Assert.Throws<InvalidDataException>(() => f.Store.Install(f.Pilot.PilotId, f.Request()));
        Assert.Equal(before, f.Quantity(34));
        var scaled = f.Store.Install(f.Pilot.PilotId, f.Request() with { QuotedSeconds = 144 }).Job;
        Assert.Equal(144, scaled.DurationSeconds);
        Assert.Equal(job.OutputQuantity, scaled.OutputQuantity);
        Assert.Equal(before - 27, f.Quantity(34));
        Assert.Equal(1, new WorldDatabase(f.Db.Path).Settings.Industry.ResearchSpeed);
    }

    [Theory]
    [InlineData(IndustryActivity.ResearchMaterial)]
    [InlineData(IndustryActivity.ResearchTime)]
    [InlineData(IndustryActivity.Copying)]
    [InlineData(IndustryActivity.Invention)]
    public void ScienceSpeedChangesDurationWithoutChangingOutputsMaterialsOrProbability(IndustryActivity activity)
    {
        bool invention = activity == IndustryActivity.Invention;
        var inputs = new ScienceInputs(new(RunsRemaining: invention ? 1 : -1), 100, 10, 0, [], [], BaseProbability: 0.4);
        var request = new IndustryRequest(
            1,
            1,
            1,
            1,
            null,
            100,
            new Dictionary<int, int>(),
            Activity: activity,
            LicensedRuns: activity == IndustryActivity.Copying ? 1 : 0
        );
        var normal = ScienceRules.Plan(request, inputs);
        var bonuses = WorldTuningRules.IndustryBonuses(new(ResearchSpeed: 2, CopyingSpeed: 2, InventionSpeed: 2));
        var scaled = ScienceRules.Plan(request with { QuotedSeconds = 50 }, inputs with { Bonuses = bonuses });
        Assert.Equal(50, scaled.DurationSeconds);
        Assert.Equal(normal.Materials, scaled.Materials);
        Assert.Equal(normal.OutputRuns, scaled.OutputRuns);
        Assert.Equal(normal.MaterialEfficiency, scaled.MaterialEfficiency);
        Assert.Equal(normal.TimeEfficiency, scaled.TimeEfficiency);
        Assert.Equal(normal.Probability, scaled.Probability);
        Assert.Equal(normal.CostMultiplier, scaled.CostMultiplier);
        Assert.Throws<InvalidDataException>(() => ScienceRules.Plan(request, inputs with { Bonuses = bonuses }));
    }

    [Theory]
    [InlineData(0.1, 5, 5)]
    [InlineData(2, 100, 100)]
    [InlineData(100, 100, 100)]
    public void ReprocessingScalesBeforeOutputRoundingAndNeverCreatesExtraMaterial(double factor, int efficiency, int quantity)
    {
        var quote = ReprocessingRules.Quote(new(1, 1, 100, 100, 0.5, [], [new(34, 100)], (decimal)factor));
        Assert.Equal(efficiency / 100d, quote.Efficiency, 10);
        Assert.Equal(quantity, Assert.Single(quote.Recoverables).Quantity);
        Assert.True(quote.Recoverables[0].Unrecoverable >= 0);
    }

    internal static void Activate(TestWorld db, Func<WorldSettings, WorldSettings> change)
    {
        WorldDatabase database = new(db.Path);
        WorldConfigurationStore configuration = new(database, TimeProvider.System);
        var state = configuration.Read();
        configuration.Save(state.Generation, state.Active.Draft with { Settings = change(state.Active.Draft.Settings) }, "test");
        WorldConfigurationStore.Activate(database);
    }
}
