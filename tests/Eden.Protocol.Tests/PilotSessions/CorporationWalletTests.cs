using System.Text.Json;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Market;
using Eden.Protocol.Corporations;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CorporationWalletTests
{
    private static object? Reply(
        CorporationTestClient client,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    ) =>
        BlueMarshal.Decode(
            BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(client.CallService("account", method, args, keywords)).Reply)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTransfersDivisionsSelectionAndJournalsConserveMoneyAndPersist(bool liveWorld)
    {
        using TestWorld world = new(liveWorld: liveWorld);
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        using (CorporationTestClient ceo = new(world, 1000001))
        using (CorporationTestClient member = new(world, 1000002))
        {
            var monitor = ceo.Services.GetRequiredService<CorporationWalletHandler>();
            monitor.Enter(ceo.Context);
            Assert.Equal(7, wallet.Divisions(90000001).Count);
            Assert.Empty(wallet.Divisions(90000002));
            Assert.Null(Reply(ceo, "GiveCash", [(long)id, 100.25, "Funding 星"], new() { ["toAccountKey"] = 1000L }));
            Assert.Null(
                Reply(
                    ceo,
                    "GiveCashFromCorpAccount",
                    [(long)id, 60.10, 1000L],
                    new() { ["toAccountKey"] = 1001L, ["reason"] = "Move to division two" }
                )
            );
            Assert.Null(
                Reply(
                    ceo,
                    "GiveCashFromCorpAccount",
                    [90000002L, 10d, 1001L],
                    new() { ["toAccountKey"] = null, ["reason"] = "Member payment" }
                )
            );
            Assert.Null(Reply(member, "GiveCash", [(long)id, 1d, "Donation"]));
            monitor.Advance(ceo.Context);
            var changed = ceo.Context.Notifications.OfType<CorporationBalanceChanged>().ToArray();
            Assert.Contains(changed, n => n.Key == 1001 && n.BalanceCents == 5010);
            Assert.Equal(4115, wallet.Balance(90000001, 1000));
            Assert.Equal(5010, wallet.Balance(90000001, 1001));
            Assert.Null(ceo.Reply("SetAccountKey", [1001L]));
            Assert.Equal(1001, ceo.Context.Pilot.Identity.CorporationAccountKey);
            Assert.Equal(50.10, Assert.IsType<double>(Reply(ceo, "GetCashBalance", [1L])));
            var divisions = Assert.IsType<List<object?>>(Reply(ceo, "GetWalletDivisionsInfo", []));
            Assert.Equal(7, divisions.Count);
            var division = BlueRead.Fields(Assert.IsType<BlueInstance>(divisions[1]).State);
            Assert.Equal(1001L, division["key"]);
            Assert.Equal(50.10, division["balance"]);
            var journal = Assert.IsType<List<object?>>(Reply(ceo, "GetTransactions", [1001L, null, null, true]));
            Assert.Equal(2, journal.Count);
            var personal = ceo.CallService("account", "GetTransactions", [1000L, null, null, false]);
            var entries = Assert.IsType<BlueList<WalletJournalEntry>>(Assert.IsType<PilotCallResult.Success>(personal).Reply).Items;
            var funding = Assert.Single(entries, e => e.Type == (WalletEntryType)CorporationWalletRules.PlayerDonationEntry);
            Assert.Equal(-10025, funding.AmountCents);
            Assert.Equal("Funding 星", funding.Description);
            Assert.Equal(900, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000002));
            Assert.Equal(840009975, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
            using WorldDbContext saved = new(world.Path);
            Assert.Equal(0, saved.Set<SavedWalletLedger>().Sum(e => e.AmountCents));
            Assert.Equal(8, saved.Set<SavedWalletLedger>().Count());
        }
        world.Services.Dispose();
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        var savedWallet = reopened.GetRequiredService<ICorporationWallet>();
        Assert.Equal(4115, savedWallet.Balance(90000001, 1000));
        Assert.Equal(5010, savedWallet.Balance(90000001, 1001));
        Assert.Equal(1001, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000001).AccountKey);
        Assert.Equal(2, savedWallet.Journal(90000001, true, 1001).Count);
    }

    [Fact]
    public void ViewingWithdrawingAndSelectingDivisionsUseDifferentCurrentPermissions()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        var roles = world.Services.GetRequiredService<ICorporationRoles>();
        CorporationMemberUpdate Access(CorporationRole role) =>
            new(90000002, null, null, null, new((long)role, 0, 0, 0, 0, 0, 0, 0), null, 0);
        wallet.Transfer(90000001, new(false, 1000, id, 1001, 10000, "Fund division"));
        roles.UpdateMembers(90000001, [Access(CorporationRole.JuniorAccountant)]);
        Assert.Equal(7, wallet.Divisions(90000002).Count);
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000002, new(true, 1001, 90000002, 1000, 1, "Forbidden")));
        Assert.Throws<InvalidDataException>(() => wallet.SelectDivision(90000002, 1001));
        roles.UpdateMembers(90000001, [Access(CorporationRole.AccountCanTake2)]);
        Assert.Equal(1001, Assert.Single(wallet.Divisions(90000002)).Key);
        wallet.SelectDivision(90000002, 1001);
        Assert.Throws<InvalidDataException>(() => wallet.Balance(90000002, 1000));
        wallet.Transfer(90000002, new(true, 1001, 90000002, 1000, 1, "Authorized"));
        roles.UpdateMembers(90000001, [Access(CorporationRole.NoRole)]);
        Assert.Equal(1000, world.Services.GetRequiredService<IPlayerCorporations>().Identity(90000002).AccountKey);
        Assert.Throws<InvalidDataException>(() => wallet.Balance(90000002, 1001));
        Assert.Throws<InvalidDataException>(() => wallet.Journal(90000002, true, 1001));
        Assert.Equal(9999, wallet.Balance(90000001, 1001));
    }

    [Fact]
    public void MalformedForgedInsufficientOverflowAndFailedWritesLeaveBothSidesUnchanged()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        using CorporationTestClient ceo = new(world, 1000001);
        using CorporationTestClient member = new(world, 1000002);
        foreach (object? amount in new object?[] { -1d, 0d, double.NaN, double.PositiveInfinity, 0.001, long.MaxValue, "100" })
        {
            Assert.IsType<PilotCallResult.Failure>(ceo.CallService("account", "GiveCash", [(long)id, amount, "Invalid"]));
        }
        Assert.IsType<PilotCallResult.Failure>(
            ceo.CallService("account", "GiveCash", [(long)id, 1d, "Forged"], new() { ["fromID"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Failure>(ceo.CallService("account", "GiveCash", [99999999L, 1d, "Unknown"]));
        Assert.IsType<PilotCallResult.Failure>(member.CallService("account", "GiveCashFromCorpAccount", [90000002L, 1d, 1000L]));
        var wallet = world.Services.GetRequiredService<ICorporationWallet>();
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, new(true, 1000, 90000002, 1000, 1, "Insufficient")));
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Add(
                new SavedCorporationWallet
                {
                    CorporationId = id,
                    Key = 1001,
                    BalanceCents = long.MaxValue,
                }
            );
            saved.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => wallet.Transfer(90000001, new(false, 1000, id, 1001, 1, "Overflow")));
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectWalletLedger BEFORE INSERT ON WorldWalletLedger
                BEGIN SELECT RAISE(ABORT,'test journal failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => wallet.Transfer(90000001, new(false, 1000, id, 1000, 1, "Rollback")));
        Assert.Equal(0, wallet.Balance(90000001, 1000));
        Assert.Equal(840020000, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        using WorldDbContext check = new(world.Path);
        Assert.Empty(check.Set<SavedWalletLedger>());
    }

    [Theory]
    [InlineData(0, 0.1, 0)]
    [InlineData(10000000, 0.1, 0)]
    [InlineData(10000001, 0.1, 1000000)]
    [InlineData(25000000, 0, 0)]
    [InlineData(25000000, 0.1, 2500000)]
    [InlineData(25000000, 1, 25000000)]
    public void BountyTaxUsesThePinnedThresholdAndTruncatesToWholeCents(long gross, double rate, long expected) =>
        Assert.Equal(expected, CorporationWalletRules.Tax(gross, rate));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FatalNpcSettlementCreditsNetRewardAndCorporateTaxExactlyOnce(bool liveWorld)
    {
        using NpcCombatTests.Fixture fixture = new(liveWorld);
        var world = fixture.Db;
        world.Store.SetPosition(NpcCombatTests.Fixture.ShipId, null);
        PlayerCorporationTests.Prepare(world);
        int id = world.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details()).Corporation.Id;
        world.Store.SetPosition(NpcCombatTests.Fixture.ShipId, fixture.Npc.Position, fixture.Npc.SystemId);
        long now = world.Services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime.ToFileTimeUtc();
        using (WorldDbContext saved = new(world.Path))
        using (var transaction = saved.Database.BeginTransaction())
        using (CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath))
        {
            var npc = saved.Set<WorldNpc>().Find(fixture.Npc.Id)!;
            var pilot = saved.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == 90000001);
            var attributes = catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == npc.TypeId)
                .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
            attributes[DogmaAttributeId.EntityKillBounty] = 250000;
            var killed = NpcDamagePersistence.Apply(
                saved,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                attributes,
                new DamageVector(1_000_000, 1_000_000, 1_000_000, 1_000_000),
                now
            );
            Assert.True(killed.Destroyed);
            Assert.Equal(25000000, killed.Reward!.AmountCents);
            Assert.Equal(2500000, killed.Reward.TaxCents);
            Assert.Equal(862520000, killed.Reward.BalanceCents);
            saved.SaveChanges();
            transaction.Commit();
            Assert.Throws<InvalidDataException>(() =>
                NpcDamagePersistence.Apply(saved, catalog, npc, pilot, pilot.ActiveShipId, attributes, new DamageVector(1, 1, 1, 1), now)
            );
        }
        Assert.Equal(2500000, world.Services.GetRequiredService<ICorporationWallet>().Balance(90000001, 1000));
        Assert.Equal(862520000, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        // Read the journal at the fixture's fully catalogued home station.
        world.Store.SetPosition(NpcCombatTests.Fixture.ShipId, null, 30000142);
        using CorporationTestClient ceo = new(world, 1000001);
        var personal = Assert
            .IsType<BlueList<WalletJournalEntry>>(
                Assert.IsType<PilotCallResult.Success>(ceo.CallService("account", "GetTransactions", [1000L, null, null, false])).Reply
            )
            .Items;
        var bounty = Assert.Single(personal, e => e.Type == WalletEntryType.BountyPrizes);
        Assert.Equal(25000000, bounty.AmountCents);
        Assert.Equal(865020000, bounty.BalanceCents);
        var tax = Assert.Single(personal, e => e.Type == CorporationWalletRules.BountyTaxEntry);
        Assert.Equal(-2500000, tax.AmountCents);
        Assert.Equal(862520000, tax.BalanceCents);
        Assert.Equal(id, tax.OwnerId2);
        Assert.Single(world.Services.GetRequiredService<ICorporationWallet>().Journal(90000001, true, 1000));
    }

    [Fact]
    public void Version75UpgradeRetainsTheMasterWalletAndAddsEmptyDivisionAndTaxState()
    {
        using TestWorld world = new();
        int id = CorporationAdministrationTests.CreateAndJoin(world);
        world.Services.Dispose();
        using (WorldDbContext saved = new(world.Path))
        {
            saved.Database.ExecuteSqlRaw(
                """
                UPDATE WorldCorporation SET BalanceCents=12345;
                DROP TABLE WorldCorporationWallet;
                DROP TABLE WorldCorporationWalletSelection;
                DROP TABLE WorldWalletLedger;
                ALTER TABLE WorldNpcBounty DROP COLUMN TaxCents;
                PRAGMA user_version=75;
                """
            );
        }
        WorldInitialization.Initialize(new(world.Path));
        using var reopened = ServerServices.Build(new(world.Path));
        var wallet = reopened.GetRequiredService<ICorporationWallet>();
        Assert.Equal(12345, wallet.Balance(90000001, 1000));
        Assert.Equal(0, wallet.Balance(90000001, 1001));
        Assert.Equal(7, wallet.Divisions(90000001).Count);
        Assert.Empty(wallet.Journal(90000001, true, 1000));
        Assert.Equal(1000, reopened.GetRequiredService<IPlayerCorporations>().Identity(90000001).AccountKey);
        wallet.Transfer(90000001, new(true, 1000, id, 1001, 12345, "Migrated"));
        Assert.Equal(12345, wallet.Balance(90000001, 1001));
        using WorldDbContext check = new(world.Path);
        Assert.Equal(WorldMigration.CurrentVersion, check.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void JournalReasonsRemainInertNativeDescriptionText()
    {
        const string reason = "92: [98000000, 999999999]\n\"quoted\" 星";
        WalletJournalEntry entry = new(
            1,
            1,
            (WalletEntryType)CorporationWalletRules.PlayerDonationEntry,
            90000001,
            98000000,
            90000001,
            1,
            1,
            reason
        );
        var fields = BlueRead.Fields(Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(entry))).State);
        using var description = JsonDocument.Parse(BlueMarshal.Text(fields["description"]));
        var property = Assert.Single(description.RootElement.EnumerateObject());
        Assert.Equal("DESC", property.Name);
        Assert.Equal(reason, property.Value.GetString());
    }

    [Fact]
    public void AFailedTaxJournalWriteRollsBackTheKillRewardAndCorporationCreditTogether()
    {
        using NpcCombatTests.Fixture fixture = new();
        var world = fixture.Db;
        world.Store.SetPosition(NpcCombatTests.Fixture.ShipId, null);
        PlayerCorporationTests.Prepare(world);
        world.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, PlayerCorporationTests.Details());
        world.Store.SetPosition(NpcCombatTests.Fixture.ShipId, fixture.Npc.Position, fixture.Npc.SystemId);
        using (WorldDbContext setup = new(world.Path))
        {
            setup.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectTax BEFORE INSERT ON WorldWalletLedger
                BEGIN SELECT RAISE(ABORT,'test tax journal failure'); END;
                """
            );
        }
        Assert.Throws<DbUpdateException>(() =>
        {
            using WorldDbContext saved = new(world.Path);
            using var transaction = saved.Database.BeginTransaction();
            using CatalogDbContext catalog = new(new WorldDatabase(world.Path).CatalogPath);
            var npc = saved.Set<WorldNpc>().Find(fixture.Npc.Id)!;
            var pilot = saved.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == 90000001);
            var attributes = catalog
                .Set<TypeAttribute>()
                .Where(a => a.ItemTypeId == npc.TypeId)
                .ToDictionary(a => (DogmaAttributeId)a.AttributeId, a => a.Value);
            attributes[DogmaAttributeId.EntityKillBounty] = 250000;
            NpcDamagePersistence.Apply(
                saved,
                catalog,
                npc,
                pilot,
                pilot.ActiveShipId,
                attributes,
                new DamageVector(1_000_000, 1_000_000, 1_000_000, 1_000_000),
                DateTime.UtcNow.ToFileTimeUtc()
            );
            saved.SaveChanges();
            transaction.Commit();
        });
        Assert.Equal(840020000, world.Services.GetRequiredService<IPilotWalletReader>().Balance(90000001));
        Assert.Equal(0, world.Services.GetRequiredService<ICorporationWallet>().Balance(90000001, 1000));
        using WorldDbContext check = new(world.Path);
        Assert.Null(check.Set<WorldNpc>().Find(fixture.Npc.Id)!.DestroyedAt);
        Assert.Empty(check.Set<NpcBounty>());
        Assert.Empty(check.Set<SavedWalletLedger>());
    }
}
