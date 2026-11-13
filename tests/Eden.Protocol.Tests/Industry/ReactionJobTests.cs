using System.Globalization;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Tests.Structures;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed class ReactionJobTests
{
    private const int Formula = 46166;
    private const int Product = 16650;
    private const int Input = 16643;
    private const int Fuel = 4051;
    private const int Athanor = 35835;
    private const int Reactor = 45537;

    [Fact]
    public void CompositeReactionReservesFormulaConsumesMaterialsAndDeliversOnceAfterPause()
    {
        using StructureTests.Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES(9,'Blueprint'),(4,'Material');
                INSERT OR IGNORE INTO ItemGroup VALUES(1888,'Composite Reaction Formulas',9),(428,'Intermediate Materials',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    (46166,'Composite Formula',1888),(16650,'Composite Output',428),(16643,'Composite Input',428);
                """
            );
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{0})",
                """{"securityStatus":0.4}"""
            );
            foreach (int type in new[] { Formula, Product, Input })
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{0},{1})",
                    type.ToString(CultureInfo.InvariantCulture),
                    """{"published":1,"marketGroupID":1,"volume":1}"""
                );
            }
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/blueprints',{0},{1})",
                JsonSerializer.Serialize(Formula.ToString(CultureInfo.InvariantCulture)),
                """
                {"blueprintTypeID":46166,"activities":{"reaction":{"time":3600,
                "materials":[{"typeID":16643,"quantity":10},{"typeID":4051,"quantity":5}],
                "products":[{"typeID":16650,"quantity":20}]}}}
                """
            );
        }

        var structure = f.Online(Athanor);
        long module = f.Item(Reactor);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(Fuel, 2_000), 2_000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        long formula = MoveToHangar(Formula, 1);
        long material = MoveToHangar(Input, 10);
        long fuel = MoveToHangar(Fuel, 5);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);

        var industry = f.Db.Services.GetRequiredService<IPilotIndustry>();
        Assert.Equal(Product, Assert.Single(industry.Facility(f.PilotId, structure.Id).ReactionProducts!));
        var request = new IndustryRequest(
            formula,
            Formula,
            structure.Id,
            1,
            Product,
            3600,
            new Dictionary<int, int> { [Input] = 10, [Fuel] = 5 },
            Activity: IndustryActivity.Reaction
        );
        Assert.Throws<InvalidDataException>(() => industry.Install(f.PilotId, request with { QuotedSeconds = 3599 }));
        var job = industry.Install(f.PilotId, request).Job;
        Assert.Equal(job.Id, industry.Blueprint(f.PilotId, formula).JobId);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, false);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(2));
        Assert.Equal(IndustryJobStatus.Paused, industry.Job(f.PilotId, job.Id).Status);
        Assert.Throws<InvalidDataException>(() => industry.Deliver(f.PilotId, job.Id, structure.SystemId));
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        f.Clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Equal(20, Assert.Single(industry.Deliver(f.PilotId, job.Id, structure.SystemId).Changes).Item.Quantity);
        Assert.Empty(industry.Deliver(f.PilotId, job.Id, structure.SystemId).Changes);
        Assert.Null(industry.Blueprint(f.PilotId, formula).JobId);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(material));
        Assert.Null(saved.Set<OwnedItem>().Find(fuel));
        Assert.Empty(saved.Set<SavedMarketUnlock>().Where(row => row.TypeId == Product));

        long MoveToHangar(int type, int quantity)
        {
            long id = f.Item(type, quantity);
            using WorldDbContext world = new(f.Db.Path);
            var item = world.Set<OwnedItem>().Find(id)!;
            item.LocationId = structure.Id;
            item.FlagId = (int)InventoryFlag.Hangar;
            world.SaveChanges();
            return id;
        }
    }
}
