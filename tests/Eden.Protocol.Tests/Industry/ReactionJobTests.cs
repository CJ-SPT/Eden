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
    private const int Input = 16643;
    private const int Fuel = 4051;
    private const int Athanor = 35835;
    private const int CompositeReactor = 45537;
    private const int HybridReactor = 45538;
    private const int BiochemicalReactor = 45539;

    [Theory]
    [InlineData(46166, 16650, 1888, CompositeReactor)]
    [InlineData(46157, 30309, 1889, HybridReactor)]
    [InlineData(57496, 57459, 4097, BiochemicalReactor)]
    public void ReactorFamilyReservesFormulaConsumesMaterialsAndDeliversOnceAfterPause(int formulaType, int product, int group, int reactor)
    {
        using StructureTests.Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                INSERT OR IGNORE INTO ItemCategory VALUES(9,'Blueprint'),(4,'Material');
                INSERT OR IGNORE INTO ItemGroup VALUES({group},'Reaction Formulas',9),(428,'Intermediate Materials',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    ({formulaType},'Formula',{group}),({product},'Output',428),(16643,'Input',428);
                """
            );
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30001392',{0})",
                """{"securityStatus":0.4}"""
            );
            foreach (int type in new[] { formulaType, product, Input })
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/types',{0},{1})",
                    type.ToString(CultureInfo.InvariantCulture),
                    """{"published":1,"marketGroupID":1,"volume":1}"""
                );
            }
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/blueprints',{0},{1})",
                JsonSerializer.Serialize(formulaType.ToString(CultureInfo.InvariantCulture)),
                $$"""
                {"blueprintTypeID":{{formulaType}},"activities":{"reaction":{"time":3600,
                "materials":[{"typeID":16643,"quantity":10},{"typeID":4051,"quantity":5}],
                "products":[{"typeID":{{product}},"quantity":20}]
                } } }
                """
            );
        }

        var structure = f.Online(Athanor);
        long module = f.Item(reactor);
        int otherReactor = reactor == CompositeReactor ? BiochemicalReactor : CompositeReactor;
        long otherModule = f.Item(otherReactor);
        f.Structures.FitService(f.PilotId, structure.Id, module, StructureRules.FirstServiceSlot);
        f.Structures.FitService(f.PilotId, structure.Id, otherModule, StructureRules.FirstServiceSlot + 1);
        f.Structures.DepositFuel(f.PilotId, structure.Id, f.Item(Fuel, 5_000), 5_000);
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, otherModule, true);
        long formula = MoveToHangar(formulaType, 1);
        long material = MoveToHangar(Input, 10);
        long fuel = MoveToHangar(Fuel, 5);
        f.Db.Services.GetRequiredService<IStationDocking>().Dock(f.PilotId, f.ShipId, structure.Id);

        var industry = f.Db.Services.GetRequiredService<IPilotIndustry>();
        Assert.Empty(industry.Facility(f.PilotId, structure.Id).ReactionProducts!);
        var request = new IndustryRequest(
            formula,
            formulaType,
            structure.Id,
            1,
            product,
            3600,
            new Dictionary<int, int> { [Input] = 10, [Fuel] = 5 },
            Activity: IndustryActivity.Reaction
        );
        Assert.Throws<InvalidDataException>(() => industry.Install(f.PilotId, request));
        f.Structures.SetServiceOnline(f.PilotId, structure.Id, module, true);
        Assert.Equal(product, Assert.Single(industry.Facility(f.PilotId, structure.Id).ReactionProducts!));
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
        Assert.Empty(saved.Set<SavedMarketUnlock>().Where(row => row.TypeId == product));

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
