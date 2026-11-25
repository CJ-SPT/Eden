using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class SecurityOfficeTests
{
    private const string Crimewatch = "crimewatch";
    private const string ApplyTags = "ApplyTagsForSecStatus";
    private const long Account = 1000001;
    private const long Pilot = 90000001;
    private const long OtherPilot = 90000002;
    private const int Office = 60012334;
    private const int OtherStation = 60003760;
    private const int Transporter = 33140;
    private const int Negotiator = 33141;
    private const double StartingStatus = -2.25;
    private const long StartingBalance = 5_000_000_000;
    private const long Fee = 2_900_000_000;

    [Fact]
    public async Task BoundExchangeConsumesLocalTagsAndPersistsWalletAndSecurityTogether()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = Prepare(db);
        using (var action = live.Economy.Begin())
        {
            SeedTag(action, Transporter, 1);
            SeedTag(action, Negotiator, 1);
            SeedTag(action, Negotiator, 4);
            SeedTag(action, Transporter, 2, owner: OtherPilot);
            action.Commit();
        }

        using (CourierServiceTests.Client client = new(db, clock, Account))
        {
            client.Refresh();
            client.Context.Notifications.Clear();
            var result = client.Call(
                "MachoBindObject",
                [new object?[] { (long)Office, 15L }, new object?[] { ApplyTags, new object?[] { 0d, StartingStatus }, null }],
                Crimewatch
            );
            Assert.True(result is PilotCallResult.Success, result.ToString());
            var response = (PilotCallResult.Success)result;
            Assert.Null(Assert.IsType<BoundPilotReply>(response.Reply).Reply.ToBlue());
            Assert.Equal(.25, Assert.Single(client.Context.Notifications.OfType<SecurityStatusUpdated>()).Value);
            Assert.Equal(21_000_000d, Assert.Single(client.Context.Notifications.OfType<AccountBalanceChanged>()).Balance);
            Assert.Equal(3, client.Context.Notifications.OfType<ItemChanged>().Count(n => n.Item.TypeId is Transporter or Negotiator));
            Assert.IsType<PilotCallResult.Failure>(client.Call(ApplyTags, [0d, StartingStatus], Crimewatch));
            Assert.Single(client.Context.Notifications.OfType<SecurityStatusUpdated>());
        }

        Assert.Equal(0, TagCount(live, Transporter));
        Assert.Equal(1, TagCount(live, Negotiator));
        Assert.Equal(2, TagCount(live, Transporter, OtherPilot));
        VerifyReceipt(db.Services);

        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(new(db.Path), clock, liveWorld: true);
        VerifyReceipt(reopened);
        var restored = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(0, TagCount(restored, Transporter));
        Assert.Equal(1, TagCount(restored, Negotiator));
        Assert.Equal(2, TagCount(restored, Transporter, OtherPilot));
    }

    [Fact]
    public void FailedExchangesPreserveTagsWalletAndSecurity()
    {
        var clock = CalendarTestData.Clock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        var live = Prepare(db, balance: 1);
        using (var action = live.Economy.Begin())
        {
            SeedTag(action, Transporter, 1);
            SeedTag(action, Negotiator, 4, owner: OtherPilot);
            SeedTag(action, Negotiator, 4, location: OtherStation);
            SeedTag(action, Negotiator, 4, location: action.Pilots.Find(Pilot)!.ActiveShipId, flag: InventoryFlag.Cargo);
            action.Commit();
        }

        using CourierServiceTests.Client client = new(db, clock, Account);
        client.Refresh();
        Reject(0d, -2d, "Your security status changed");
        Reject(double.NaN, StartingStatus, "Expected a finite number");
        Reject(-2d, StartingStatus, "You do not have enough ISK");
        VerifyUnchanged(expectedBalance: 1);

        using (var action = live.Economy.Begin())
        {
            action.Set<PilotWallet>().Find(Pilot)!.Cents = StartingBalance;
            action.Commit();
        }
        Reject(0d, StartingStatus, "Place the required security tags");
        VerifyUnchanged(StartingBalance);

        using (var action = live.Economy.Begin())
        {
            action.Ships.Find(action.Pilots.Find(Pilot)!.ActiveShipId)!.StationId = OtherStation;
            action.Commit();
        }
        client.Refresh();
        Reject(-2d, StartingStatus, "Dock at a low-security CONCORD or DED Security Office");
        VerifyUnchanged(StartingBalance);

        void Reject(double target, double quote, string reason) =>
            Assert.StartsWith(reason, Assert.IsType<PilotCallResult.Failure>(client.Call(ApplyTags, [target, quote], Crimewatch)).Message);

        void VerifyUnchanged(long expectedBalance)
        {
            var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
            var security = db.Services.GetRequiredService<IPilotSecurityStatus>();
            Assert.Equal(expectedBalance, wallet.Balance(Pilot));
            Assert.Empty(wallet.Journal(Pilot).ServicePayments ?? []);
            Assert.Equal(StartingStatus, security.Read(Pilot).Value);
            Assert.Empty(security.History(Pilot));
            Assert.Empty(client.Context.Notifications.OfType<SecurityStatusUpdated>());
            Assert.Equal(1, TagCount(live, Transporter));
            Assert.Equal(0, TagCount(live, Negotiator));
            Assert.Equal(4, TagCount(live, Negotiator, OtherPilot));
        }
    }

    private static LiveWorld Prepare(TestWorld db, long balance = StartingBalance)
    {
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO NpcCorporation(Id,Name) VALUES (1000125,'CONCORD');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30001385,20000020,'Jan');
                INSERT INTO Celestial(Id,SolarSystemId,TypeId,GroupId,Radius,X,Y,Z)
                    VALUES (40088235,30001385,52673,8,1620000,0,0,0);
                INSERT INTO Station(Id,SolarSystemId,TypeId,OwnerId,OperationId,OrbitId,X,Y,Z)
                    VALUES (60012334,30001385,52678,1000125,32,40088235,0,0,0);
                INSERT OR REPLACE INTO SourceRecord(Dataset,Key,Data) VALUES
                    ('client/systems','30001385','{{"securityStatus":0.4169539213180542}}'),
                    ('client/solarsystemcontent','30001385','{{"security":0.4169539213180542}}'),
                    ('client/stationoperations','32','{{"services":[27]}}'),
                    ('client/types','33140','{{"volume":0.1,"published":1,"marketGroupID":1700}}'),
                    ('client/types','33141','{{"volume":0.1,"published":1,"marketGroupID":1700}}');
                INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (17,'Commodity');
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (1206,'Security Tags',17);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (33140,'Transporter Tag',1206),(33141,'Negotiator Tag',1206);
                INSERT INTO DogmaAttribute(Id,Name) VALUES (1904,'securityProcessingFee');
                INSERT INTO TypeAttribute(ItemTypeId,AttributeId,Value) VALUES (33140,1904,5000000),(33141,1904,6000000);
                """
            );
        }
        var live = db.Services.GetRequiredService<LiveWorld>();
        using var action = live.Economy.Begin();
        action.Ships.Find(action.Pilots.Find(Pilot)!.ActiveShipId)!.StationId = Office;
        action.Add(new PilotWallet { PilotId = Pilot, Cents = balance });
        action.Add(new SavedSecurityStatus { PilotId = Pilot, Value = StartingStatus });
        action.Commit();
        return live;
    }

    private static void SeedTag(
        EconomyTransaction action,
        int type,
        int quantity,
        long owner = Pilot,
        long location = Office,
        InventoryFlag flag = InventoryFlag.Hangar
    ) =>
        action.Add(
            new OwnedItem
            {
                Id = action.NextItemId(),
                OwnerId = owner,
                TypeId = type,
                LocationId = location,
                FlagId = (int)flag,
                Quantity = quantity,
            }
        );

    private static int TagCount(LiveWorld live, int type, long owner = Pilot) =>
        live.Economy.Inspect(action =>
            action
                .Set<OwnedItem>()
                .Where(item => item.OwnerId == owner && item.LocationId == Office && item.FlagId == (int)InventoryFlag.Hangar)
                .Where(item => item.TypeId == type)
                .Sum(item => item.Quantity)
        );

    private static void VerifyReceipt(IServiceProvider services)
    {
        var status = services.GetRequiredService<IPilotSecurityStatus>();
        Assert.Equal(.25, status.Read(Pilot).Value);
        var entry = Assert.Single(status.History(Pilot));
        Assert.Equal(385, (int)entry.Event);
        Assert.Equal(.25, entry.NewValue);
        var wallet = services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(StartingBalance - Fee, wallet.Balance(Pilot));
        var payment = Assert.Single(wallet.Journal(Pilot).ServicePayments!);
        Assert.Equal(117, (int)payment.Kind);
        Assert.Equal(-Fee, payment.AmountCents);
        Assert.Equal(StartingBalance - Fee, payment.BalanceCents);
    }
}
