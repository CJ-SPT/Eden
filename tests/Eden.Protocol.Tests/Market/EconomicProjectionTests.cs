using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class EconomicProjectionTests
{
    private const long Pilot = 90000001;
    private const int Skill = 3300;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyTransactionProjectsOnlyReferencedDependenciesAndPreservesRollback(bool commit)
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin())
        {
            action.Set<PilotWallet>().Add(new() { PilotId = Pilot, Cents = 100 });
            action
                .Set<PilotSkill>()
                .Add(
                    new()
                    {
                        PilotId = Pilot,
                        TypeId = Skill,
                        Points = 123,
                    }
                );
            action.Commit();
        }
        List<string> commands = [];
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            world.Database.OpenConnection();
            using var metrics = SessionPerformance.Begin("selective projection", _ => { }, force: true, command: commands.Add)!;
            using var transaction = world.Database.BeginTransaction();
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(100, world.Set<PilotWallet>().AsNoTracking().Single(w => w.PilotId == Pilot).Cents);
            world.Set<PilotWallet>().Where(w => w.PilotId == Pilot).ExecuteUpdate(set => set.SetProperty(w => w.Cents, 200));
            Assert.Equal(200, world.Set<PilotWallet>().AsNoTracking().Single(w => w.PilotId == Pilot).Cents);
            Assert.DoesNotContain(commands, sql => sql.Contains("WorldSkill", StringComparison.Ordinal));
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            Assert.True(metrics.Commands <= 8, $"Expected bounded projection, got {metrics.Commands} SQL commands.");
            if (commit)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }
        }
        Assert.True(live.Economy.Mappings[typeof(PilotSkill)].HasChanges);
        Assert.Equal(commit ? 200 : 100, live.Economy.Inspect(action => action.Set<PilotWallet>().Find(Pilot)!.Cents));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NamedSavepointRollbackReprojectsRemovedWorkWithoutOverwritingEarlierSqlWrites(bool readAgain)
    {
        using TestWorld fixture = new(liveWorld: true);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin())
        {
            action.Set<PilotWallet>().Add(new() { PilotId = Pilot, Cents = 100 });
            action
                .Set<PilotSkill>()
                .Add(
                    new()
                    {
                        PilotId = Pilot,
                        TypeId = Skill,
                        Points = 123,
                    }
                );
            action.Commit();
        }
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            using var transaction = world.Database.BeginTransaction();
            world.Set<PilotWallet>().Where(w => w.PilotId == Pilot).ExecuteUpdate(set => set.SetProperty(w => w.Cents, 200));
            transaction.CreateSavepoint("earlier");
            Assert.Equal(123, world.Set<PilotSkill>().AsNoTracking().Single(s => s.PilotId == Pilot && s.TypeId == Skill).Points);
            transaction.CreateSavepoint("later");
            world
                .Set<PilotSkill>()
                .Where(s => s.PilotId == Pilot && s.TypeId == Skill)
                .ExecuteUpdate(set => set.SetProperty(s => s.Points, 999));
            transaction.RollbackToSavepoint("earlier");
            if (readAgain)
            {
                Assert.Equal(123, world.Set<PilotSkill>().AsNoTracking().Single(s => s.PilotId == Pilot && s.TypeId == Skill).Points);
                Assert.Equal(200, world.Set<PilotWallet>().AsNoTracking().Single(w => w.PilotId == Pilot).Cents);
            }
            transaction.ReleaseSavepoint("earlier");
            transaction.Commit();
        }
        Assert.Equal(123, live.Economy.Inspect(action => action.Set<PilotSkill>().Find(Pilot, Skill)!.Points));
        Assert.Equal(200, live.Economy.Inspect(action => action.Set<PilotWallet>().Find(Pilot)!.Cents));
    }
}
