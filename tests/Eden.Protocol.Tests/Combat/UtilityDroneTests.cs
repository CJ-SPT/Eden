using System.Reflection;
using System.Text.Json;
using Eden.Data.World.Drones;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Catalog;
using Eden.World.Drones;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Combat.CombatDroneTests.Fixture;

namespace Eden.Protocol.Tests.Combat;

public sealed class UtilityDroneTests
{
    [Fact]
    public void UtilityStrengthAndMiningSkillsHaveSeparateConsumers()
    {
        var profile = new UtilityDroneProfile(UtilityDroneKind.OreMining, 5000, 60000, 25);
        var basis = new DroneProfile(
            10246,
            5,
            5,
            100,
            10,
            1,
            400,
            200,
            100,
            5000,
            0,
            0,
            0,
            60000,
            new(0, 0, 0, 0),
            new(100, 100, 100, 10000, new(1, 1, 1, 1), new(1, 1, 1, 1), new(1, 1, 1, 1)),
            CombatDroneKind.Utility,
            profile
        );
        var skilled = DroneRules.ApplySkills(
            basis,
            new Dictionary<DroneSkill, int> { [DroneSkill.MiningDroneOperation] = 5, [DroneSkill.DroneInterfacing] = 5 },
            new HashSet<int>()
        );
        Assert.Equal(46.875, skilled.Utility!.Amount);
        var web = basis with { Utility = profile with { Kind = UtilityDroneKind.Webifying, Amount = -5 } };
        Assert.Equal(
            -5,
            DroneRules
                .ApplySkills(web, new Dictionary<DroneSkill, int> { [DroneSkill.DroneInterfacing] = 5 }, new HashSet<int>())
                .Utility!.Amount
        );
    }

    public static IEnumerable<object[]> StaticTypes()
    {
        using var stream = Assembly
            .GetExecutingAssembly()
            .GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.UtilityDroneCatalog.json")!;
        using var document = JsonDocument.Parse(stream);
        var records = document
            .RootElement.GetProperty("records")
            .EnumerateArray()
            .Where(r => r.GetProperty("dataset").GetString() == "client/types")
            .ToDictionary(r => r.GetProperty("key").GetInt32(), r => r.GetProperty("data").Clone());
        foreach (var type in document.RootElement.GetProperty("types").EnumerateArray())
        {
            int id = type.GetProperty("id").GetInt32();
            if (UtilityDroneRules.Group(type.GetProperty("groupId").GetInt32()))
            {
                bool mutated = records[id].TryGetProperty("metaGroupID", out var meta) && meta.GetInt32() == 15;
                yield return [id, !mutated];
            }
        }
    }

    [Theory]
    [MemberData(nameof(StaticTypes))]
    public void StaticUtilityAndMiningCatalogProfilesRetainTheirCapabilities(int type, bool supported)
    {
        using var f = new Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture();
        PassiveCatalogFixture.LoadUtilityDrones(f.CatalogPath);
        using CatalogDbContext catalog = new(f.CatalogPath);
        if (!supported)
        {
            Assert.Throws<InvalidDataException>(() => DroneCatalog.Profile(catalog, type));
            return;
        }
        var profile = DroneCatalog.Profile(catalog, type);
        Assert.NotNull(profile.Utility);
        Assert.True(profile.Utility.Range > 0);
        Assert.Equal(type, profile.TypeId);
        Assert.Equal(catalog.RequireItem(type).GroupId == UtilityDroneRules.HybridGroup, profile.Damage.Total > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MiningDronesUseGatheringYieldDeliverCargoAndCheckpoint(decimal multiplier)
    {
        using Fixture f = Prepare();
        PassiveCatalogFixture.LoadMining(f.Base.CatalogPath);
        WorldTuningTests.Activate(f.Base.Db, s => s with { Resources = s.Resources with { OreYieldMultiplier = multiplier } });
        long stack = f.Bay(10246, 1);
        f.Space();
        using LiveWorld live = new(f.Base.Db.Services.GetRequiredService<WorldDatabase>(), f.Base.Clock, _ => { });
        long rockId = 8000000000100;
        using (var transaction = live.Economy.Begin())
        {
            var position = transaction.Pilots.Find(f.Pilot)!.ActiveShip.Position!;
            transaction.Add(
                new WorldAsteroid
                {
                    Id = rockId,
                    SystemId = 30000144,
                    BeltId = 40009232,
                    TypeId = 1230,
                    Quantity = 100000,
                    Radius = 30,
                    X = position.X + 500,
                    Y = position.Y,
                    Z = position.Z,
                }
            );
            transaction.Commit();
        }
        long drone = f.Drones.Launch(f.Pilot, [new(stack, 1)], f.Now).Launches.Single().DroneIds.Single();
        f.Drones.Order(f.Pilot, [drone], DroneOrder.Mine, rockId, new HashSet<long> { rockId }, f.Now);
        Assert.Empty(f.Tick().Hits);
        var mined = f.Tick(60);
        Assert.Empty(mined.Hits);
        Assert.Empty(mined.Changes);
        var checkpoint = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        using (checkpoint)
        {
            checkpoint.Project();
            using WorldDbContext reopened = new(checkpoint.Database);
            var work = JsonSerializer.Deserialize<DroneWork>(reopened.Set<WorldDrone>().Find(drone)!.WorkJson!)!;
            Assert.InRange(Assert.Single(work.Cargo!).Quantity, (int)(468 * multiplier), (int)(469 * multiplier));
            Assert.Equal(100000 - Assert.Single(work.Cargo!).Quantity, reopened.Set<WorldAsteroid>().Find(rockId)!.Quantity);
        }
        var delivered = f.Tick();
        var cargo = Assert.Single(delivered.Changes);
        Assert.Equal(1230, cargo.Item.TypeId);
        Assert.Equal((int)InventoryFlag.Cargo, cargo.Item.FlagId);
        Assert.InRange(cargo.Item.Quantity, (int)(468 * multiplier), (int)(469 * multiplier));
        Assert.Empty(f.Tick(120).Changes);
        Assert.Equal(DroneActivity.Idle, Assert.Single(f.Drones.List(30000144, f.Now)).Activity);
    }

    internal static Fixture Prepare()
    {
        Fixture f = new();
        PassiveCatalogFixture.LoadUtilityDrones(f.Base.CatalogPath);
        foreach (var skill in Enum.GetValues<DroneSkill>())
        {
            f.Base.Train((int)skill, 5);
        }
        using (CatalogDbContext setup = new(f.Base.CatalogPath, writable: true))
        {
            string security = JsonSerializer.Serialize(new { securityStatus = 0 });
            setup.Database.ExecuteSql($"INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000144',{security})");
        }
        using CatalogDbContext catalog = new(f.Base.CatalogPath);
        var types = catalog
            .Set<Eden.Data.Catalog.TypeAttribute>()
            .Where(a =>
                a.AttributeId == (int)DogmaAttributeId.RequiredSkill1
                || a.AttributeId == (int)DogmaAttributeId.RequiredSkill2
                || a.AttributeId == (int)DogmaAttributeId.RequiredSkill3
            )
            .Select(a => (int)a.Value)
            .Distinct()
            .ToArray();
        foreach (int skill in types)
        {
            f.Base.Train(skill, 5);
        }
        return f;
    }
}
