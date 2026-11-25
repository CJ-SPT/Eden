using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class LoyaltyStoreSkinLicenseTests
{
    private const int Concord = LoyaltyStoreRules.ConcordCorporation;
    private const int Ded = 1000137;
    private const int PoliceCometOffer = 16071;
    private const int TwilightAlgosOffer = 16348;
    private const int UnmappedTwilightOffer = 16349;
    private const int RequiredSurveyData = 48121;
    private const long StartingPoints = 100_000;
    private const long StartingCents = 10_000_000_000;

    [Theory]
    [InlineData(Concord, PoliceCometOffer, 34718, 140, 17841, 37500, 0)]
    [InlineData(Ded, TwilightAlgosOffer, 52398, 7979, 32872, 10000, 100)]
    public async Task NativeLoyaltyStoreLicensesCanBePurchasedActivatedAndAppliedAcrossCheckpoint(
        int issuer,
        int offerId,
        int type,
        int skin,
        int hull,
        long pricePoints,
        int requiredQuantity
    )
    {
        const int count = 2;
        const long priceCents = 2_500_000_000;
        var clock = new TestClock();
        using TestWorld db = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(db.Path);
        var pilot = db.Store.FindPilot(1000001)!;
        SeedCatalog(database, pilot.StationId, issuer, type, skin, hull);
        var live = db.Services.GetRequiredService<LiveWorld>();
        using (var action = live.Economy.Begin(new(pilots: [pilot.PilotId])))
        {
            action.Ships.Find(pilot.ShipId)!.TypeId = hull;
            action.Add(new PilotWallet { PilotId = pilot.PilotId, Cents = StartingCents });
            action.Add(
                new SavedLoyaltyBalance
                {
                    PilotId = pilot.PilotId,
                    IssuerCorporationId = issuer,
                    Points = StartingPoints,
                }
            );
            action.Commit();
        }

        int surveyRemainder = requiredQuantity > 0 ? 25 : 0;
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        using (CorporationTestClient client = new(db, 1000001))
        {
            var rows = Assert.IsType<List<object?>>(Reply(client.CallService("LPStoreMgr", "GetAvailableOffersFromCorp", [(long)issuer])));
            var offers = rows.Select(row => BlueRead.Fields(Assert.IsType<BlueInstance>(row).State)).ToArray();
            var offer = Assert.Single(offers, row => Equals(row["offerID"], (long)offerId));
            Assert.Equal((long)type, offer["typeID"]);
            Assert.Equal(pricePoints, offer["lpCost"]);
            Assert.Equal(25_000_000d, offer["iskCost"]);
            Assert.DoesNotContain(offers, row => Equals(row["offerID"], (long)UnmappedTwilightOffer));

            int wrongIssuer = issuer == Concord ? Ded : Concord;
            Assert.IsType<PilotCallResult.Failure>(
                client.CallService("LPStoreMgr", "TakeOfferForCharacter", [(long)wrongIssuer, (long)offerId, 1L])
            );
            if (requiredQuantity > 0)
            {
                Assert.IsType<PilotCallResult.Failure>(Buy());
                Assert.Equal(StartingCents, wallet.Balance(pilot.PilotId));
                Assert.Empty(wallet.Journal(pilot.PilotId).ServicePayments ?? []);
                using var action = live.Economy.Begin(new(pilots: [pilot.PilotId]));
                action.Add(
                    new OwnedItem
                    {
                        Id = action.NextItemId(),
                        OwnerId = pilot.PilotId,
                        LocationId = pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        TypeId = RequiredSurveyData,
                        Quantity = requiredQuantity * count + surveyRemainder,
                    }
                );
                action.Commit();
            }

            Assert.Equal(true, Reply(Buy()));
            long itemId = Assert.Single(client.Context.Notifications.OfType<ItemChanged>(), change => change.Item.TypeId == type).Item.Id;
            Assert.Equal(count, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), item => item.Id == itemId).Quantity);
            Assert.Null(db.Services.GetRequiredService<IPilotShipSkinLicenses>().Read(pilot.PilotId, skin));
            Assert.Same(
                PilotCallResult.None,
                client.CallService("shipCosmeticsMgr", "ActivateSkinLicense", [new List<object?> { itemId }])
            );
            Assert.Equal(skin, Assert.Single(client.Context.Notifications.OfType<ShipSkinLicenseActivated>()).SkinId);
            Assert.Same(PilotCallResult.None, client.CallService("shipCosmeticsMgr", "ApplySkinToShip", [pilot.ShipId, (long)skin]));

            PilotCallResult Buy() => client.CallService("LPStoreMgr", "TakeOfferForCharacter", [(long)issuer, (long)offerId, (long)count]);
        }

        await live.FlushAsync();
        db.Services.Dispose();
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var hangar = reopened.GetRequiredService<IPilotInventory>().List(pilot.PilotId, pilot.StationId);
        Assert.Equal(1, Assert.Single(hangar, item => item.TypeId == type).Quantity);
        Assert.Equal(surveyRemainder, hangar.Where(item => item.TypeId == RequiredSurveyData).Sum(item => item.Quantity));
        Assert.Equal(
            new ShipSkinLicense(skin, null, false),
            reopened.GetRequiredService<IPilotShipSkinLicenses>().Read(pilot.PilotId, skin)
        );
        Assert.Equal(skin, reopened.GetRequiredService<IPilotShipSkins>().ReadOwned(pilot.PilotId, pilot.ShipId)!.SkinId);
        Assert.Equal(
            StartingPoints - pricePoints * count,
            Assert.Single(reopened.GetRequiredService<IPilotLoyaltyWallet>().Read(pilot.PilotId)).Points
        );
        Assert.Equal(StartingCents - priceCents * count, reopened.GetRequiredService<IPilotWalletReader>().Balance(pilot.PilotId));
        var receipt = Assert.Single(reopened.GetRequiredService<IPilotWalletReader>().Journal(pilot.PilotId).ServicePayments!);
        Assert.Equal((issuer, (long)offerId, -priceCents * count), (receipt.CounterpartyId, receipt.ContextId, receipt.AmountCents));
    }

    private static object? Reply(PilotCallResult result)
    {
        Assert.True(result is PilotCallResult.Success, result.ToString());
        return BlueMarshal.Decode(BlueMarshal.Encode(((PilotCallResult.Success)result).Reply));
    }

    private static void SeedCatalog(WorldDatabase database, int station, int issuer, int license, int skin, int hull)
    {
        PassiveCatalogFixture.LoadSmallHulls(database.CatalogPath, new HashSet<int> { hull });
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO NpcCorporation VALUES (1000125,'CONCORD'),(1000137,'DED');
            INSERT OR IGNORE INTO ItemCategory VALUES (17,'Commodity'),(91,'SKIN');
            INSERT OR IGNORE INTO ItemGroup VALUES (1950,'Permanent SKIN',91),(280,'Data',17);
            INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                (34718,'Federation Navy Comet Police SKIN',1950),(52398,'Algos Triglavian Twilight SKIN',1950),
                (52392,'Apocalypse Triglavian Twilight SKIN',1950),(48121,'Triglavian Survey Database',280);
            """
        );
        catalog.Database.ExecuteSql($"UPDATE Station SET OwnerId={issuer} WHERE Id={station}");
        Record("client/stationoperations", 14, new { services = new[] { 7, 17, 25 } });
        Record("client/types", license, new { volume = 0.01, published = 1 });
        Record("client/types", 52392, new { volume = 0.01, published = 1 });
        Record("client/types", RequiredSurveyData, new { volume = 0.1, published = 1 });
        Record(
            "client/skinlicenses",
            license.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new
            {
                duration = -1,
                skinID = skin,
                licenseTypeID = license,
            }
        );
        Record(
            "client/skins",
            skin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new { skinID = skin, types = new[] { hull } }
        );

        void Record(string dataset, object key, object value) =>
            catalog.Database.ExecuteSql(
                $"INSERT OR REPLACE INTO SourceRecord VALUES ({dataset},{JsonSerializer.Serialize(key)},{JsonSerializer.Serialize(value)})"
            );
    }
}
