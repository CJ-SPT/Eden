using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Trading;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Propulsion;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed class MutaplasmidTests
{
    [Theory]
    [InlineData(0, 80)]
    [InlineData(0.5, 100)]
    [InlineData(1, 120)]
    public void TypedMutationUsesExactBoundsAndRejectsInvalidRolls(double fraction, double expected)
    {
        Dictionary<DogmaAttributeId, double> basis = new() { [DogmaAttributeId.CpuLoad] = 100 };
        MutationRange[] ranges = [new(DogmaAttributeId.CpuLoad, 0.8, 1.2)];
        var result = MutationRules.Apply(basis, ranges, [new(DogmaAttributeId.CpuLoad, fraction)]);
        Assert.Equal(expected, result[DogmaAttributeId.CpuLoad], 8);
        Assert.Equal(100, basis[DogmaAttributeId.CpuLoad]);
        Assert.Throws<InvalidDataException>(() => MutationRules.Apply(basis, ranges, [new(DogmaAttributeId.CpuLoad, double.NaN)]));
        Assert.Throws<InvalidDataException>(() => MutationRules.Apply(basis, ranges, [new(DogmaAttributeId.CpuLoad, 1.1)]));
    }

    [Fact]
    public async Task NativeMutationPersistsUniqueAttributesThroughFittingTradeAndReopen()
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        f.Train(3435, 4);
        long source = f.Grant(526);
        long mutator = f.Grant(47699);
        long ship = f.ShipId;
        long station = f.StationId;
        var pilot = f.Db.Store.FindPilot(1000001)!;
        var partner = f.Db.Store.FindPilot(1000002)!;
        using (WorldDbContext disk = new(f.Db.Path))
        {
            disk.Ships.Find(partner.ShipId)!.StationId = checked((int)station);
            disk.SaveChanges();
        }
        MutatedItem mutation;
        using (LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { }))
        using (ResidentFitCache cache = new(f.Db.Services.GetRequiredService<WorldDatabase>()))
        {
            var service = f.Db.Services.GetRequiredService<IMutaplasmids>();
            using PilotCallContext context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new FlightSession(f.Db.Store, p, f.Clock, new(), []),
                1,
                new([], [])
            );
            context.Select(pilot);
            MutaplasmidHandler handler = new(service);
            var created = Assert.IsType<PilotCallResult.Success>(
                handler.Call(context, "dynamicItemService", "CreateDynamicItem", [mutator, source])
            );
            long id = Assert.IsType<long>(created.Reply.ToBlue());
            mutation = service.Read(id)!;
            Assert.Equal(47702, mutation.TypeId);
            Assert.Equal(source, mutation.SourceItemId);
            Assert.Equal(4, mutation.Modifiers.Count);
            Assert.Equal(3, context.Notifications.OfType<ItemChanged>().Count());
            Assert.Null(live.Economy.Inspect(w => w.Set<OwnedItem>().Find(source)));
            Assert.Null(live.Economy.Inspect(w => w.Set<OwnedItem>().Find(mutator)));
            Assert.Throws<InvalidDataException>(() => service.Create(f.PilotId, mutator, source));
            var wire = new MutatedItemReply(mutation).ToBlue();
            Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(wire)));
            using CatalogDbContext catalog = new(f.CatalogPath);
            var original = catalog.FittingAttributes(526);
            foreach (var pair in mutation.Modifiers)
            {
                Assert.InRange(pair.Value, 0, 1);
            }
            f.Fitting.Fit(f.PilotId, [id], station, (int)InventoryFlag.AutoFit);
            f.Fitting.SetOnline(f.PilotId, ship, id, true);
            var fitted = Assert.Single(f.Inventory.List(f.PilotId, ship), i => i.Id == id);
            var attributes = f.Fitting.ModuleAttributes(f.PilotId, fitted);
            Assert.Equal(mutation.Attributes[DogmaAttributeId.MaxRange], attributes[DogmaAttributeId.MaxRange]);
            Assert.Equal(mutation.Attributes[DogmaAttributeId.Cpu], f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CpuLoad], 2);
            Assert.Equal(10000, original[DogmaAttributeId.MaxRange]);
            using (var measured = SessionPerformance.Begin("warm mutated fit", _ => { }, force: true)!)
            {
                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(
                        attributes[DogmaAttributeId.MaxRange],
                        f.Fitting.ModuleAttributes(f.PilotId, fitted)[DogmaAttributeId.MaxRange]
                    );
                }
                Assert.Equal(0, measured.Commands);
                Assert.Equal(0, measured.Connections);
                Assert.Equal(0, measured.FittingCalculations);
            }
            f.Inventory.Move(f.PilotId, station, new([id], ship, (int)InventoryFlag.Hangar, null));
            var repackage = f.Db.Services.GetRequiredService<IPilotRepackaging>();
            Assert.Throws<InvalidDataException>(() => repackage.Repackage(f.PilotId, station, [new(id, station)]));
            var trades = f.Db.Services.GetRequiredService<IPlayerTradeStore>();
            var offer = trades.Quote(f.PilotId, station, new([id], station, (int)InventoryFlag.Hangar, null));
            trades.Settle(station, new(f.PilotId, 0, offer), new(partner.PilotId, 0, []));
            Assert.Equal(partner.PilotId, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(id)!.OwnerId));
            Assert.Equal(mutation.Attributes, service.Read(id)!.Attributes);
            await live.FlushAsync();
        }
        using (LiveWorld reopened = new(new(f.Db.Path), f.Clock, _ => { }))
        {
            var restored = f.Db.Services.GetRequiredService<IMutaplasmids>().Read(mutation.ItemId)!;
            Assert.Equal(mutation.Attributes, restored.Attributes);
            Assert.Equal(mutation.Modifiers, restored.Modifiers);
            Assert.Equal(partner.PilotId, reopened.Economy.Inspect(w => w.Set<OwnedItem>().Find(mutation.ItemId)!.OwnerId));
        }
    }

    [Fact]
    public void ModulesSharingResultTypeKeepIndependentSourceAttributesInOneFit()
    {
        using var f = TackleTests.Prepare(526, 527);
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        f.Train(3435, 4);
        long firstSource = f.Grant(526);
        long secondSource = f.Grant(527);
        long mutators = f.Grant(47699, 2);
        long ship = f.ShipId;
        long station = f.StationId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var service = f.Db.Services.GetRequiredService<IMutaplasmids>();
        var first = service.Create(f.PilotId, mutators, firstSource).Item;
        var second = service.Create(f.PilotId, mutators, secondSource).Item;
        Assert.Equal(first.TypeId, second.TypeId);
        // T1/T2 web strength intervals do not overlap, independently of the random rolls.
        Assert.NotEqual(first.Attributes[DogmaAttributeId.SpeedFactor], second.Attributes[DogmaAttributeId.SpeedFactor]);
        f.Fitting.Fit(f.PilotId, [first.ItemId, second.ItemId], station, (int)InventoryFlag.AutoFit);
        foreach (var mutation in new[] { first, second })
        {
            f.Fitting.SetOnline(f.PilotId, ship, mutation.ItemId, true);
            var item = f.Inventory.List(f.PilotId, ship).Single(i => i.Id == mutation.ItemId);
            Assert.Equal(
                mutation.Attributes[DogmaAttributeId.SpeedFactor],
                f.Fitting.ModuleAttributes(f.PilotId, item)[DogmaAttributeId.SpeedFactor]
            );
        }
        Assert.Equal(
            Math.Round(first.Attributes[DogmaAttributeId.Cpu], 2) + Math.Round(second.Attributes[DogmaAttributeId.Cpu], 2),
            f.Fitting.ShipAttributes(f.PilotId)[DogmaAttributeId.CpuLoad],
            2
        );
    }

    [Fact]
    public void MutatedWebAppliesItsOwnRangeAndStrengthToActualTackle()
    {
        using var f = TackleTests.Prepare(526);
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        f.Train(3435, 4);
        long source = f.Grant(526);
        long mutator = f.Grant(47699);
        long ship = f.ShipId;
        long station = f.StationId;
        long target = f.Db.Store.FindPilot(1000002)!.ShipId;
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var mutation = f.Db.Services.GetRequiredService<IMutaplasmids>().Create(f.PilotId, mutator, source).Item;
        f.Fitting.Fit(f.PilotId, [mutation.ItemId], station, (int)InventoryFlag.AutoFit);
        f.Fitting.SetOnline(f.PilotId, ship, mutation.ItemId, true);
        f.Db.Store.SetPosition(ship, new SpacePosition(2_000_000_000, 0, 0));
        f.Db.Store.SetPosition(target, new SpacePosition(2_000_001_000, 0, 0));
        var tackle = f.Db.Services.GetRequiredService<IPilotTackle>();
        var paid = tackle.Activate(
            f.PilotId,
            mutation.ItemId,
            DogmaEffect.StasisWebifier,
            new(target, TackleTargetKind.Ship),
            new HashSet<long> { target },
            f.Clock.Now
        );
        Assert.Equal(mutation.Attributes[DogmaAttributeId.MaxRange], paid.Module.Range);
        Assert.Equal(mutation.Attributes[DogmaAttributeId.SpeedFactor], paid.Module.WebPercent);
        Assert.Equal(paid.Module, Assert.Single(tackle.Incoming(target)).Module);
    }

    [Theory]
    [InlineData(47699)]
    [InlineData(47700)]
    [InlineData(47701)]
    [InlineData(47746)]
    [InlineData(47747)]
    [InlineData(47748)]
    public void PinnedMutatorQualitiesUsePublishedMappingsAndPreserveUnrolledAttributes(int type)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        var definition = catalog.Require(CatalogData.Client.Dynamicitemattributes, (long)type);
        var mapping = definition.InputOutputMapping!.Value[0];
        int input = checked((int)mapping.ApplicableTypes!.Value.Min());
        var basis = catalog.FittingAttributes(input);
        long source = f.Grant(input);
        long mutator = f.Grant(type);
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        var result = f.Db.Services.GetRequiredService<IMutaplasmids>().Create(f.PilotId, mutator, source).Item;
        Assert.Equal(mapping.ResultingType, result.TypeId);
        foreach (var range in definition.AttributeIDs!)
        {
            var attribute = (DogmaAttributeId)int.Parse(range.Key, System.Globalization.CultureInfo.InvariantCulture);
            double expected =
                basis[attribute]
                * (range.Value.Min!.Value + result.Modifiers[attribute] * (range.Value.Max!.Value - range.Value.Min.Value));
            Assert.Equal(expected, result.Attributes[attribute]);
        }
        Assert.Equal(basis[DogmaAttributeId.StructureHitPoints], result.Attributes[DogmaAttributeId.StructureHitPoints]);
        Assert.True(live.Economy.Inspect(w => w.Set<OwnedItem>().Find(result.ItemId)!.Singleton));
    }

    [Theory]
    [InlineData("wrongOwner")]
    [InlineData("cargo")]
    [InlineData("incompatible")]
    [InlineData("space")]
    public void RejectedMutationLeavesBothInputsAndNoOutput(string reason)
    {
        using Fixture f = new();
        PassiveCatalogFixture.LoadAbyssal(f.CatalogPath);
        long source = f.Grant(reason == "incompatible" ? 621 : 526, cargo: reason == "cargo");
        long mutator = f.Grant(47699);
        if (reason == "space")
        {
            f.Db.Store.SetPosition(f.ShipId, new(1, 2, 3));
        }
        using LiveWorld live = new(new(f.Db.Path), f.Clock, _ => { });
        if (reason == "wrongOwner")
        {
            using var tx = live.Economy.Begin(new(pilots: [f.PilotId]));
            tx.Set<OwnedItem>().Find(source)!.OwnerId = f.Db.Store.FindPilot(1000002)!.PilotId;
            tx.Commit();
        }
        var service = f.Db.Services.GetRequiredService<IMutaplasmids>();
        Assert.Throws<InvalidDataException>(() => service.Create(f.PilotId, mutator, source));
        Assert.Equal(1, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(source)!.Quantity));
        Assert.Equal(1, live.Economy.Inspect(w => w.Set<OwnedItem>().Find(mutator)!.Quantity));
        Assert.Empty(live.Economy.Inspect(w => w.Filter<SavedMutatedItem>(_ => true).ToArray()));
    }
}
