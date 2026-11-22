using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Fittings;

public sealed class ResidentFittingSelectionTests
{
    private const long Owner = 90000001;
    private const long OtherOwner = 91000001;
    private const long Ship = 91000002;
    private const long Station = 60003760;
    private const long FirstItem = InventoryDefinitions.FirstOwnedItemId + 1000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FittedAndLoadedItemsKeepExistingOrderAndSeePrivateMutations(bool ownerBucket)
    {
        using TestWorld fixture = new();
        using LiveWorld live = new(new WorldDatabase(fixture.Path), new TestClock(), _ => { });
        Seed(live, ownerBucket ? 0 : 200, ownerBucket ? 200 : 0);
        using var action = live.Economy.Begin();
        var items = action.Set<OwnedItem>();
        items.Find(FirstItem + 1)!.LocationId = Station;
        items.Find(FirstItem + 4)!.LocationId = Ship;
        action.Remove(items.Find(FirstItem + 3)!);
        items.Find(FirstItem + 8)!.LocationId = FirstItem + 2;
        items.Find(FirstItem + 6)!.Quantity = 17;
        action.Add(new SavedContractEscrow { ItemId = FirstItem + 10, ContractId = 1 });
        var addedModule = action.Add(Item(20, Ship, InventoryFlag.MediumSlot0)).Entity;
        var addedCharge = action.Add(Item(21, addedModule.Id, InventoryFlag.Cargo)).Entity;

        using var metrics = SessionPerformance.Begin("resident fitting selection", _ => { }, force: true)!;
        var fitted = action.FittedItems(Owner, Ship);
        Assert.Equal(ControlFitted(action), fitted);
        Assert.Equal(new[] { FirstItem + 2, FirstItem + 9, FirstItem + 4, addedModule.Id }, fitted.Select(row => row.Id));

        long[] ids = fitted.Select(row => row.Id).ToArray();
        var loaded = action.LoadedFittingItems(Owner, ids);
        Assert.Equal(ControlLoaded(action, ids), loaded);
        Assert.Equal(new[] { FirstItem + 6, FirstItem + 11, FirstItem + 8, addedCharge.Id }, loaded.Select(row => row.Id));
        Assert.Same(addedCharge, loaded[^1]);
        Assert.Equal(17, loaded[0].Quantity);
        Assert.Empty(action.LoadedFittingItems(Owner, []));
        Assert.Equal(0, metrics.Commands);
    }

    private static OwnedItem[] ControlFitted(WorldAction action) =>
        action
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == Owner
                && i.LocationId == Ship
                && (
                    (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                    || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                )
            )
            .ToArray();

    private static OwnedItem[] ControlLoaded(WorldAction action, long[] modules) =>
        action.Set<OwnedItem>().Where(i => i.OwnerId == Owner && Enumerable.Contains(modules, i.LocationId)).ToArray();

    private static OwnedItem Item(int offset, long location, InventoryFlag flag, long owner = Owner) =>
        new()
        {
            Id = FirstItem + offset,
            TypeId = 34,
            OwnerId = owner,
            LocationId = location,
            FlagId = (int)flag,
            Quantity = 1,
        };

    private static void Seed(LiveWorld live, int cargo, int foreign)
    {
        using var action = live.Economy.Begin();
        action.Add(new InventoryOwner { Id = OtherOwner });
        action.Add(Item(1, Ship, InventoryFlag.MediumSlot0));
        action.Add(Item(2, Ship, InventoryFlag.HighSlot0));
        action.Add(Item(3, Ship, InventoryFlag.RigSlot0));
        action.Add(Item(4, Station, InventoryFlag.LowSlot0));
        action.Add(Item(5, Ship, InventoryFlag.LowSlot0, OtherOwner));
        action.Add(Item(6, FirstItem + 2, InventoryFlag.Cargo));
        action.Add(Item(7, FirstItem + 2, InventoryFlag.Cargo, OtherOwner));
        action.Add(Item(8, Station, InventoryFlag.Cargo));
        action.Add(Item(9, Ship, InventoryFlag.LowSlot7));
        action.Add(Item(10, FirstItem + 2, InventoryFlag.Cargo));
        action.Add(Item(11, FirstItem + 9, InventoryFlag.Cargo));
        for (int i = 0; i < cargo; i++)
        {
            action.Add(Item(100 + i, Station, InventoryFlag.Cargo));
        }
        for (int i = 0; i < foreign; i++)
        {
            action.Add(Item(10_000 + i, Ship, InventoryFlag.Cargo, OtherOwner));
            action.Add(Item(20_000 + i, FirstItem + 2, InventoryFlag.Cargo, OtherOwner));
        }
        action.Commit();
    }
}
