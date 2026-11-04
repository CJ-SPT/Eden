using Eden.Gameplay.Characters;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Clock = Eden.Protocol.Tests.Fixtures.TestClock;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotTrainingTests
{
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
                db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotNavigationSkills>()
            ),
            new(clock, db.Services.GetRequiredService<Eden.Gameplay.Propulsion.IPilotPropulsion>()),
            scope.ServiceProvider.GetRequiredService<Eden.Protocol.Docking.FittingOperations>(),
            db.Services.GetRequiredService<Eden.Gameplay.Characters.IPilotCharacterSheet>()
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
