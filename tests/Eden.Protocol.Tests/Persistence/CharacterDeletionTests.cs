using Eden.Gameplay.Characters;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.World.Characters;
using Eden.World.Contracts;
using Eden.World.Corporations;
using Eden.World.Industry;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CharacterDeletionTests
{
    private const long Account = 1000001;
    private const long Pilot = 90000001;

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void TypedReadinessUsesTheExactDeadline(long offset, bool ready)
    {
        long deadline = CharacterDeletionRules.ReadyAt(1);
        Assert.Equal(1 + TimeSpan.FromMinutes(10).Ticks, deadline);
        CharacterDeletionState state = new(deadline, null);
        if (ready)
        {
            CharacterDeletionRules.RequireReady(state, deadline + offset);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => CharacterDeletionRules.RequireReady(state, deadline + offset));
        }
        Assert.Throws<InvalidDataException>(() => CharacterDeletionRules.RequireReady(null, deadline));
        Assert.Throws<InvalidDataException>(() => CharacterDeletionRules.RequireReady(state with { DeletedAt = deadline }, deadline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparationAndDeletionPersistReleaseSlotsAndKeepHistoricalIdentity(bool liveWorld)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock, liveWorld: liveWorld);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        NewCharacter character = new("Second Pilot", 1, 1, 1, 64, [1], [2], 18);
        long second = creation.Create(Account, character);
        creation.Create(Account, character with { Name = "Third Pilot" });
        db.Store.SetPosition(db.Store.FindPilot(Account, Pilot)!.ShipId, new SpacePosition(1, 2, 3));
        using (WorldDbContext training = new(db.Path))
        {
            training.Add(
                new QueuedLevel
                {
                    PilotId = Pilot,
                    Position = 0,
                    TypeId = 3300,
                    Level = 1,
                }
            );
            training.SaveChanges();
        }
        long ready = store.Prepare(Account, Pilot);
        clock.AdvanceWallTime(TimeSpan.FromMinutes(1));
        Assert.Equal(ready, store.Prepare(Account, Pilot));
        Assert.Equal(new(ready, null), store.Read(Account, Pilot));
        Assert.Throws<InvalidDataException>(() => creation.Create(Account, character with { Name = "Fourth Pilot" }));
        Assert.Throws<InvalidDataException>(() => store.Delete(Account, Pilot));
        if (!liveWorld)
        {
            WorldInitialization.Initialize(new(db.Path));
            Assert.Equal(new(ready, null), store.Read(Account, Pilot));
        }
        clock.AdvanceWallTime(TimeSpan.FromMinutes(9));
        store.Delete(Account, Pilot);
        Assert.Null(db.Store.FindPilot(Account, Pilot));
        Assert.Equal(2, db.Store.ListPilots(Account).Count);
        long replacement = creation.Create(Account, character with { Name = "Replacement Pilot" });
        Assert.True(replacement > second);
        Assert.Equal(3, db.Store.ListPilots(Account).Count);
        Assert.Equal(CharacterNameResult.Taken, creation.ValidateName("Eden Pilot"));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(clock.Now, world.Set<SavedCharacterDeletion>().Find(Pilot)!.DeletedAt);
        Assert.NotNull(world.Pilots.Find(Pilot));
        Assert.NotEmpty(world.Ships.Where(s => s.OwnerId == Pilot));
        Assert.Empty(world.Set<QueuedLevel>().Where(q => q.PilotId == Pilot));
        Assert.Empty(world.Positions.Where(p => world.Ships.Any(s => s.OwnerId == Pilot && s.Id == p.ShipId)));
        Assert.NotNull(db.Store.FindPilot(1000002));
        if (!liveWorld)
        {
            var reopened = WorldInitialization.Open(db.Path);
            Assert.Null(reopened.FindPilot(Account, Pilot));
            Assert.Equal(3, reopened.ListPilots(Account).Count);
        }
    }

    [Fact]
    public async Task ResidentDeletionSurvivesCheckpointAndShutdown()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        using (LiveWorld live = new(new(db.Path), clock, _ => { }))
        {
            store.Prepare(Account, Pilot);
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(new(db.Path));
        Assert.NotNull(store.Read(Account, Pilot));
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        using (LiveWorld live = new(new(db.Path), clock, _ => { }))
        {
            store.Delete(Account, Pilot);
        }
        Assert.Null(WorldInitialization.Open(db.Path).FindPilot(Account, Pilot));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(clock.Now, world.Set<SavedCharacterDeletion>().Find(Pilot)!.DeletedAt);
        Assert.Equal(0, world.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public void Version92MigrationPreservesPilotsAndReopensIdempotently()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldCharacterDeletion; PRAGMA user_version=92");
        }
        WorldInitialization.Initialize(new(db.Path));
        WorldInitialization.Initialize(new(db.Path));
        using WorldDbContext reopened = new(db.Path);
        Assert.Equal(2, reopened.Pilots.Count());
        Assert.Empty(reopened.Set<SavedCharacterDeletion>());
        Assert.Equal(WorldMigration.CurrentVersion, reopened.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(601)]
    public void Version93MigrationShortensPendingWaitFromOriginalRequestAndPreservesDeletedRecords(int elapsedMinutes)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        long requestedAt = clock.Now;
        long oldDeadline = requestedAt + TimeSpan.FromHours(10).Ticks;
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(new SavedCharacterDeletion { PilotId = Pilot, ReadyAt = oldDeadline });
            world.Add(
                new SavedCharacterDeletion
                {
                    PilotId = 90000002,
                    ReadyAt = oldDeadline,
                    DeletedAt = requestedAt,
                }
            );
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("PRAGMA user_version=93");
        }
        clock.AdvanceWallTime(TimeSpan.FromMinutes(elapsedMinutes));
        WorldInitialization.Initialize(new(db.Path), clock);
        WorldInitialization.Initialize(new(db.Path), clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        long ready = requestedAt + TimeSpan.FromMinutes(10).Ticks;
        Assert.Equal(new(ready, null), store.Read(Account, Pilot));
        Assert.Equal(ready, store.Prepare(Account, Pilot));
        using WorldDbContext reopened = new(db.Path);
        var deleted = reopened.Set<SavedCharacterDeletion>().Find(90000002L)!;
        Assert.Equal(oldDeadline, deleted.ReadyAt);
        Assert.Equal(requestedAt, deleted.DeletedAt);
        if (elapsedMinutes >= 10)
        {
            store.Delete(Account, Pilot);
            Assert.Null(db.Store.FindPilot(Account, Pilot));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => store.Delete(Account, Pilot));
            Assert.NotNull(db.Store.FindPilot(Account, Pilot));
        }
    }

    [Fact]
    public void FailedDeletionRollsBackTrainingAndSpaceRemoval()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        long ship = db.Store.FindPilot(Account, Pilot)!.ShipId;
        db.Store.SetPosition(ship, new SpacePosition(1, 2, 3));
        using WorldDbContext world = new(db.Path);
        world.Add(
            new QueuedLevel
            {
                PilotId = Pilot,
                Position = 0,
                TypeId = 3300,
                Level = 1,
            }
        );
        world.SaveChanges();
        store.Prepare(Account, Pilot);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        world.Database.ExecuteSqlRaw(
            "CREATE TRIGGER reject_biomass BEFORE UPDATE ON WorldCharacterDeletion BEGIN SELECT RAISE(ABORT,'test rollback'); END"
        );
        Assert.Throws<DbUpdateException>(() => store.Delete(Account, Pilot));
        Assert.Null(world.Set<SavedCharacterDeletion>().Find(Pilot)!.DeletedAt);
        Assert.Single(world.Set<QueuedLevel>().Where(q => q.PilotId == Pilot));
        Assert.NotNull(world.Positions.Find(ship));
        Assert.NotNull(db.Store.FindPilot(Account, Pilot));
    }

    [Theory]
    [InlineData("corporation")]
    [InlineData("market")]
    [InlineData("industry")]
    [InlineData("contract")]
    [InlineData("auction")]
    public void OutstandingObligationsRejectPreparationAndAreRecheckedAtFinalDeletion(string obligation)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        long ready = store.Prepare(Account, Pilot);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        using WorldDbContext world = new(db.Path);
        switch (obligation)
        {
            case "corporation":
                world.Add(
                    new SavedCorporation
                    {
                        Id = 98000001,
                        CeoId = Pilot,
                        CreatorId = Pilot,
                    }
                );
                world.Pilots.Find(Pilot)!.CorporationId = 98000001;
                break;
            case "market":
                world.Add(
                    new SavedPersonalOrder
                    {
                        PilotId = Pilot,
                        Entered = 1,
                        Remaining = 1,
                        PriceCents = 1,
                    }
                );
                break;
            case "industry":
                world.Add(
                    new IndustryJob
                    {
                        PilotId = Pilot,
                        Runs = 1,
                        OutputQuantity = 1,
                        DurationSeconds = 1,
                        EndTime = clock.Now,
                        Status = IndustryJobStatus.Installed,
                    }
                );
                break;
            case "contract":
                world.Add(
                    new SavedContract
                    {
                        Id = 1,
                        IssuerId = Pilot,
                        Status = ContractStatus.Outstanding,
                    }
                );
                break;
            case "auction":
                world.Add(
                    new SavedContract
                    {
                        Id = 1,
                        IssuerId = 90000002,
                        Status = ContractStatus.Outstanding,
                    }
                );
                world.Add(new SavedContractBid { ContractId = 1, BidderId = Pilot });
                break;
        }
        world.SaveChanges();
        Assert.Throws<InvalidDataException>(() => store.Delete(Account, Pilot));
        Assert.Equal(new(ready, null), store.Read(Account, Pilot));
        store.Cancel(Account, Pilot);
        Assert.Throws<InvalidDataException>(() => store.Prepare(Account, Pilot));
        Assert.Null(store.Read(Account, Pilot));
        Assert.NotNull(db.Store.FindPilot(Account, Pilot));
    }

    [Fact]
    public void CancelIsIdempotentAndRestartStartsANewDeadline()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        long first = store.Prepare(Account, Pilot);
        store.Cancel(Account, Pilot);
        store.Cancel(Account, Pilot);
        Assert.Null(store.Read(Account, Pilot));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        Assert.Equal(first + TimeSpan.TicksPerSecond, store.Prepare(Account, Pilot));
    }

    [Fact]
    public void PendingAndDeletedStateOverridesStaleSelectionRoster()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        using var scope = db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = db.Store.FindPilot(Account)!;
        using PilotCallContext context = new(
            Account,
            pilot.Account,
            [pilot],
            services.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = services.GetRequiredService<PilotCallRouter>();
        var store = services.GetRequiredService<ICharacterDeletion>();
        store.Prepare(Account, Pilot);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "charUnboundMgr", "SelectCharacterID", new object?[] { Pilot }, _ => { })
        );
        Assert.False(context.Selected);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        store.Cancel(Account, Pilot);
        Assert.IsType<PilotCallResult.Success>(
            router.Call(context, "charUnboundMgr", "SelectCharacterID", new object?[] { Pilot }, _ => { })
        );
        Assert.True(context.Selected);
        context.LogOff();
        store.Prepare(Account, Pilot);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        store.Delete(Account, Pilot);
        Assert.Contains(context.Roster, p => p.PilotId == Pilot);
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "charUnboundMgr", "SelectCharacterID", new object?[] { Pilot }, _ => { })
        );
        Assert.False(context.Selected);
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData("PrepareCharacterForDelete")]
    [InlineData("CancelCharacterDeletePrepare")]
    [InlineData("DeleteCharacter")]
    public void ProtocolRejectsForeignMalformedAndSelectedRequestsWithoutWrites(string method)
    {
        using TestWorld db = new();
        using var scope = db.Services.CreateScope();
        var services = scope.ServiceProvider;
        var pilot = db.Store.FindPilot(Account)!;
        using PilotCallContext context = new(
            Account,
            pilot.Account,
            [pilot],
            services.GetRequiredService<FlightSessionFactory>().Create,
            42,
            SignedSettings.Empty
        );
        var router = services.GetRequiredService<PilotCallRouter>();
        foreach (
            object? arguments in new object?[]
            {
                null,
                Array.Empty<object?>(),
                new object?[] { Pilot, Pilot },
                new object?[] { "90000001" },
                new object?[] { -1L },
                new object?[] { 90000002L },
                new object?[] { long.MaxValue },
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(router.Call(context, "charUnboundMgr", method, arguments, _ => { }));
        }
        Assert.IsType<PilotCallResult.Failure>(
            router.Call(context, "charUnboundMgr", method, new object?[] { Pilot }, _ => { }, new() { ["unexpected"] = true })
        );
        context.Select(pilot);
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "charUnboundMgr", method, new object?[] { Pilot }, _ => { }));
        using WorldDbContext world = new(db.Path);
        Assert.Empty(world.Set<SavedCharacterDeletion>());
        Assert.Equal(2, world.Pilots.Count());
        Assert.Equal(2, world.Ships.Count());
    }

    [Fact]
    public void StoreRejectsOtherAccountsAndDeletedCharactersWithoutChangingState()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<ICharacterDeletion>();
        foreach (long account in new[] { 1000002L, long.MaxValue })
        {
            Assert.Throws<InvalidDataException>(() => store.Read(account, Pilot));
            Assert.Throws<InvalidDataException>(() => store.Prepare(account, Pilot));
            Assert.Throws<InvalidDataException>(() => store.Cancel(account, Pilot));
            Assert.Throws<InvalidDataException>(() => store.Delete(account, Pilot));
        }
        store.Prepare(Account, Pilot);
        clock.AdvanceWallTime(CharacterDeletionRules.PreparationDelay);
        store.Delete(Account, Pilot);
        Assert.Throws<InvalidDataException>(() => store.Prepare(Account, Pilot));
        Assert.Throws<InvalidDataException>(() => store.Cancel(Account, Pilot));
        Assert.Throws<InvalidDataException>(() => store.Delete(Account, Pilot));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(clock.Now, world.Set<SavedCharacterDeletion>().Find(Pilot)!.DeletedAt);
    }
}
