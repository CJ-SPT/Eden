using System.Globalization;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InventoryOwnershipTests
{
    private const long CorporateHull = 1000000000099;
    private const long CorporateStack = 1100000000099;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RegisteredCorporationsCanOwnInactiveShipsAndItemsWithoutExposingThemAsPersonalAssets(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        PlayerCorporationTests.Prepare(world);
        int corporation = world
            .Services.GetRequiredService<IPlayerCorporations>()
            .Create(90000001, PlayerCorporationTests.Details())
            .Corporation.Id;
        using (WorldDbContext saved = new(world.Path))
        {
            Assert.NotNull(saved.Set<InventoryOwner>().Find((long)corporation));
            saved.Add(
                new Ship
                {
                    Id = CorporateHull,
                    OwnerId = corporation,
                    TypeId = 601,
                    StationId = 60003760,
                    Name = "Corporate hull",
                }
            );
            saved.Add(
                new OwnedItem
                {
                    Id = CorporateStack,
                    OwnerId = corporation,
                    TypeId = InventoryDefinitions.CivilianMinerType,
                    LocationId = 60003760,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            saved.SaveChanges();
        }
        var inventory = world.Services.GetRequiredService<IPilotInventory>();
        Assert.DoesNotContain(inventory.List(90000001, 60003760), i => i.Id == CorporateStack);
        Assert.DoesNotContain(world.Services.GetRequiredService<IPilotFleet>().Ships(90000001, 60003760), s => s.Id == CorporateHull);
        Assert.Equal(corporation, Assert.Single(inventory.List(corporation, 60003760)).OwnerId);
        using (WorldDbContext saved = new(world.Path))
        {
            long activeShip = world.Store.FindPilot(1000001)!.ShipId;
            Assert.Throws<SqliteException>(() =>
                saved.Database.ExecuteSql($"UPDATE WorldShip SET OwnerId={corporation} WHERE Id={activeShip}")
            );
        }
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(corporation, check.Ships.Find(CorporateHull)!.OwnerId);
        Assert.Equal(corporation, check.Set<OwnedItem>().Find(CorporateStack)!.OwnerId);
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
        Assert.Equal(90000001, check.Ships.Find(reopened.GetRequiredService<SqliteWorld>().FindPilot(1000001)!.ShipId)!.OwnerId);
    }

    [Fact]
    public void NewPilotsRegisterAutomaticallyAndUnknownOwnersStillFailTheDatabaseConstraint()
    {
        using TestWorld world = new();
        TestCatalog.AddSchool(new WorldDatabase(world.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        long pilot = world.Services.GetRequiredService<ICharacterCreation>().Create(1000001, new("New Owner", 1, 1, 1, 64, [1], [2], 18));
        using WorldDbContext saved = new(world.Path);
        Assert.NotNull(saved.Set<InventoryOwner>().Find(pilot));
        saved.Add(
            new OwnedItem
            {
                Id = CorporateStack,
                OwnerId = 999999,
                TypeId = InventoryDefinitions.CivilianMinerType,
                LocationId = 60003760,
                FlagId = (int)InventoryFlag.Hangar,
                Quantity = 1,
            }
        );
        Assert.Throws<DbUpdateException>(() => saved.SaveChanges());
    }

    [Fact]
    public void Version77RebuildPreservesContentsIndexesFittingTriggersAndCompositeActiveShipOwnership()
    {
        using TestWorld world = new();
        long[] items;
        long[] ships;
        string[] triggers;
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw("CREATE INDEX OwnershipTestIndex ON WorldItem(TypeId,Quantity)");
            items = saved.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray();
            ships = saved.Ships.OrderBy(s => s.Id).Select(s => s.Id).ToArray();
            triggers = saved
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_schema WHERE type='trigger' ORDER BY name")
                .ToArray();
        }
        world.Services.Dispose();
        RestoreVersion77(world.Path);
        WorldInitialization.Initialize(new(world.Path));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(items, check.Set<OwnedItem>().OrderBy(i => i.Id).Select(i => i.Id).ToArray());
        Assert.Equal(ships, check.Ships.OrderBy(s => s.Id).Select(s => s.Id).ToArray());
        Assert.Equal(
            triggers,
            check.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_schema WHERE type='trigger' ORDER BY name").ToArray()
        );
        Assert.Equal(
            1,
            check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_schema WHERE name='OwnershipTestIndex'").Single()
        );
        Assert.Equal(
            "WorldInventoryOwner",
            check
                .Database.SqlQueryRaw<string>(
                    "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldItem') WHERE \"from\"='OwnerId'"
                )
                .Single()
        );
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(0, check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
    }

    [Fact]
    public void ACorruptLegacyOwnerRejectsAndRollsBackBothInventoryTableReplacements()
    {
        using TestWorld world = new();
        world.Services.Dispose();
        RestoreVersion77(world.Path);
        using (WorldDbContext corrupt = new(world.Path))
        {
            corrupt.Database.OpenConnection();
            corrupt.Database.ExecuteSqlRaw("PRAGMA foreign_keys=OFF");
            corrupt.Database.ExecuteSql($"UPDATE WorldItem SET OwnerId=999999 WHERE TypeId={InventoryDefinitions.CivilianMinerType}");
            corrupt.Database.CloseConnection();
        }
        Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(new(world.Path)));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(77, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(
            "WorldPilot",
            check
                .Database.SqlQueryRaw<string>(
                    "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldItem') WHERE \"from\"='OwnerId'"
                )
                .Single()
        );
        Assert.Equal(
            "WorldPilot",
            check
                .Database.SqlQueryRaw<string>(
                    "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldShip') WHERE \"from\"='OwnerId'"
                )
                .Single()
        );
        Assert.Equal(
            0,
            check.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_schema WHERE name LIKE '%OwnershipUpgrade'").Single()
        );
        Assert.Equal(1, check.Database.SqlQueryRaw<int>("PRAGMA foreign_keys").AsEnumerable().Single());
    }

    private static void RestoreVersion77(string path)
    {
        // Change only the scratch database's FK targets to emulate the old schema.
        // Reopening with a bumped schema cookie makes SQLite reparse those declarations.
        using WorldDbContext saved = new(path);
        saved.Database.OpenConnection();
        int cookie = saved.Database.SqlQueryRaw<int>("PRAGMA schema_version").AsEnumerable().Single();
        saved.Database.ExecuteSqlRaw(
            """
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema SET sql=replace(sql,'REFERENCES "WorldInventoryOwner"','REFERENCES "WorldPilot"')
                WHERE type='table' AND name IN ('WorldItem','WorldShip');
            PRAGMA writable_schema=OFF;
            PRAGMA user_version=77;
            """
        );
        string resetCookie = "PRAGMA schema_version=" + checked(cookie + 1).ToString(CultureInfo.InvariantCulture);
        saved.Database.ExecuteSqlRaw(resetCookie);
        saved.Database.CloseConnection();
    }
}
