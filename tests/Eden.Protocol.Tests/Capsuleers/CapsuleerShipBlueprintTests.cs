using System.Text.Json;
using Eden.Data.Catalog.Capsuleers;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerShipBlueprintTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void BatchedBlueprintMatchesOrdinaryCreationAndReusesValidatedFit(bool imported, bool ammunition, bool partial)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using var catalogScope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        var origin = CapsuleerProvisioningCatalog.Origins(catalog)[0];
        TrafficProfile? profile = !imported ? null : TrafficSnapshotTests.Profile();
        if (ammunition)
        {
            int full = checked(
                (int)
                    Math.Floor(
                        (catalog.RequireItem(561).Capacity ?? 0) / PilotInventoryStore.Volume(catalog, 215)
                            + InventoryDefinitions.ResourceTolerance
                    )
            );
            profile = profile! with
            {
                HullTypeId = 603,
                Equipment =
                [
                    new(561, (int)InventoryFlag.HighSlot0, 1),
                    new(215, (int)InventoryFlag.HighSlot0, full),
                    new(561, (int)InventoryFlag.HighSlot1, 1),
                    new(215, (int)InventoryFlag.HighSlot1, partial ? 1 : full),
                ],
            };
        }
        int hull = profile?.HullTypeId ?? 648;
        const long ordinaryPilot = 990000001;
        const long copiedPilot = ordinaryPilot + 1;
        const long repeatedPilot = copiedPilot + 1;
        const long ordinaryShip = 140990000001;
        const long copiedShip = ordinaryShip + 10000;
        const long repeatedShip = copiedShip + 10000;
        int station = catalog.Definitions.Stations.Keys.First();
        using CapsuleerShipBlueprints blueprints = new(catalog);
        using (WorldDbContext world = new(f.Database.Path))
        using (var transaction = world.Database.BeginTransaction())
        {
            CreateIdentity(world, ordinaryPilot, ordinaryShip);
            StartingSkills.Grant(world, catalog, ordinaryPilot, origin.RaceId);
            CapsuleerCommunityStore.GrantRequirements(world, catalog, ordinaryPilot, hull, []);
            if (profile is not null)
            {
                TrafficFitting.Apply(world, catalog, ordinaryPilot, ordinaryShip, profile, f.Clock.Now);
            }
            else
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = ordinaryShip + 1,
                        OwnerId = ordinaryPilot,
                        TypeId = CapsuleerProvisioningCatalog.FreightOre(catalog),
                        LocationId = ordinaryShip,
                        FlagId = (int)InventoryFlag.Cargo,
                        Quantity = 100,
                    }
                );
                world.SaveChanges();
            }
            CapsuleerSupplyStore.Ensure(world, catalog, ordinaryPilot, ordinaryShip);
            CreateIdentity(world, copiedPilot, copiedShip);
            blueprints.Create(world, catalog, origin.RaceId, hull, profile, copiedPilot, copiedShip, station, f.Clock.Now);
            Assert.Equal(Snapshot(world, ordinaryPilot, ordinaryShip, f.Clock.Now), Snapshot(world, copiedPilot, copiedShip, f.Clock.Now));
            CreateIdentity(world, repeatedPilot, repeatedShip);
            using (var metrics = SessionPerformance.Begin("warm blueprint materialization", output.WriteLine, force: true)!)
            {
                blueprints.Create(world, catalog, origin.RaceId, hull, profile, repeatedPilot, repeatedShip, station, f.Clock.Now);
                Assert.Equal(0, metrics.FittingCalculations);
                Assert.InRange(metrics.Commands, 2, 12);
                Assert.Equal(0, metrics.Connections);
            }
            Assert.Equal(
                Snapshot(world, ordinaryPilot, ordinaryShip, f.Clock.Now),
                Snapshot(world, repeatedPilot, repeatedShip, f.Clock.Now)
            );
            transaction.Commit();
        }
        using WorldDbContext reopened = new(f.Database.Path);
        Assert.Equal(
            Snapshot(reopened, ordinaryPilot, ordinaryShip, f.Clock.Now),
            Snapshot(reopened, copiedPilot, copiedShip, f.Clock.Now)
        );

        void CreateIdentity(WorldDbContext world, long pilot, long ship)
        {
            long account = world.Pilots.Select(p => p.AccountId).First();
            world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId,CreatedAt,RaceId,BloodlineId,
                    GenderId,AncestryId,SchoolId)
                VALUES ({pilot},{account},{"Blueprint " + pilot},{origin.CorporationId},{ship},{f.Clock.Now},
                    {origin.RaceId},{origin.BloodlineId},0,0,{origin.SchoolId});
                INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},{hull},{station},{"Blueprint " + pilot});
                """
            );
        }
    }

    private static string Snapshot(WorldDbContext world, long pilot, long ship, long now)
    {
        var items = world.Set<OwnedItem>().Where(item => item.OwnerId == pilot).AsNoTracking().ToArray();
        var flags = items.ToDictionary(item => item.Id, item => item.FlagId);
        var health = world.Set<ShipHealthState>().AsNoTracking().SingleOrDefault(row => row.ShipId == ship);
        var energy = world.Set<ShipEnergy>().AsNoTracking().SingleOrDefault(row => row.ShipId == ship);
        return JsonSerializer.Serialize(
            new
            {
                Skills = world
                    .Set<PilotSkill>()
                    .Where(skill => skill.PilotId == pilot)
                    .OrderBy(skill => skill.TypeId)
                    .Select(skill => new { skill.TypeId, skill.Points })
                    .ToArray(),
                Items = items
                    .Select(item => new
                    {
                        item.TypeId,
                        item.FlagId,
                        item.Quantity,
                        item.Singleton,
                        item.Online,
                        item.CrystalDamage,
                        Container = item.LocationId == ship ? "ship"
                        : flags.TryGetValue(item.LocationId, out int flag) ? $"module:{flag}"
                        : "station",
                    })
                    .OrderBy(item => item.Container)
                    .ThenBy(item => item.FlagId)
                    .ThenBy(item => item.TypeId)
                    .ToArray(),
                Health = health is null
                    ? null
                    : new
                    {
                        health.ShieldCharge,
                        health.ArmorDamage,
                        health.StructureDamage,
                    },
                Capacitor = energy?.Charge,
                Cooldowns = world
                    .Set<ModuleCooldown>()
                    .AsNoTracking()
                    .AsEnumerable()
                    .Where(row => flags.ContainsKey(row.ModuleId))
                    .Select(row => new
                    {
                        Flag = flags[row.ModuleId],
                        Delay = row.ReadyAt - now,
                        row.Kind,
                    })
                    .OrderBy(row => row.Flag)
                    .ToArray(),
                Supplies = world.Set<SavedCapsuleerSupplies>().Any(row => row.PilotId == pilot && row.ShipId == ship),
            }
        );
    }
}
