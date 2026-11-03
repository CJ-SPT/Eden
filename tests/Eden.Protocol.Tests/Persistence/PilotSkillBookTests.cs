using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotSkillBookTests
{
    private const long Pilot = 90000001;
    private const int ShieldOperation = 3416;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public IPilotSkillBooks Books => Db.Services.GetRequiredService<IPilotSkillBooks>();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public long Book { get; }

        public Fixture(int quantity = 1)
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (3416,'Shield Operation',257);
                    INSERT INTO TypeAttribute VALUES (3416,275,1),(3416,180,165),(3416,181,166);
                    INSERT INTO SourceRecord VALUES ('client/types','3416','{{"volume":0.01,"published":1}}'),
                        ('client/stationoperations','14','{{"services":[7]}}'),
                        ('client/requiredskillsfortypes','3416','{{"3327":4}}');
                    UPDATE SourceRecord SET Data='{{"skills":[{{"typeID":3327,"level":4}},{{"typeID":3416,"level":5}}]}}'
                        WHERE Dataset='client/clonegrades';
                    """
                );
            }
            using (WorldDbContext world = new(Db.Path))
            {
                world.Add(new PilotWallet { PilotId = PilotSkillBookTests.Pilot, Cents = 2000000 });
                world.SaveChanges();
            }
            Book = Assert
                .Single(
                    Db.Services.GetRequiredService<IPilotMarket>()
                        .Buy(PilotSkillBookTests.Pilot, Pilot.StationId, [new(ShieldOperation, 500000, quantity)])
                        .Changes
                )
                .Item.Id;
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void PurchasedBookConsumesOneAndLearnsAtZeroWithoutBypassingTrainingRequirements()
    {
        using Fixture f = new(2);
        var result = f.Books.Inject(Pilot, [f.Book]);
        Assert.Equal(new[] { ShieldOperation }, result.TypeIds);
        var change = Assert.Single(result.Changes);
        Assert.Equal(2, change.Previous!.Quantity);
        Assert.Equal(1, change.Item.Quantity);
        Assert.Throws<InvalidDataException>(() => f.Books.Inject(Pilot, [f.Book]));
        Clock clock = new();
        var training = new PilotTrainingStore(new(f.Db.Path), clock);
        Assert.Throws<InvalidTrainingQueue>(() => training.Save(Pilot, [new(ShieldOperation, 1)], true));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            Assert.Equal(0, saved.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
            saved.Set<PilotSkill>().Find(Pilot, 3327)!.Points = TrainedSkill.PointsForLevel(1, 4);
            saved.SaveChanges();
        }
        var queue = training.Save(Pilot, [new(ShieldOperation, 1)], true);
        Assert.Equal(250, Assert.Single(queue.Queue).EndPoints);
        clock.Now = new DateTimeOffset(DateTime.FromFileTimeUtc(queue.Queue[0].EndTime!.Value));
        Assert.Contains(ShieldOperation, new PilotTrainingStore(new(f.Db.Path), clock).Advance(Pilot).ChangedSkills);
        _ = new SqliteWorld(f.Db.Path);
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(250, reopened.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
        Assert.Equal(1, reopened.Set<OwnedItem>().Find(f.Book)!.Quantity);
        Assert.Equal(1000000, reopened.Set<PilotWallet>().Find(Pilot)!.Cents);
        Assert.Equal(98, f.Db.Services.GetRequiredService<IPilotMarket>().Orders(Pilot).Single(o => o.TypeId == ShieldOperation).Remaining);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("miningHold")]
    [InlineData("module")]
    [InlineData("unpublished")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("empty")]
    public void InvalidBatchNeverConsumesTheValidBookOrAddsSkills(string rejection)
    {
        using Fixture f = new();
        long[] ids = [f.Book];
        using (WorldDbContext world = new(f.Db.Path))
        {
            var book = world.Set<OwnedItem>().Find(f.Book)!;
            switch (rejection)
            {
                case "foreign":
                    book.OwnerId = 90000002;
                    break;
                case "remote":
                    book.LocationId++;
                    break;
                case "miningHold":
                    book.LocationId = f.Pilot.ShipId;
                    book.FlagId = (int)InventoryFlag.GeneralMiningHold;
                    break;
                case "module":
                    ids = [f.Book, world.Set<OwnedItem>().First(i => i.TypeId == InventoryDefinitions.CivilianMinerType).Id];
                    break;
                case "duplicate":
                    ids = [f.Book, f.Book];
                    break;
                case "missing":
                    ids = [f.Book, long.MaxValue];
                    break;
                case "empty":
                    ids = [];
                    break;
            }
            world.SaveChanges();
        }
        if (rejection == "unpublished")
        {
            using CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data='{{\"volume\":0.01,\"published\":0}}' WHERE Dataset='client/types' AND Key='3416'"
            );
        }
        Assert.Throws<InvalidDataException>(() => f.Books.Inject(Pilot, ids));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(f.Book)!.Quantity);
        Assert.Null(saved.Set<PilotSkill>().Find(Pilot, ShieldOperation));
    }

    [Fact]
    public async Task CompetingInjectionsLearnOnlyOnceAndConsumeOnlyOneBook()
    {
        using Fixture f = new(2);
        var attempts = Enumerable
            .Range(0, 2)
            .Select(_ =>
                Task.Run(() =>
                {
                    try
                    {
                        f.Books.Inject(Pilot, [f.Book]);
                        return true;
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                })
            )
            .ToArray();
        Assert.Single(await Task.WhenAll(attempts), success => success);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(f.Book)!.Quantity);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
    }

    [Fact]
    public void SpaceInjectionRequiresCurrentCargoAndNativeCallRefreshesSkillAndInventoryCaches()
    {
        using Fixture f = new();
        f.Db.Store.SetPosition(f.Pilot.ShipId, SpacePosition.Zero);
        Assert.Throws<InvalidDataException>(() => f.Books.Inject(Pilot, [f.Book]));
        using (WorldDbContext saved = new(f.Db.Path))
        {
            var book = saved.Set<OwnedItem>().Find(f.Book)!;
            book.LocationId = f.Pilot.ShipId;
            book.FlagId = (int)InventoryFlag.Cargo;
            saved.SaveChanges();
        }
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new(), []),
            7,
            SignedSettings.Empty
        );
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] args = [new List<object?> { f.Book }];
        Assert.Same(PilotCallResult.Unsupported, router.CallWire(context, "dogmaIM", "InjectSkillIntoBrain", args, _ => { }));
        context.Select(pilot);
        Assert.Null(router.CallWire(context, "dogmaIM", "InjectSkillIntoBrain", args, _ => { }));
        Assert.Equal(new[] { "OnItemChange", "OnServerSkillsChanged" }, context.Notifications.Select(n => n.Name));
        var item = Assert.IsType<BlueConstructor>(context.Notifications[0].Arguments[0]);
        Assert.NotEmpty(BlueMarshal.Encode(item));
        var skills = Assert.IsType<Dictionary<long, object?>>(context.Notifications[1].Arguments[0]);
        Assert.NotEmpty(BlueMarshal.Encode(skills));
        Assert.Single(skills);
        Assert.True(skills.ContainsKey(ShieldOperation));
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Null(reopened.Set<OwnedItem>().Find(f.Book));
        Assert.Equal(0, reopened.Set<PilotSkill>().Find(Pilot, ShieldOperation)!.Points);
    }
}
