using System.Text.Json.Nodes;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotTrainingTests
{
    [Fact]
    public async Task ConfiguredSpeedActivatesOnRestartAndRescheduledTrainingSurvivesCheckpoint()
    {
        const long pilot = 90000001;
        const int skill = 3327;
        Clock clock = new();
        using TestWorld fixture = new(clock: clock);
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore configuration = new(database, clock);
        PilotTrainingStore training = new(database, clock);
        TrainingTarget[] targets = [new(skill, 4)];
        var initial = configuration.Read();
        var legacy = JsonNode.Parse(configuration.Export(initial.Active.Id))!;
        legacy["settings"]!["progression"]!.AsObject().Remove("trainingSpeedMultiplier");
        Assert.Equal(1, ConfigurationValidation.Decode(legacy.ToJsonString()).Settings.Progression.TrainingSpeedMultiplier);

        TrainingEntry baseline;
        long pendingId;
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            baseline = Assert.Single(training.Save(pilot, targets, true).Queue);
            var settings = initial.Active.Draft.Settings;
            var pending = configuration.Save(
                initial.Generation,
                initial.Active.Draft with
                {
                    Settings = settings with { Progression = settings.Progression with { TrainingSpeedMultiplier = 2.5m } },
                },
                "training configuration test"
            );
            pendingId = pending.Pending!.Id;
            Assert.Equal(1, database.Settings.Progression.TrainingSpeedMultiplier);
            await live.FlushAsync();
        }

        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        WorldInitialization.Initialize(database, clock);
        Assert.Equal(pendingId, configuration.Read().Active.Id);
        Assert.Equal(2.5m, database.Settings.Progression.TrainingSpeedMultiplier);
        TrainingEntry resumed;
        using (var live = new LiveWorld(database, clock, combatObjects: false, simulationObjects: false))
        {
            Assert.Equal(baseline, Assert.Single(training.Advance(pilot).Queue));
            var paused = Assert.Single(training.Pause(pilot).Queue);
            Assert.Null(paused.EndTime);
            Assert.Equal(baseline.StartPoints + 30, paused.StartPoints);
            resumed = Assert.Single(training.Save(pilot, targets, true).Queue);
            Assert.Equal(paused.StartPoints, resumed.StartPoints);
            Assert.Equal(baseline.EndPoints, resumed.EndPoints);
            long duration = (long)decimal.Ceiling((resumed.EndPoints - resumed.StartPoints) * TimeSpan.TicksPerMinute / 37.5m);
            Assert.Equal(clock.Now + duration, resumed.EndTime);
            await live.FlushAsync();
        }

        clock.UtcNow = DateTimeOffset.FromFileTime(resumed.EndTime!.Value);
        WorldDatabase reopened = new(fixture.Path);
        WorldInitialization.Initialize(reopened, clock);
        using var restarted = new LiveWorld(reopened, clock, combatObjects: false, simulationObjects: false);
        training = new(reopened, clock);
        var completed = training.Advance(pilot);
        Assert.Empty(completed.Queue);
        Assert.Equal(skill, Assert.Single(completed.ChangedSkills));
        Assert.Empty(training.Advance(pilot).ChangedSkills);
        var completion = Assert.Single(training.History(pilot, 100), entry => entry.EventId == 53);
        Assert.Equal(resumed.EndTime, completion.Time);
        Assert.Equal(resumed.EndPoints, completion.Points);
    }

    [Fact]
    public void TrainingResumesOfflineAndCreditsEachLevelOnce()
    {
        using TestWorld db = new();
        var file = new WorldDatabase(db.Path);
        Clock clock = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var store = new PilotTrainingStore(file, clock);
        const long pilot = 90000001;
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<PilotSkill>().Find(pilot, 3327)!.Points = 0;
            world.SaveChanges();
        }
        TrainingTarget[] targets = [new(3327, 1), new(3327, 2)];
        var active = store.Save(pilot, targets, true);
        Assert.Equal(clock.UtcNow.AddSeconds(1000).UtcDateTime.ToFileTimeUtc(), active.Queue[0].EndTime);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Assert.Equal(active.Queue, store.Save(pilot, targets, true).Queue);
        var paused = store.Pause(pilot);
        Assert.Null(paused.Queue[0].EndTime);
        Assert.Equal(30, paused.Queue[0].StartPoints);
        clock.UtcNow = clock.UtcNow.AddDays(3);
        var resumed = new PilotTrainingStore(file, clock).Save(pilot, targets, true);
        Assert.Equal(clock.UtcNow.AddSeconds(880).UtcDateTime.ToFileTimeUtc(), resumed.Queue[0].EndTime);
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.Empty(store.Advance(pilot).Queue);
        Assert.Empty(store.Advance(pilot).ChangedSkills);
        Assert.Equal(2, store.History(pilot, 100).Count(e => e.EventId == 53));
        Assert.Equal(1415, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(pilot)).Points);
        Assert.Empty(store.Advance(90000002).Queue);
        Assert.Empty(store.History(90000002, 10));
        Assert.Equal(3, TaskTrackingTestData.Count(db, TaskEvents.TrainingStarted));
    }

    [Fact]
    public void InvalidEditRollsBackPartialPointsAndKeepsActiveQueue()
    {
        using TestWorld db = new();
        Clock clock = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var store = new PilotTrainingStore(new(db.Path), clock);
        const long pilot = 90000001;
        var initial = store.Save(pilot, [new(3327, 4)], true);
        clock.UtcNow = clock.UtcNow.AddMinutes(2);
        Assert.Throws<InvalidTrainingQueue>(() => store.Save(pilot, [new(3327, 5)], true));
        Assert.Throws<InvalidTrainingQueue>(() => store.Save(pilot, [new(3327, 4), new(3327, 5)], true));
        Assert.Throws<InvalidTrainingQueue>(() => store.Save(pilot, [new(3302, 1)], true));
        Assert.Equal(initial.Queue, store.Advance(pilot).Queue);
        Assert.Single(store.History(pilot, 10));
        Assert.Equal(8000, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(pilot)).Points);
    }

    [Fact]
    public void CompletedTrainingSendsOneVersionedBrainUpdateAndRetainsEarnedPoints()
    {
        using TestWorld db = new();
        Clock clock = new(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero));
        var training = new PilotTrainingStore(new(db.Path), clock);
        var pilot = db.Store.FindPilot(1000001)!;
        var skills = db.Services.GetRequiredService<IPilotSkillReader>();
        using var scope = db.Services.CreateScope();
        SkillHandler handler = new(
            skills,
            training,
            db.Services.GetRequiredService<IPilotSkillBooks>(),
            clock,
            new(
                skills,
                clock,
                db.Services.GetRequiredService<IPilotIndustrySkills>(),
                db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotJumpCloneSkills>(),
                db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotNavigationSkills>()
            ),
            new(clock, db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>()),
            scope.ServiceProvider.GetRequiredService<Eden.Protocol.Docking.FittingOperations>(),
            db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotCharacterSheet>(),
            db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotJumpCloneSkills>()
        );
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<PilotSkill>().Find(pilot.PilotId, 3327)!.Points = 0;
            world.SaveChanges();
        }
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), []),
            7,
            SignedSettings.Empty
        );
        context.Select(pilot);
        training.Save(pilot.PilotId, [new(3327, 1)], true);
        clock.UtcNow = clock.UtcNow.AddSeconds(1000);
        Assert.Empty(handler.Advance(context));
        Assert.Equal(
            new[] { "OnServerSkillsChanged", "OnServerBrainUpdated", "OnModuleAttributeChanges", "OnNewSkillQueueSaved" },
            context.Notifications.Select(n => n.Name)
        );
        var notification = Assert.Single(context.Notifications, n => n.Name == "OnServerBrainUpdated");
        var update = Assert.IsType<object?[]>(Assert.Single(notification.Arguments));
        Assert.Equal(clock.UtcNow.UtcDateTime.ToFileTimeUtc(), update[0]);
        Assert.Equal(3, Assert.IsType<object?[]>(BlueMarshal.Decode(Assert.IsType<byte[]>(update[1]))).Length);
        Assert.Equal(250, Assert.Single(skills.ReadSkills(pilot.PilotId)).Points);
        context.Notifications.Clear();
        Assert.Empty(handler.Advance(context));
        Assert.Empty(context.Notifications);
    }
}
