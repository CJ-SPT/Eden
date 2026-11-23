using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ShipSkinLicenseTests
{
    private const long OwnerAccount = 1000001;
    private const long OtherAccount = 1000002;
    private const int Merlin = 603;
    private const int Apocalypse = 642;
    private const int Punisher = 597;
    private const int PermanentLicense = 34734;
    private const int TimedLicense = 34917;
    private const int VolatileLicense = 46585;
    private const int MerlinSkin = 156;
    private const int ApocalypseSkin = 195;
    private const int PunisherSkin = 4698;
    private const int SevenDays = 7;

    [Fact]
    public async Task NativeActivationCommitsInventoryAndLicensesTogetherAndRenewsAtExpiryAfterRestart()
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
        using TestWorld world = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(world.Path);
        LoadSkins(database.CatalogPath);
        var live = world.Services.GetRequiredService<LiveWorld>();
        var owner = world.Store.FindPilot(OwnerAccount)!;
        var other = world.Store.FindPilot(OtherAccount)!;
        var permanent = new ShipSkinLicense(MerlinSkin, null, false);
        long expiry = clock.Now + TimeSpan.FromDays(SevenDays).Ticks;
        var timed = new ShipSkinLicense(ApocalypseSkin, expiry, false);
        var singleUse = new ShipSkinLicense(PunisherSkin, null, true);

        long permanentStack,
            spareStack,
            timedStack,
            singleUseStack,
            inaccessible,
            foreign;
        using (var setup = live.Economy.Begin(new(pilots: [owner.PilotId, other.PilotId])))
        {
            permanentStack = Stock(owner.PilotId, owner.StationId, InventoryFlag.Hangar, PermanentLicense, 2);
            spareStack = Stock(owner.PilotId, owner.StationId, InventoryFlag.Hangar, PermanentLicense, 1);
            timedStack = Stock(owner.PilotId, owner.ShipId, InventoryFlag.Cargo, TimedLicense, 2);
            singleUseStack = Stock(owner.PilotId, owner.StationId, InventoryFlag.Hangar, VolatileLicense, 1);
            inaccessible = Stock(owner.PilotId, other.ShipId, InventoryFlag.Cargo, TimedLicense, 1);
            foreign = Stock(other.PilotId, other.StationId, InventoryFlag.Hangar, TimedLicense, 1);
            setup.Commit();

            long Stock(long holder, long location, InventoryFlag flag, int type, int amount)
            {
                long id = setup.NextItemId();
                setup.Add(
                    new OwnedItem
                    {
                        Id = id,
                        OwnerId = holder,
                        LocationId = location,
                        FlagId = (int)flag,
                        TypeId = type,
                        Quantity = amount,
                    }
                );
                return id;
            }
        }

        var inventory = world.Services.GetRequiredService<IPilotInventory>();
        using (Client ownerClient = new(world.Services, OwnerAccount))
        using (Client otherClient = new(world.Services, OtherAccount))
        {
            Assert.IsType<PilotCallResult.Failure>(ownerClient.Activate(permanentStack, inaccessible));
            Assert.IsType<PilotCallResult.Failure>(ownerClient.Activate(permanentStack, foreign));
            Assert.IsType<PilotCallResult.Failure>(ownerClient.Activate(permanentStack, spareStack));
            Assert.Empty(ownerClient.List());
            Assert.Empty(ownerClient.Notifications());
            Assert.Equal(2, Quantity(inventory, owner.PilotId, owner.StationId, permanentStack));
            Assert.Equal(1, Quantity(inventory, owner.PilotId, owner.StationId, spareStack));
            Assert.Equal(1, Quantity(inventory, owner.PilotId, other.ShipId, inaccessible));
            Assert.Equal(1, Quantity(inventory, other.PilotId, other.StationId, foreign));

            Assert.Same(PilotCallResult.None, ownerClient.Activate(permanentStack, timedStack, singleUseStack, permanentStack));
            var notifications = ownerClient.Notifications();
            Assert.Equal(6, notifications.Length);
            AssertActivation(notifications, 0, owner.PilotId, permanentStack, MerlinSkin);
            AssertActivation(notifications, 2, owner.PilotId, timedStack, ApocalypseSkin);
            AssertActivation(notifications, 4, owner.PilotId, singleUseStack, PunisherSkin);
            Assert.Equal(1, Quantity(inventory, owner.PilotId, owner.StationId, permanentStack));
            Assert.Equal(1, Quantity(inventory, owner.PilotId, owner.ShipId, timedStack));
            Assert.Equal(0, Quantity(inventory, owner.PilotId, owner.StationId, singleUseStack));

            Assert.Equal(new[] { permanent, timed, singleUse }, ownerClient.List().OrderBy(license => license.SkinId));
            Assert.Equal(permanent, Assert.Single(ownerClient.List(Merlin)));
            Assert.Equal(timed, Assert.Single(ownerClient.List(Apocalypse)));
            Assert.Equal(singleUse, Assert.Single(ownerClient.List(Punisher)));
            Assert.Empty(ownerClient.List(owner.ShipTypeId));
            Assert.Equal(timed, ownerClient.Detail(owner.PilotId, ApocalypseSkin));
            Assert.Empty(otherClient.List());
            Assert.Null(ownerClient.Detail(other.PilotId, MerlinSkin));
            Assert.Equal(permanent, otherClient.Detail(owner.PilotId, MerlinSkin));
            Assert.Empty(otherClient.Notifications());

            Assert.IsType<PilotCallResult.Failure>(ownerClient.Activate(timedStack));
            Assert.IsType<PilotCallResult.Failure>(ownerClient.Activate(singleUseStack));
            Assert.Equal(notifications, ownerClient.Notifications());
            Assert.Equal(1, Quantity(inventory, owner.PilotId, owner.ShipId, timedStack));
            Assert.Equal(timed, ownerClient.Detail(owner.PilotId, ApocalypseSkin));
        }

        await live.FlushAsync();
        world.Services.Dispose();
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var store = reopened.GetRequiredService<SqliteWorld>();
        store.SetPosition(owner.ShipId, new SpacePosition(1_000_000, 0, 0));
        using Client restored = new(reopened, OwnerAccount);
        Assert.NotNull(restored.Pilot.Position);
        Assert.Equal(new[] { permanent, timed, singleUse }, restored.List().OrderBy(license => license.SkinId));

        clock.AdvanceWallTime(TimeSpan.FromTicks(expiry - clock.Now - 1));
        Assert.Equal(timed, restored.Detail(owner.PilotId, ApocalypseSkin));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Null(restored.Detail(owner.PilotId, ApocalypseSkin));
        Assert.Empty(restored.List(Apocalypse));
        Assert.Equal(new[] { permanent, singleUse }, restored.List().OrderBy(license => license.SkinId));

        Assert.Same(PilotCallResult.None, restored.Activate(timedStack));
        AssertActivation(restored.Notifications(), 0, owner.PilotId, timedStack, ApocalypseSkin);
        Assert.Equal(2, restored.Notifications().Length);
        Assert.Equal(timed with { Expires = expiry + TimeSpan.FromDays(SevenDays).Ticks }, Assert.Single(restored.List(Apocalypse)));
        var restoredInventory = reopened.GetRequiredService<IPilotInventory>();
        Assert.Equal(0, Quantity(restoredInventory, owner.PilotId, owner.ShipId, timedStack));
        Assert.Equal(1, Quantity(restoredInventory, owner.PilotId, owner.StationId, permanentStack));
    }

    private static int Quantity(IPilotInventory inventory, long holder, long location, long item) =>
        inventory.List(holder, location).SingleOrDefault(row => row.Id == item)?.Quantity ?? 0;

    private static void AssertActivation(PilotNotification[] events, int offset, long pilot, long item, int skin)
    {
        var change = Assert.IsType<ItemChanged>(events[offset]);
        Assert.Equal(item, change.Item.Id);
        Assert.Equal(pilot, change.Item.OwnerId);
        var activation = events[offset + 1];
        Assert.Equal("OnSkinLicenseActivated", activation.Name);
        Assert.Equal(pilot, activation.BroadcastCharacterId);
        Assert.Equal(
            new object?[] { (long)skin, pilot },
            Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(activation.Arguments)))
        );
    }

    private static void LoadSkins(string path)
    {
        PassiveCatalogFixture.LoadSmallHulls(path, new HashSet<int> { Merlin, Punisher });
        PassiveCatalogFixture.LoadCombatHulls(path, new HashSet<int> { Apocalypse });
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Add(new ItemCategory { Id = (int)InventoryCategory.ShipSkin, Name = "SKIN" });
        // Minimal catalog definitions from build 3662168. SKIN and license record keys are JSON strings.
        AddLicense(
            PermanentLicense,
            MerlinSkin,
            1950,
            "Permanent SKIN",
            "Merlin Nugoeihuvi SKIN",
            """{"duration":-1,"skinID":156,"licenseTypeID":34734}""",
            """{"internalName":"Merlin Nugoeihuvi","skinMaterialID":10,"visibleTranquility":true,"skinID":156,"types":[603]}"""
        );
        AddLicense(
            TimedLicense,
            ApocalypseSkin,
            1952,
            "7-Day SKIN",
            "Apocalypse Blood Raiders SKIN (7 Days)",
            """{"duration":7,"skinID":195,"licenseTypeID":34917}""",
            """{"internalName":"Apocalypse Blood Raiders","skinMaterialID":25,"visibleTranquility":true,"skinID":195,"types":[642]}"""
        );
        AddLicense(
            VolatileLicense,
            PunisherSkin,
            1951,
            "Volatile SKIN",
            "Punisher War Reserves Lieutenant SKIN (Volatile)",
            """{"duration":-1,"skinID":4698,"isSingleUse":true,"licenseTypeID":46585}""",
            """
            {"internalName":"Punisher War Reserves Lieutenant","skinMaterialID":209,
             "visibleTranquility":true,"skinID":4698,"types":[597]}
            """
        );
        catalog.SaveChanges();

        void AddLicense(int type, int skin, int group, string groupName, string itemName, string licenseData, string skinData)
        {
            catalog.Add(
                new ItemGroup
                {
                    Id = group,
                    Name = groupName,
                    CategoryId = (int)InventoryCategory.ShipSkin,
                }
            );
            catalog.Add(
                new ItemType
                {
                    Id = type,
                    Name = itemName,
                    GroupId = group,
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = CatalogData.Client.Types.Name,
                    Key = type.ToString(CultureInfo.InvariantCulture),
                    Data = """{"volume":0.01,"published":1}""",
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = CatalogData.Client.Skinlicenses.Name,
                    Key = Key(type),
                    Data = licenseData,
                }
            );
            catalog.Add(
                new SourceRecord
                {
                    Dataset = CatalogData.Client.Skins.Name,
                    Key = Key(skin),
                    Data = skinData,
                }
            );
        }

        static string Key(int id) => JsonSerializer.Serialize(id.ToString(CultureInfo.InvariantCulture));
    }

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotCallContext _context;
        private readonly PilotCallRouter _router;

        public Client(IServiceProvider services, long account)
        {
            var pilot = services.GetRequiredService<SqliteWorld>().FindPilot(account)!;
            _scope = services.CreateScope();
            _router = _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var factory = _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            _context = new(account, pilot.Account, [pilot], factory.Create, account, SignedSettings.Empty);
            _context.Select(pilot);
        }

        public DockedPilot Pilot => _context.Pilot;

        public PilotCallResult Activate(params long[] items) => Call("ActivateSkinLicense", [items.Select(item => (object?)item).ToList()]);

        public IEnumerable<ShipSkinLicense> List(int? hull = null) =>
            Assert
                .IsType<List<object?>>(
                    hull is int type ? Wire("GetLicencedSkinsForShipType", [Pilot.PilotId, (long)type]) : Wire("GetLicencedSkins", [])
                )
                .Select(ReadLicense);

        public ShipSkinLicense? Detail(long licensee, int skin) =>
            Wire("GetFirstPartySkinData", [licensee, (long)skin]) is { } reply ? ReadLicense(reply) : null;

        public PilotNotification[] Notifications() => _context.NotificationsBeforeFlight.Concat(_context.Notifications).ToArray();

        private PilotCallResult Call(string method, object?[] args) =>
            _router.Call(_context, "shipCosmeticsMgr", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        private object? Wire(string method, object?[] args) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply));

        private static ShipSkinLicense ReadLicense(object? reply)
        {
            var keyVal = Assert.IsType<BlueInstance>(reply);
            Assert.Equal("utillib.KeyVal", keyVal.Name);
            var fields = BlueRead.Fields(keyVal.State);
            Assert.Equal(new[] { "expires", "isSingleUse", "skinID" }, fields.Keys.Order());
            return new(
                checked((int)Assert.IsType<long>(fields["skinID"])),
                fields["expires"] is { } time ? Assert.IsType<long>(time) : null,
                Assert.IsType<bool>(fields["isSingleUse"])
            );
        }

        public void Dispose()
        {
            _context.Dispose();
            _scope.Dispose();
        }
    }
}
