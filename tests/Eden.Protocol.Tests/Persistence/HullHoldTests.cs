using System.Reflection;
using System.Text.Json;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Persistence;

public sealed class HullHoldTests
{
    private sealed record NativeCapacity(int Flag, int Attribute, double Capacity);

    private sealed record NativeHull(int TypeId, int GroupId, IReadOnlyList<NativeCapacity> Holds);

    private sealed record NativeFixture(IReadOnlyList<NativeHull> Volumes);

    public static IEnumerable<object[]> Hulls()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.NativeShipInventory.json")!;
        return JsonSerializer
            .Deserialize<NativeFixture>(stream, JsonSerializerOptions.Web)!
            .Volumes.Where(h =>
                TechOneHullRules.IsSmall((HullClass)h.GroupId)
                || TechOneHullRules.IsCombat((HullClass)h.GroupId)
                || TechOneHullRules.IsUtility((HullClass)h.GroupId)
            )
            .Where(h => h.Holds.Any(hold => hold.Flag != (int)InventoryFlag.Cargo))
            .Select(h => new object[] { h.TypeId, h.GroupId })
            .ToArray();
    }

    internal static Fixture Prepare(int type, HullClass hullClass)
    {
        Fixture fixture = new();
        if (TechOneHullRules.IsSmall(hullClass))
        {
            PassiveCatalogFixture.LoadSmallHull(fixture.CatalogPath, type);
        }
        else if (TechOneHullRules.IsCombat(hullClass))
        {
            PassiveCatalogFixture.LoadCombatHull(fixture.CatalogPath, type);
        }
        else
        {
            PassiveCatalogFixture.LoadUtilityHull(fixture.CatalogPath, type);
        }
        PassiveCatalogFixture.LoadHoldCargo(fixture.CatalogPath);
        SmallHullTests.Board(fixture, type);
        return fixture;
    }

    [Theory]
    [MemberData(nameof(Hulls))]
    public void EveryPersonalHoldEnforcesItsCapacityCargoOwnershipAndRestart(int type, int group)
    {
        using Fixture f = Prepare(type, (HullClass)group);
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var attributes = f.Fitting.ShipAttributes(f.PilotId);
        foreach (
            var hold in ShipHoldRules.Definitions.Where(h =>
                h.Flag != InventoryFlag.Cargo && attributes.GetValueOrDefault(h.CapacityAttribute) > 0
            )
        )
        {
            int flag = (int)hold.Flag;
            if (hold.RequiresAssembledShip)
            {
                int childType = hold.Flag == InventoryFlag.FrigateEscapeBay ? 587 : 33513;
                long child = Assert.Single(fleet.Assemble(f.PilotId, [f.Grant(childType)], "Stored hull").ShipIds);
                f.Inventory.Move(f.PilotId, [child], f.StationId, f.ShipId, flag, null);
                Assert.DoesNotContain(fleet.Ships(f.PilotId, checked((int)f.StationId)), item => item.Id == child);
                Assert.Contains(f.Inventory.List(f.PilotId, f.ShipId), item => item.Id == child && item.FlagId == flag);
                Assert.Throws<InvalidDataException>(() => fleet.Board(f.PilotId, child, f.ShipId));
                f.Inventory.Move(f.PilotId, [child], f.ShipId, f.StationId, (int)InventoryFlag.Hangar, null);
                Assert.Equal("Stored hull", fleet.Name(f.PilotId, child));
                Assert.Contains(fleet.Ships(f.PilotId, checked((int)f.StationId)), item => item.Id == child);
                continue;
            }
            long cargo = f.Grant(Probe(hold.Flag));
            var source = Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == cargo);
            Assert.True(hold.Allows(source));
            Assert.Throws<InvalidDataException>(() => f.Inventory.Move(f.PilotId, [cargo, cargo], f.StationId, f.ShipId, flag, null));
            Assert.Throws<InvalidDataException>(() => f.Inventory.Move(90000002, [cargo], f.StationId, f.ShipId, flag, null));
            Assert.Throws<InvalidDataException>(() => f.Inventory.Move(f.PilotId, [cargo], f.StationId + 1, f.ShipId, flag, null));
            f.Inventory.Move(f.PilotId, [cargo], f.StationId, f.ShipId, flag, null);
            Assert.Contains(f.Inventory.List(f.PilotId, f.ShipId), item => item.Id == cargo && item.FlagId == flag);
            if (hold.Flag != InventoryFlag.FleetHangar)
            {
                long invalid = f.Grant(hold.Flag == InventoryFlag.AmmoHold ? 3651 : 215);
                Assert.Throws<InvalidDataException>(() => f.Inventory.Move(f.PilotId, [invalid], f.StationId, f.ShipId, flag, null));
            }
            int excessive = checked((int)Math.Floor(attributes[hold.CapacityAttribute] / source.Volume) + 1);
            long over = f.Grant(source.TypeId, excessive);
            Assert.Throws<CargoCapacityException>(() => f.Inventory.Move(f.PilotId, [over], f.StationId, f.ShipId, flag, null));
            Assert.Equal(excessive, Assert.Single(f.Inventory.List(f.PilotId, f.StationId), item => item.Id == over).Quantity);
            f.Inventory.Move(f.PilotId, [cargo], f.ShipId, f.StationId, (int)InventoryFlag.Hangar, null);
            f.Inventory.Move(f.PilotId, [cargo], f.StationId, f.ShipId, flag, null);
        }
        var saved = f.Inventory.List(f.PilotId, f.ShipId);
        _ = WorldInitialization.Open(f.Db.Path);
        Assert.Equal(saved, f.Inventory.List(f.PilotId, f.ShipId));
    }

    internal static int Probe(InventoryFlag flag) =>
        flag switch
        {
            InventoryFlag.DroneBay => 2488,
            InventoryFlag.FuelBay => 16272,
            InventoryFlag.GeneralMiningHold => 1230,
            InventoryFlag.GasHold => 25268,
            InventoryFlag.MineralHold => 34,
            InventoryFlag.AmmoHold or InventoryFlag.FleetHangar => 215,
            InventoryFlag.CommandCenterHold => 2524,
            InventoryFlag.PlanetaryCommoditiesHold or InventoryFlag.InfrastructureHold => 3645,
            InventoryFlag.QuafeBay => 3699,
            InventoryFlag.FighterBay => 23055,
            InventoryFlag.CorpseBay => 25,
            InventoryFlag.IceHold => 16262,
            InventoryFlag.MobileDepotHold => 33474,
            InventoryFlag.ExpeditionHold => 28668,
            _ => throw new InvalidDataException("A native hold needs an explicit acceptance cargo probe."),
        };
}
