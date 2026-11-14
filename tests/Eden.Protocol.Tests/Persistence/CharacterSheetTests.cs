using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CharacterSheetTests
{
    private const long PilotId = 90000001;
    private const long WingId = 90000002;
    private static readonly CharacterAttributes FastShips = new(17, 17, 17, 27, 21);

    [Fact]
    public void NewlyCreatedCharacterReceivesIndependentDefaultsAndCanSaveBothTabs()
    {
        using TestWorld db = new();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long id = db.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new("Sheet Pilot", 1, 1, 1, 64, [1], [2], 18));
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(id));
        Assert.Equal("", sheet.ReadBiography(id));
        sheet.SaveBiography(id, "New explorer");
        Assert.True(sheet.Remap(id, FastShips).Changed);
        WorldInitialization.Open(db.Path);
        Assert.Equal("New explorer", sheet.ReadBiography(id));
        Assert.Equal(FastShips, sheet.ReadAttributes(id).Attributes);
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(PilotId));
        Assert.Equal("", sheet.ReadBiography(PilotId));
    }

    [Fact]
    public void FailedQueueReplanRollsBackAttributesRemapBalancePointsAndHistoryTogether()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var training = db.Services.GetRequiredService<IPilotTraining>();
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        var initial = training.Save(PilotId, [new(3327, 4), new(3327, 5)], true);
        var history = training.History(PilotId, 100);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Alpha;
            world.SaveChanges();
        }
        Assert.Throws<InvalidTrainingQueue>(() => sheet.Remap(PilotId, FastShips));
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(PilotId));
        Assert.Equal(initial.Queue, training.Advance(PilotId).Queue);
        Assert.Equal(history, training.History(PilotId, 100));
        Assert.Equal(8000, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(PilotId)).Points);
    }

    [Fact]
    public void BiographySurvivesReopenAndRejectsInvalidEditsWithoutChangingEitherPilot()
    {
        using TestWorld db = new();
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        const string biography = "<b>Explorer</b><br>星間旅行 — пилот 🚀";
        Assert.Equal("", sheet.ReadBiography(PilotId));
        sheet.SaveBiography(PilotId, biography);
        sheet.SaveBiography(WingId, "Wing's biography");
        using var reopened = ServerServices.Build(new(db.Path));
        var saved = reopened.GetRequiredService<IPilotCharacterSheet>();
        Assert.Equal(biography, saved.ReadBiography(PilotId));
        Assert.Equal("Wing's biography", saved.ReadBiography(WingId));
        Assert.Throws<InvalidDataException>(() => saved.SaveBiography(PilotId, new string('x', 2501)));
        Assert.Throws<InvalidDataException>(() => saved.SaveBiography(PilotId, "invalid\0text"));
        Assert.Throws<InvalidDataException>(() => saved.SaveBiography(123, "unknown"));
        Assert.Throws<InvalidDataException>(() => saved.ReadBiography(123));
        Assert.Equal(biography, saved.ReadBiography(PilotId));
        Assert.Equal("Wing's biography", saved.ReadBiography(WingId));
        saved.SaveBiography(PilotId, new string('x', 2500));
        Assert.Equal(2500, saved.ReadBiography(PilotId).Length);
        saved.SaveBiography(PilotId, "");
        Assert.Equal("", sheet.ReadBiography(PilotId));
        Assert.Equal(NeuralRemapState.Default, saved.ReadAttributes(PilotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemapBanksOldRateAndReplansActiveQueueAtomicallyAcrossReopen(bool resident)
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero));
        using TestWorld db = new(clock: clock, liveWorld: resident);
        var training = db.Services.GetRequiredService<IPilotTraining>();
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        ResetSkill(db);
        var old = training.Save(PilotId, [new(3327, 1), new(3327, 2)], true);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(1000).Ticks, old.Queue[0].EndTime);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(2));
        var remap = sheet.Remap(PilotId, FastShips);
        Assert.True(remap.Changed);
        Assert.Equal(30, remap.Training.Queue[0].StartPoints);
        Assert.Equal(clock.Now, remap.Training.Queue[0].StartTime);
        long duration = (long)Math.Ceiling(220d * TimeSpan.TicksPerMinute / 18.75);
        Assert.Equal(clock.Now + duration, remap.Training.Queue[0].EndTime);
        Assert.Equal(remap.Training.Queue[0].EndTime, remap.Training.Queue[1].StartTime);
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(WingId));
        if (resident)
        {
            var live = db.Services.GetRequiredService<LiveWorld>();
            Assert.Equal(FastShips, live.Economy.Peek<SavedCharacterSheet>(PilotId)!.Snapshot().Attributes);
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var saved = reopened.GetRequiredService<IPilotCharacterSheet>();
        var resumed = reopened.GetRequiredService<IPilotTraining>();
        Assert.Equal(FastShips, saved.ReadAttributes(PilotId).Attributes);
        Assert.Equal(2, saved.ReadAttributes(PilotId).BonusRemaps);
        Assert.Equal(remap.Training.Queue, resumed.Advance(PilotId).Queue);
        Assert.False(saved.Remap(PilotId, FastShips).Changed);
        Assert.Equal(remap.Training.Queue, resumed.Advance(PilotId).Queue);
        Assert.Throws<InvalidCharacterSheet>(() => saved.Remap(PilotId, FastShips with { Perception = 28 }));
        Assert.Equal(remap.Training.Queue, resumed.Advance(PilotId).Queue);
        clock.AdvanceWallTime(TimeSpan.FromHours(4));
        Assert.Empty(resumed.Advance(PilotId).Queue);
        Assert.Empty(resumed.Advance(PilotId).ChangedSkills);
        Assert.Equal(2, resumed.History(PilotId, 100).Count(e => e.EventId == 53));
        Assert.Equal(1415, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(PilotId)).Points);
    }

    [Fact]
    public void RemapPreservesPausedQueueAndCompletedLevelsAtTheBoundary()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        ResetSkill(db);
        var training = db.Services.GetRequiredService<IPilotTraining>();
        var sheet = db.Services.GetRequiredService<IPilotCharacterSheet>();
        training.Save(PilotId, [new(3327, 1), new(3327, 2)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1000));
        var remap = sheet.Remap(PilotId, FastShips);
        Assert.Equal(2, Assert.Single(remap.Training.Queue).Target.Level);
        Assert.Single(training.History(PilotId, 100), e => e.EventId == 53);
        training.Pause(PilotId);
        var paused = sheet.Remap(PilotId, CharacterAttributes.Default);
        Assert.Null(Assert.Single(paused.Training.Queue).StartTime);
        Assert.Null(Assert.Single(paused.Training.Queue).EndTime);
        Assert.Equal(1, sheet.ReadAttributes(PilotId).BonusRemaps);
    }

    [Fact]
    public void MigrationFromVersion38CreatesDefaultsWithoutTouchingPilotProgress()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldCharacterSheet; PRAGMA user_version=38");
        }
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var sheet = reopened.GetRequiredService<IPilotCharacterSheet>();
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(PilotId));
        Assert.Equal(NeuralRemapState.Default, sheet.ReadAttributes(WingId));
        Assert.Equal("", sheet.ReadBiography(PilotId));
        Assert.Equal(8000, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(PilotId)).Points);
        sheet.SaveBiography(PilotId, "Migrated");
        WorldInitialization.Open(db.Path);
        Assert.Equal("Migrated", sheet.ReadBiography(PilotId));
    }

    private static void ResetSkill(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        world.Set<PilotSkill>().Find(PilotId, 3327)!.Points = 0;
        world.SaveChanges();
    }
}
