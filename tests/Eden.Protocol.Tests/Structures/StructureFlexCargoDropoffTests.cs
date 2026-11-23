using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Fixtures;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Structures.StructureTests.Fixture;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureFlexCargoDropoffTests
{
    [Theory]
    [InlineData(JumpBridgeRules.Ansiblex)]
    [InlineData(CapitalTravelRules.Pharolux)]
    [InlineData(CapitalTravelRules.Tenebrex)]
    public async Task NativeDropOffFuelsPrivateFlexStructuresWithWholeStacksAcrossRestart(int structureType)
    {
        const long DepositorAccount = 1000002;
        const int NitrogenFuelBlock = 4051;
        const int Tritanium = 34;
        const int DepositRange = 10_000;
        using Fixture f = new();
        WorldDatabase database = new(f.Db.Path);
        PassiveCatalogFixture.LoadCapitalTravel(database.CatalogPath);
        using LiveWorld live = new(database, f.Clock, _ => { });
        var depositor = f.Db.Store.FindPilot(DepositorAccount)!;
        Assert.NotEqual(f.CorporationId, depositor.CorpId);
        var inventory = f.Db.Services.GetRequiredService<IPilotInventory>();

        long structureId,
            existing,
            tooLarge,
            first,
            exceedsRemaining,
            second,
            ozone,
            minerals;
        using (var setup = live.Economy.Begin(new(systems: [Fixture.SystemId], pilots: [depositor.PilotId])))
        {
            structureId = StructureRules.FirstStructureId + setup.NextItemId();
            setup.Add(
                new SavedStructure
                {
                    Id = structureId,
                    Name = "Private navigation site",
                    TypeId = structureType,
                    CorporationId = f.CorporationId,
                    SystemId = Fixture.SystemId,
                    State = StructureState.Fitting,
                    StateStartedAt = f.Clock.Now,
                    DeployedAt = f.Clock.Now,
                    PublicAccess = false,
                    X = Fixture.Point.X,
                    Y = Fixture.Point.Y,
                    Z = Fixture.Point.Z,
                }
            );
            existing = Stock(f.CorporationId, structureId, StructureRules.FuelBayFlag, NitrogenFuelBlock, 5_996);
            tooLarge = Cargo(NitrogenFuelBlock, 5);
            first = Cargo(NitrogenFuelBlock, 2);
            exceedsRemaining = Cargo(NitrogenFuelBlock, 3);
            second = Cargo(NitrogenFuelBlock, 2);
            ozone = Cargo((int)HoldCargoType.LiquidOzone, 7);
            minerals = Cargo(Tritanium, 11);
            setup.Commit();

            long Cargo(int type, int quantity) => Stock(depositor.PilotId, depositor.ShipId, (int)InventoryFlag.Cargo, type, quantity);

            long Stock(long owner, long location, int flag, int type, int quantity)
            {
                long id = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = owner,
                        LocationId = location,
                        FlagId = flag,
                        TypeId = type,
                        Quantity = quantity,
                    }
                );
                return id;
            }
        }
        var structure = Assert.IsType<StructureSnapshot>(f.Structures.Read(f.PilotId, structureId));
        Assert.Equal(StructureState.Fitting, structure.State);
        Assert.False(structure.Profile.Dockable);
        Assert.Throws<InvalidDataException>(() => f.Structures.Inventory(depositor.PilotId, structureId));

        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var factory = scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
        long session = 70;
        List<object?> items = [tooLarge, first, exceedsRemaining, ozone, second, minerals];
        Place(DepositRange + 1);
        using (var beyond = Select())
        {
            Assert.IsType<PilotCallResult.Failure>(DropOff(beyond));
            Assert.Empty(Changes(beyond));
        }
        Assert.Equal(existing, Assert.Single(f.Structures.Inventory(f.PilotId, structureId)).Id);

        Place(DepositRange);
        using (var sender = Select())
        {
            Assert.Equal(new object?[] { first, second }, Moved(DropOff(sender)));
            var changes = Changes(sender).ToArray();
            Assert.Equal(new[] { first, second }, changes.Select(change => change.Item.Id).Order());
            Assert.All(
                changes,
                change =>
                {
                    Assert.Equal("OnItemChange", change.Name);
                    Assert.Equal(depositor.PilotId, change.Item.OwnerId);
                    Assert.Equal((long)InventoryLocation.Graveyard, change.Item.LocationId);
                    Assert.Equal((int)InventoryFlag.AutoFit, change.Item.FlagId);
                    Assert.Equal(2, change.Item.Quantity);
                    Assert.Equal(depositor.ShipId, change.Previous[InventoryColumn.Location]);
                    Assert.Equal((long)InventoryFlag.Cargo, change.Previous[InventoryColumn.Flag]);
                    Assert.DoesNotContain(InventoryColumn.Owner, change.Previous.Keys);
                    Assert.NotEmpty(BlueMarshal.Encode(change.Arguments));
                }
            );
            Assert.Empty(Moved(DropOff(sender)));
            Assert.Equal(changes, Changes(sender));
        }
        AssertStock();

        await live.FlushAsync();
        live.Dispose();
        WorldInitialization.Initialize(database, f.Clock);
        using LiveWorld reopened = new(database, f.Clock, _ => { });
        using var replay = Select();
        Assert.Empty(Moved(DropOff(replay)));
        Assert.Empty(Changes(replay));
        AssertStock();

        void AssertStock()
        {
            var fuel = f.Structures.Inventory(f.PilotId, structureId);
            Assert.Equal(new[] { (existing, 5_996), (first, 2), (second, 2) }, fuel.Select(item => (item.Id, item.Quantity)));
            Assert.All(
                fuel,
                item => Assert.Equal(((long)f.CorporationId, 172, NitrogenFuelBlock), (item.OwnerId, item.FlagId, item.TypeId))
            );
            Assert.Empty(inventory.List(depositor.PilotId, structureId));
            var remaining = inventory.List(depositor.PilotId, depositor.ShipId);
            Assert.DoesNotContain(remaining, item => item.Id == first || item.Id == second);
            Assert.Equal(5, Assert.Single(remaining, item => item.Id == tooLarge).Quantity);
            Assert.Equal(3, Assert.Single(remaining, item => item.Id == exceedsRemaining).Quantity);
            Assert.Equal(7, Assert.Single(remaining, item => item.Id == ozone).Quantity);
            Assert.Equal(11, Assert.Single(remaining, item => item.Id == minerals).Quantity);
        }

        void Place(double surfaceDistance) =>
            f.Db.Store.SetPosition(
                depositor.ShipId,
                structure.Position + new SpacePosition(structure.Profile.Radius + depositor.Flight.ShipRadius + surfaceDistance, 0, 0),
                Fixture.SystemId
            );

        PilotCallContext Select()
        {
            var pilot = f.Db.Store.FindPilot(DepositorAccount)!;
            Assert.NotNull(pilot.Position);
            PilotCallContext context = new(DepositorAccount, pilot.Account, [pilot], factory.Create, session++, SignedSettings.Empty);
            context.Select(pilot);
            context.Flight.Snapshot();
            return context;
        }

        PilotCallResult DropOff(PilotCallContext context) =>
            router.Call(
                context,
                "structureCargoDelivery",
                "DropOffItems",
                BlueMarshal.Decode(BlueMarshal.Encode(new object?[] { items, depositor.ShipId, structureId })),
                _ => { }
            );

        static IEnumerable<ItemChanged> Changes(PilotCallContext context) =>
            context.Notifications.Concat(context.NotificationsBeforeFlight).OfType<ItemChanged>();

        static List<object?> Moved(PilotCallResult result) =>
            Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply)));
    }
}
