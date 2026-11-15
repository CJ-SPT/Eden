using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed partial class CargoContainerAssemblyTests
{
    [Theory]
    [InlineData(InventoryFlag.Unlocked, false)]
    [InlineData(InventoryFlag.Locked, false)]
    [InlineData(InventoryFlag.Unlocked, true)]
    [InlineData(InventoryFlag.Locked, true)]
    public void NativeItemMenuWalksFromContainerThroughItsParentsToTheDockedStation(InventoryFlag flag, bool inShip)
    {
        using Fixture f = new();
        var pilot = f.Context.Pilot;
        long container = f.Assembled(inShip ? SmallStandardContainer : StationContainer);
        if (inShip)
        {
            using var action = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin();
            var item = action.Set<OwnedItem>().Find(container)!;
            item.LocationId = pilot.ShipId;
            item.FlagId = (int)InventoryFlag.Cargo;
            action.Commit();
        }
        long child = f.Add(InventoryDefinitions.CivilianMinerType, 2, container, flag: flag);
        string inventory = f.Open(container);
        var row = Assert.Single(Assert.IsType<BlueList<InventoryRow>>(Success(f.Service("List", [], inventory))).Items);
        Assert.Equal(child, row.Id);
        Assert.Equal((int)flag, row.FlagId);

        // Build 3662168 ItemChecker.IsInAssetSafety / IsInTradeSession repeatedly
        // call GetInventoryFromId(locationID).GetItem(), whose remote read is GetSelfInvItem.
        long[] parents = inShip ? [container, pilot.ShipId, pilot.DockedLocationId] : [container, pilot.DockedLocationId];
        foreach (long parent in parents)
        {
            Assert.Equal(parent, row.LocationId);
            row = MenuParent(f, parent);
            Assert.Equal(parent, row.Id);
            Assert.NotEmpty(BlueMarshal.Encode(row));
        }
        Assert.Equal(pilot.SystemId, row.LocationId);
        Assert.Equal(pilot.StationTypeId, row.TypeId);
        Assert.Equal(pilot.Station.GroupId, row.GroupId);
        Assert.Equal(pilot.StationOwnerId, row.OwnerId);
        Assert.Empty(f.Context.Notifications);
        Assert.Equal(child, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).Id);
        Assert.Equal((int)flag, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).FlagId);
    }

    [Fact]
    public void StationMenuBindingIsLocalExpiresOnDepartureAndDoesNotExposeContainerChildren()
    {
        using Fixture f = new();
        long container = f.Assembled();
        long child = f.Add(InventoryDefinitions.CivilianMinerType, 1, container, flag: InventoryFlag.Unlocked);
        long station = f.Context.Pilot.DockedLocationId;
        var reference = Assert.IsType<BlueObjectReference>(Success(f.Service("GetInventoryFromId", [station, 0L])).ToBlue());
        string service = f.Context.ResolveReference(reference.Id)!;
        Assert.Equal("@hangar", service);
        var items = Assert.IsType<BlueList<InventoryRow>>(Success(f.Service("List", [], service))).Items;
        Assert.Contains(items, item => item.Id == container);
        Assert.DoesNotContain(items, item => item.Id == child);
        Assert.Same(PilotCallResult.Unsupported, f.Service("GetInventoryFromId", [(long)ContractAssetTestData.RemoteStation, 0L]));
        Assert.Same(PilotCallResult.Unsupported, f.Service("GetInventoryFromId", [station, 0L, 0L]));

        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, SpacePosition.Zero);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        Assert.Null(f.Context.ResolveReference(reference.Id));
        Assert.Same(PilotCallResult.Unsupported, f.Service("GetInventoryFromId", [station, 0L]));
    }

    [Fact]
    public void WarmStationMenuBindingsUseResidentSessionMetadataWithoutDatabaseCommands()
    {
        using Fixture f = new();
        long station = f.Context.Pilot.DockedLocationId;
        MenuParent(f, station);
        using var performance = SessionPerformance.Begin("station container menu ancestry", _ => { }, force: true)!;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            Assert.Equal(station, MenuParent(f, station).Id);
        }
        Assert.Equal(0, performance.Commands);
        Assert.Equal(0, performance.Connections);
        Assert.Equal(0, performance.FittingCalculations);
    }

    private static InventoryRow MenuParent(Fixture f, long parent)
    {
        var bound = Assert.IsType<BoundPilotReply>(
            Success(
                f.Service(
                    "MachoBindObject",
                    [
                        new object?[] { f.Context.Pilot.DockedLocationId, (long)f.Context.Pilot.Station.GroupId },
                        new object?[] { "GetInventoryFromId", new object?[] { parent, 0L } },
                    ]
                )
            )
        );
        var reference = Assert.IsType<BlueObjectReference>(bound.Reply.ToBlue());
        return Assert.IsType<InventoryRow>(Success(f.Service("GetSelfInvItem", [], f.Context.ResolveReference(reference.Id)!)));
    }
}
