using System.Diagnostics;
using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class RedemptionTests
{
    [Theory]
    [InlineData("remote")]
    [InlineData("owner")]
    [InlineData("space")]
    [InlineData("expired")]
    public void IneligibleReversalsLeaveTheItemAndGrantLedgerUnchanged(string reason)
    {
        var clock = CalendarTestData.Clock();
        using var db = Create(clock);
        var store = Store(db);
        var token = store.Grant(Grant(expires: clock.Now + TimeSpan.TicksPerSecond));
        var item = Assert.Single(store.Claim(Account, Pilot, [new(token.Id, 34)], false).Items).Item;
        HomeStationTestData.Prepare(db);
        using (WorldDbContext world = new(db.Path))
        {
            var saved = world.Set<OwnedItem>().Find(item.Id)!;
            if (reason == "remote")
            {
                saved.LocationId = HomeStationTestData.School;
            }
            if (reason == "owner")
            {
                saved.OwnerId = 90000002;
            }
            world.SaveChanges();
        }
        if (reason == "space")
        {
            var pilot = db.Store.FindPilot(Account)!;
            db.Store.SetPosition(pilot.ShipId, new(1, 2, 3), pilot.SystemId);
        }
        if (reason == "expired")
        {
            clock.AdvanceWallTime(TimeSpan.FromSeconds(1));
        }
        Assert.Throws<InvalidDataException>(() => store.Reverse(Account, Pilot, item.Id));
        using WorldDbContext verify = new(db.Path);
        var retained = verify.Set<OwnedItem>().Find(item.Id)!;
        Assert.Equal(10, retained.Quantity);
        Assert.Equal(reason == "owner" ? 90000002 : Pilot, retained.OwnerId);
        Assert.Equal(reason == "remote" ? HomeStationTestData.School : item.LocationId, retained.LocationId);
        var ledger = Assert.Single(verify.Set<SavedRedeemToken>());
        Assert.Equal(RedeemTokenState.Claimed, ledger.State);
        Assert.Null(ledger.ReversedAt);
        Assert.Empty(store.Read(Account));
    }

    [Fact]
    public async Task AdministrativeGrantCommandPersistsExactlyOnceAndExitsWithoutStartingTheServer()
    {
        using var db = Create();
        string manifest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(db.Path)!, "reward.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(Grant()));
        for (int run = 0; run < 2; run++)
        {
            ProcessStartInfo start = new("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in new[] { typeof(WorldDatabase).Assembly.Location, "--world", db.Path, "--grant-redeem", manifest })
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await error);
            Assert.Contains("issued to account 1000001", await output);
            Assert.DoesNotContain("listening", await output);
        }
        var token = Assert.Single(Store(db).Read(Account));
        Assert.Equal(10, token.Quantity);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(3651)]
    [InlineData(601)]
    [InlineData(32880)]
    public void CatalogVariantsBecomeRealHangarInventoryAndShipsCanBeAssembled(int type)
    {
        using var db = Create();
        if (type == 32880)
        {
            PilotFleetTests.AddVenture(db);
        }
        var store = Store(db);
        var token = store.Grant(Grant(type: type, quantity: 1));
        var item = Assert.Single(store.Claim(Account, Pilot, [new(token.Id, type)], false).Items).Item;
        Assert.Equal(type, item.TypeId);
        Assert.Equal((int)InventoryFlag.Hangar, item.FlagId);
        Assert.Equal(
            item,
            Assert.Single(db.Services.GetRequiredService<IPilotInventory>().List(Pilot, item.LocationId), i => i.Id == item.Id)
        );
        if (type == 32880)
        {
            var assembled = db.Services.GetRequiredService<IPilotFleet>().Assemble(Pilot, [item.Id], "Redeemed ship");
            Assert.Equal(item.Id, Assert.Single(assembled.ShipIds));
            Assert.Throws<InvalidDataException>(() => store.Reverse(Account, Pilot, item.Id));
        }
    }

    internal const long Account = 1000001;
    internal const long Pilot = 90000001;

    internal static RedeemGrant Grant(
        string reference = "reward",
        int type = 34,
        int quantity = 10,
        int? station = null,
        long? expires = null
    ) => new(Account, reference, type, quantity, "Test reward", "Reward description", station, expires);

    internal static IAccountRedemption Store(TestWorld db) => db.Services.GetRequiredService<IAccountRedemption>();

    internal static TestWorld Create(TimeProvider? clock = null)
    {
        TestWorld db = new(clock: clock);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO ItemCategory VALUES (4,'Material');
            INSERT INTO ItemGroup VALUES (18,'Mineral',4);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (34,'Tritanium',18);
            INSERT INTO SourceRecord VALUES ('client/types','34','{{"volume":0.01,"published":1}}');
            """
        );
        return db;
    }

    [Fact]
    public void GrantsArePrivatePersistentAndIdempotentEvenAfterClaimAndReopen()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = Create(clock);
        var store = Store(db);
        var grant = Grant();
        var token = store.Grant(grant);
        Assert.Equal(token, store.Grant(grant));
        Assert.Empty(store.Read(1000002));
        Assert.Equal(token, Assert.Single(store.Read(Account)));
        Assert.Throws<InvalidDataException>(() => store.Grant(grant with { Quantity = 11 }));
        var result = store.Claim(Account, Pilot, [new(token.Id, token.TypeId)], false);
        var item = Assert.Single(result.Items).Item;
        Assert.Equal(10, item.Quantity);
        Assert.Equal(34, item.TypeId);
        Assert.Equal(Pilot, item.OwnerId);
        Assert.Equal(db.Store.FindPilot(Account)!.StationId, item.LocationId);
        Assert.False(item.Singleton);
        Assert.Empty(result.Errors);
        Assert.Empty(store.Read(Account));
        Assert.Equal(token, store.Grant(grant));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var again = reopened.GetRequiredService<IAccountRedemption>();
        Assert.Empty(again.Read(Account));
        Assert.Equal(token, again.Grant(grant));
        Assert.Single(again.Claim(Account, Pilot, [new(token.Id, token.TypeId)], false).Errors);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(10, world.Set<OwnedItem>().Find(item.Id)!.Quantity);
        Assert.Single(world.Set<SavedRedeemToken>());
    }

    [Fact]
    public void NativeBatchReportsUnavailableTokensWithoutLosingEligibleRewardsOrLeakingForeignRewards()
    {
        using TestWorld db = Create();
        var store = Store(db);
        var first = store.Grant(Grant("one"));
        var second = store.Grant(Grant("two", quantity: 20));
        var foreign = store.Grant(Grant("foreign") with { AccountId = 1000002 });
        var result = store.Claim(
            Account,
            Pilot,
            [new(first.Id, 34), new(second.Id, 35), new(foreign.Id, 34), new(long.MaxValue, 34)],
            false
        );
        Assert.Single(result.Items);
        Assert.Equal(first.Id, result.Items[0].TokenId);
        Assert.Equal(3, result.Errors.Count);
        Assert.Equal(second, Assert.Single(store.Read(Account)));
        Assert.Equal(foreign, Assert.Single(store.Read(1000002)));
        Assert.Throws<InvalidDataException>(() => store.Claim(Account, 90000002, [new(second.Id, 34)], false));
        Assert.Throws<InvalidDataException>(() => store.Trash(Account, [second.Id, foreign.Id]));
        Assert.Equal(second, Assert.Single(store.Read(Account)));
        store.Trash(Account, [second.Id]);
        Assert.Empty(store.Read(Account));
        Assert.Equal(second, store.Grant(Grant("two", quantity: 20)));
    }

    [Fact]
    public void ExpiryUsesTheExactAuthoritativeBoundaryAndCannotConsumeAnExpiredReward()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = Create(clock);
        var store = Store(db);
        long due = clock.Now + TimeSpan.TicksPerSecond;
        var token = store.Grant(Grant(expires: due));
        clock.AdvanceWallTime(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        Assert.Single(store.Read(Account));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Empty(store.Read(Account));
        Assert.Single(store.Claim(Account, Pilot, [new(token.Id, 34)], false).Errors);
        Assert.Throws<InvalidDataException>(() => store.Trash(Account, [token.Id]));
        Assert.Throws<InvalidDataException>(() => store.Grant(Grant("late", expires: due)));
        Assert.Equal(token, store.Grant(Grant(expires: due)));
        using WorldDbContext world = new(db.Path);
        Assert.Equal(RedeemTokenState.Pending, world.Set<SavedRedeemToken>().Single().State);
    }

    [Fact]
    public void HomeAndFixedDestinationsUseSavedPilotStateAndNeverTheCallersClaimedLocation()
    {
        using TestWorld db = Create();
        var store = Store(db);
        int current = db.Store.FindPilot(Account)!.StationId;
        HomeStationTestData.Prepare(db);
        int home = HomeStationTestData.School;
        Assert.NotEqual(current, home);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedHomeStation>().Find(Pilot)!.StationId = home;
            world.SaveChanges();
        }
        var fixedToken = store.Grant(Grant("fixed", station: current));
        var free = store.Grant(Grant("free"));
        var result = store.Claim(Account, Pilot, [new(fixedToken.Id, 34), new(free.Id, 34)], true);
        Assert.Equal(new long[] { current, home }, result.Items.Select(i => i.Item.LocationId));
        var inSpace = store.Grant(Grant("space"));
        var contact = db.Store.FindPilot(Account)!;
        db.Store.SetPosition(contact.ShipId, new(1, 2, 3), contact.SystemId);
        Assert.Throws<InvalidDataException>(() => store.Claim(Account, Pilot, [new(inSpace.Id, 34)], false));
        Assert.Equal(inSpace, Assert.Single(store.Read(Account)));
        Assert.Equal(home, Assert.Single(store.Claim(Account, Pilot, [new(inSpace.Id, 34)], true).Items).Item.LocationId);
    }

    [Fact]
    public async Task CompetingGrantsAndClaimsCannotDuplicateTokensOrInventory()
    {
        using TestWorld db = Create();
        var store = Store(db);
        var tokens = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() => store.Grant(Grant()))));
        Assert.Equal(tokens[0], tokens[1]);
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => store.Claim(Account, Pilot, [new(tokens[0].Id, 34)], false)))
        );
        Assert.Single(results.SelectMany(r => r.Items));
        Assert.Single(results.SelectMany(r => r.Errors));
        using WorldDbContext world = new(db.Path);
        Assert.Single(world.Set<SavedRedeemToken>());
        Assert.Equal(10, world.Set<OwnedItem>().Find(results.SelectMany(r => r.Items).Single().Item.Id)!.Quantity);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("trash")]
    [InlineData("reverse")]
    public void LatePersistenceFailuresRollBackInventoryAndEveryTokenState(string operation)
    {
        using TestWorld db = Create();
        var store = Store(db);
        var one = store.Grant(Grant("one"));
        var two = store.Grant(Grant("two"));
        long item = operation == "reverse" ? Assert.Single(store.Claim(Account, Pilot, [new(one.Id, 34)], false).Items).Item.Id : 0;
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            operation == "reverse"
                ? "CREATE TRIGGER fail_redeem BEFORE INSERT ON WorldRedeemToken BEGIN SELECT RAISE(ABORT,'queue failed'); END"
                : $"CREATE TRIGGER fail_redeem BEFORE UPDATE ON WorldRedeemToken WHEN NEW.Id={two.Id} "
                    + "BEGIN SELECT RAISE(ABORT,'queue failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() =>
        {
            if (operation == "claim")
            {
                store.Claim(Account, Pilot, [new(one.Id, 34), new(two.Id, 34)], false);
            }
            else if (operation == "trash")
            {
                store.Trash(Account, [one.Id, two.Id]);
            }
            else
            {
                store.Reverse(Account, Pilot, item);
            }
        });
        using WorldDbContext world = new(db.Path);
        Assert.Equal(2, world.Set<SavedRedeemToken>().Count());
        if (operation == "reverse")
        {
            Assert.NotNull(world.Set<OwnedItem>().Find(item));
            Assert.Null(world.Set<SavedRedeemToken>().Find(one.Id)!.ReversedAt);
        }
        else
        {
            Assert.All(world.Set<SavedRedeemToken>(), t => Assert.Equal(RedeemTokenState.Pending, t.State));
            Assert.DoesNotContain(world.Set<OwnedItem>(), i => i.TypeId == 34 && i.Quantity == 10);
        }
    }

    [Fact]
    public void OnlyAnIntactLocalRedeemedStackCanBeReversedAndItCanBeClaimedAgainWithoutDuplication()
    {
        using TestWorld db = Create();
        var store = Store(db);
        var token = store.Grant(Grant());
        var item = Assert.Single(store.Claim(Account, Pilot, [new(token.Id, 34)], false).Items).Item;
        Assert.Throws<InvalidDataException>(() => store.Reverse(1000002, 90000002, item.Id));
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(item.Id)!.Quantity--;
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => store.Reverse(Account, Pilot, item.Id));
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(item.Id)!.Quantity++;
            world.SaveChanges();
        }
        var removal = store.Reverse(Account, Pilot, item.Id);
        Assert.Equal(0, removal.Item.Quantity);
        Assert.Equal(item, removal.Previous);
        var returned = Assert.Single(store.Read(Account));
        Assert.NotEqual(token.Id, returned.Id);
        Assert.Throws<InvalidDataException>(() => store.Reverse(Account, Pilot, item.Id));
        Assert.Equal(token, store.Grant(Grant()));
        var replacement = Assert.Single(store.Claim(Account, Pilot, [new(returned.Id, 34)], false).Items).Item;
        Assert.NotEqual(item.Id, replacement.Id);
        using WorldDbContext verify = new(db.Path);
        Assert.Null(verify.Set<OwnedItem>().Find(item.Id));
        Assert.Equal(10, verify.Set<OwnedItem>().Find(replacement.Id)!.Quantity);
        Assert.Empty(store.Read(Account));
    }

    [Fact]
    public void GenuineVersion54MigrationPreservesSecurityAndAddsAnEmptyAccountQueue()
    {
        using TestWorld db = Create();
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(new SavedSecurityStatus { PilotId = Pilot, Value = 2.5 });
            world.SaveChanges();
            world.Database.ExecuteSqlRaw("DROP TABLE WorldRedeemToken; PRAGMA user_version=54;");
        }
        WorldInitialization.Open(db.Path);
        Assert.Empty(Store(db).Read(Account));
        Assert.Equal(2.5, db.Services.GetRequiredService<IPilotSecurityStatus>().Read(Pilot).Value);
        Assert.True(Store(db).Grant(Grant()).Id > 0);
    }

    [Fact]
    public void InvalidGrantsAndDuplicateBatchesLeaveAllStateUnchanged()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = Create(clock);
        var store = Store(db);
        foreach (
            var grant in new[]
            {
                Grant() with
                {
                    AccountId = long.MaxValue,
                },
                Grant(type: int.MaxValue),
                Grant(quantity: 0),
                Grant(station: int.MaxValue),
                Grant() with
                {
                    Reference = "#reserved",
                },
                Grant() with
                {
                    Label = "",
                },
                Grant() with
                {
                    Description = new('x', RedemptionRules.MaximumDescription + 1),
                },
                Grant(expires: long.MaxValue),
            }
        )
        {
            Assert.Throws<InvalidDataException>(() => store.Grant(grant));
        }
        Assert.Empty(store.Read(Account));
        var token = store.Grant(Grant());
        foreach (var ids in new long[][] { [], [0], [token.Id, token.Id], Enumerable.Range(1, 101).Select(i => (long)i).ToArray() })
        {
            Assert.Throws<InvalidDataException>(() => store.Trash(Account, ids));
            Assert.Throws<InvalidDataException>(() =>
                store.Claim(Account, Pilot, ids.Select(i => new RedeemSelection(i, 34)).ToArray(), false)
            );
        }
        Assert.Equal(token, Assert.Single(store.Read(Account)));
    }

    [Theory]
    [InlineData(InventoryCategory.Ship, true)]
    [InlineData(InventoryCategory.Module, true)]
    [InlineData(InventoryCategory.Charge, true)]
    [InlineData(InventoryCategory.Skill, true)]
    [InlineData(InventoryCategory.Commodity, true)]
    [InlineData(InventoryCategory.Material, true)]
    [InlineData(InventoryCategory.Blueprint, false)]
    [InlineData(InventoryCategory.SpecialEditionAssets, false)]
    [InlineData(InventoryCategory.ShipSkin, false)]
    public void SpecialActivationRewardsCannotBeIssuedAsOrdinaryInventory(InventoryCategory category, bool expected) =>
        Assert.Equal(expected, RedemptionRules.Deliverable(category));
}
