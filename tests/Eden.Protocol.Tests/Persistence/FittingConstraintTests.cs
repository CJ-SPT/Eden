using System.Text.Json;
using System.Text.Json.Nodes;
using Eden.Data.Catalog;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class FittingConstraintTests
{
    [Fact]
    public void HullModifiedFittedLimitAllowsTheSecondModuleAndRejectsTheThirdAtomically()
    {
        using Fixture fixture = new();
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            SetAttribute(catalog, 377, DogmaAttributeId.MaxGroupFitted, 1);
            SetAttribute(catalog, 601, DogmaAttributeId.MediumSlots, 3);
            SetAttribute(catalog, 601, DogmaAttributeId.MaxGroupFitted, 2);
            SetRecord(catalog, "client/types", 601, new { published = 1, techLevel = 1 });
            const int effect = 900031;
            SetRecord(catalog, "client/typedogma", 601, new { dogmaEffects = new[] { new { effectID = effect, isDefault = 0 } } });
            SetRecord(
                catalog,
                "client/dogmaeffects",
                effect,
                new
                {
                    effectID = effect,
                    effectCategory = (int)PassiveEffectPhase.Fitted,
                    modifierInfo = new[]
                    {
                        new
                        {
                            func = "LocationGroupModifier",
                            domain = "shipID",
                            groupID = (int)PassiveModuleGroup.ShieldExtender,
                            modifiedAttributeID = (int)DogmaAttributeId.MaxGroupFitted,
                            modifyingAttributeID = (int)DogmaAttributeId.MaxGroupFitted,
                            operation = (int)DogmaOperation.PostAssignment,
                        },
                    },
                }
            );
        }
        long first = fixture.Fit(377, online: false);
        long second = fixture.Fit(377, online: false);
        long third = fixture.Grant(377, 2);
        var before = fixture.Inventory.List(fixture.PilotId, fixture.StationId).OrderBy(item => item.Id).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Fitting.Fit(fixture.PilotId, [third], fixture.StationId, (int)InventoryFlag.AutoFit)
        );
        Assert.Equal(before, fixture.Inventory.List(fixture.PilotId, fixture.StationId).OrderBy(item => item.Id));
        Assert.Equal(new[] { first, second }, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).Select(item => item.Id).Order());
        _ = WorldInitialization.Open(fixture.Db.Path);
        Assert.Equal(2, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).Count);
        Assert.Equal(2, fixture.Inventory.List(fixture.PilotId, fixture.StationId).Single(item => item.Id == third).Quantity);
    }

    [Fact]
    public void LowerOnlineLimitOfflinesExcessAndRejectedOnlinePreservesStateAcrossRestart()
    {
        using Fixture fixture = new();
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            SetAttribute(catalog, 601, DogmaAttributeId.MediumSlots, 3);
            SetAttribute(catalog, 377, DogmaAttributeId.MaxGroupOnline, 2);
            SetAttribute(catalog, 8427, DogmaAttributeId.MaxGroupOnline, 1);
        }
        long first = fixture.Fit(377);
        long second = fixture.Fit(377);
        long restrictive = fixture.Grant(8427);
        var changes = fixture.Fitting.Fit(fixture.PilotId, [restrictive], fixture.StationId, (int)InventoryFlag.AutoFit);
        Assert.Contains(changes, change => change.Item.Id == second && !change.Item.Online && change.Previous!.Online);
        var before = fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id).ToArray();
        Assert.True(before.Single(item => item.Id == first).Online);
        Assert.False(before.Single(item => item.Id == second).Online);
        Assert.False(before.Single(item => item.Id == restrictive).Online);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        using var scope = fixture.Db.Services.CreateScope();
        var operations = scope.ServiceProvider.GetRequiredService<FittingOperations>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            selected => new(fixture.Db.Store, selected, fixture.Clock, new(), fixture.Db.Objects),
            10,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Notifications.Clear();
        operations.NotifyOnlineChanges(context, changes);
        var notification = Assert.Single(context.Notifications);
        Assert.Equal("OnGodmaShipEffect", notification.Name);
        Assert.Equal(second, notification.Arguments[0]);
        Assert.Equal(false, notification.Arguments[3]);
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(operations.SetOnline(context, fixture.ShipId, second, true));
        Assert.Empty(context.Notifications);
        Assert.Equal(before, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id));
        _ = WorldInitialization.Open(fixture.Db.Path);
        Assert.Equal(before, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id));
    }

    private static void SetAttribute(CatalogDbContext catalog, int type, DogmaAttributeId attribute, double value) =>
        catalog.Database.ExecuteSql(
            $"INSERT OR REPLACE INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES ({type},{(int)attribute},{value})"
        );

    [Fact]
    public void EffectiveSlotAllocationAndCapacityRemovalShareOneAuthoritativeGraph()
    {
        using Fixture fixture = new();
        const int processor = 3887;
        using (CatalogDbContext catalog = new(fixture.CatalogPath, writable: true))
        {
            SetAttribute(catalog, 601, DogmaAttributeId.MediumSlots, 2);
            SetAttribute(catalog, processor, DogmaAttributeId.MediumSlots, 1);
            const int effect = 900032;
            var record = catalog
                .Set<SourceRecord>()
                .AsTracking()
                .Single(source => source.Dataset == "client/typedogma" && source.Key == processor.ToString());
            var dogma = JsonNode.Parse(record.Data)!;
            dogma["dogmaEffects"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { effectID = effect, isDefault = 0 }));
            record.Data = dogma.ToJsonString();
            catalog.SaveChanges();
            SetRecord(
                catalog,
                "client/dogmaeffects",
                effect,
                new
                {
                    effectID = effect,
                    effectCategory = (int)PassiveEffectPhase.Online,
                    modifierInfo = new[]
                    {
                        new
                        {
                            func = "ItemModifier",
                            domain = "shipID",
                            modifiedAttributeID = (int)DogmaAttributeId.MediumSlots,
                            modifyingAttributeID = (int)DogmaAttributeId.MediumSlots,
                            operation = (int)DogmaOperation.Add,
                        },
                    },
                }
            );
        }
        long allowance = fixture.Fit(processor);
        Assert.Equal(3, fixture.Fitting.ShipAttributes(fixture.PilotId)[DogmaAttributeId.MediumSlots]);
        _ = fixture.Fit(377, online: false);
        _ = fixture.Fit(377, online: false);
        long third = fixture.Fit(377, online: false);
        var before = fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id).ToArray();
        Assert.Equal((int)InventoryFlag.MediumSlot0 + 2, before.Single(item => item.Id == third).FlagId);
        Assert.Throws<InvalidDataException>(() => fixture.Fitting.SetOnline(fixture.PilotId, fixture.ShipId, allowance, false));
        Assert.Throws<InvalidDataException>(() =>
            fixture.Inventory.Move(fixture.PilotId, fixture.StationId, new([allowance], fixture.ShipId, (int)InventoryFlag.Hangar, null))
        );
        Assert.Equal(before, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id));
        _ = WorldInitialization.Open(fixture.Db.Path);
        Assert.Equal(before, fixture.Inventory.List(fixture.PilotId, fixture.ShipId).OrderBy(item => item.Id));
    }

    private static void SetRecord(CatalogDbContext catalog, string dataset, int key, object value) =>
        catalog.Database.ExecuteSql(
            $"INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES ({dataset},{key.ToString()},{JsonSerializer.Serialize(value)})"
        );
}
