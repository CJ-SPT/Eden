using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Skills;

public sealed partial class ExpertSystemTests
{
    private const long OwnerAccount = 1000001;
    private const long OtherAccount = 1000002;
    private const int MiningBargeOperations = 57207;
    private const int CoreShipOperations = 57208;
    private const int SpaceExploration = 81098;
    private const int FactionWarfare = 81118;
    private const int Retriever = 17478;
    private const int StripMiner = 17482;
    private const int MiningBargeSkill = 17940;
    private const int SpaceshipCommand = 3327;
    private const double BaseHold = 27_500;
    private const double GrantedHold = 33_000;
    private const long Duration = 7 * TimeSpan.TicksPerDay;
    private const string ExpertService = "expertSystemMgr";
    private static readonly DateTimeOffset s_start = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NativeExpertSystemUnlocksRetrieverAndStripMinerUntilExactExpiryThenSupportsRemoval()
    {
        TestClock clock = new(s_start);
        using TestWorld world = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(world.Path);
        LoadCatalog(database.CatalogPath, includeLimitSystems: false);
        var live = world.Services.GetRequiredService<LiveWorld>();
        var owner = world.Store.FindPilot(OwnerAccount)!;
        var other = world.Store.FindPilot(OtherAccount)!;
        long trainedPoints = TrainedSkill.PointsForLevel(1, 2);
        long hull,
            module,
            spare,
            charges,
            foreign;
        using (var setup = live.Economy.Begin(new(pilots: [owner.PilotId, other.PilotId])))
        {
            setup.Set<Account>().Find(OwnerAccount)!.CloneGrade = AccountCloneGrade.Omega;
            setup.Set<PilotSkill>().Find(owner.PilotId, SpaceshipCommand)!.Points = trainedPoints;
            hull = setup.NextItemId();
            setup.Add(
                new Ship
                {
                    Id = hull,
                    OwnerId = owner.PilotId,
                    TypeId = Retriever,
                    StationId = owner.StationId,
                    Name = "Training Retriever",
                }
            );
            module = Stock(setup, owner, StripMiner, 1);
            spare = Stock(setup, owner, StripMiner, 1);
            charges = Stock(setup, owner, MiningBargeOperations, 3);
            foreign = Stock(setup, other, MiningBargeOperations, 1);
            setup.Commit();
        }

        var inventory = world.Services.GetRequiredService<IPilotInventory>();
        var actualSkills = world.Services.GetRequiredService<IPilotSkillReader>();
        var learned = actualSkills.ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId).ToArray();
        Assert.DoesNotContain(learned, skill => skill.TypeId == MiningBargeSkill);
        long installed = clock.Now;
        long expires = installed + 2 * Duration;
        using (Client client = new(world.Services, OwnerAccount))
        {
            Assert.Empty(client.Systems());
            Assert.IsType<PilotCallResult.Failure>(client.Board(hull));
            Assert.IsType<PilotCallResult.Failure>(client.Consume(foreign));
            Assert.Empty(client.Notices());
            Assert.Equal(1, Quantity(inventory, other, foreign));
            Assert.Equal(3, Quantity(inventory, owner, charges));
            Assert.DoesNotContain((long)MiningBargeSkill, client.Skills().Keys);
            AssertSkill(client.Skills()[SpaceshipCommand], SpaceshipCommand, 2, trainedPoints, 1d, null);

            Assert.Same(PilotCallResult.None, client.Consume(charges));
            AssertSystem(client.Systems(), installed, installed + Duration);
            var installation = client.Notices();
            var item = Assert.Single(installation.OfType<ItemChanged>());
            Assert.Equal(charges, item.Item.Id);
            Assert.Equal(2, item.Item.Quantity);
            AssertSystemNotice(installation, true, installed, installed + Duration);
            Assert.Equal(2, Quantity(inventory, owner, charges));
            var changes = Assert.Single(installation.OfType<ServerSkillsChanged>());
            Assert.Equal(installed, changes.Time);
            var changedRows = Assert.IsType<Dictionary<long, object?>>(WireAssertions.Materialize(changes.Arguments[0]));
            AssertSkill(changedRows[MiningBargeSkill], MiningBargeSkill, null, null, 4d, 4);
            AssertSkill(client.Skills()[SpaceshipCommand], SpaceshipCommand, 2, trainedPoints, 1d, 4);
            AssertSkill(client.Skills()[MiningBargeSkill], MiningBargeSkill, null, null, 4d, 4);
            Assert.Equal(learned, actualSkills.ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId));

            clock.AdvanceWallTime(TimeSpan.FromDays(1));
            Assert.Same(PilotCallResult.None, client.Consume(charges));
            AssertSystemNotice(client.Notices(), false, installed, expires);
            AssertSystem(client.Systems(), installed, expires);
            Assert.Equal(1, Quantity(inventory, owner, charges));
            Assert.IsType<PilotCallResult.Success>(client.Board(hull));
            Assert.Equal(hull, client.Pilot.ShipId);
            Assert.Same(PilotCallResult.None, client.Fit(module));
            Assert.Contains(
                inventory.List(owner.PilotId, hull),
                installedItem => installedItem.Id == module && InventoryDefinitions.IsFittedSlot(installedItem.FlagId)
            );
            Assert.Equal(GrantedHold, Hold(world.Services, owner.PilotId));
        }

        await live.FlushAsync();
        world.Services.Dispose();
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        using Client restored = new(reopened, OwnerAccount);
        Assert.Equal(hull, restored.Pilot.ShipId);
        AssertSystem(restored.Systems(), installed, expires);
        AssertSkill(restored.Skills()[MiningBargeSkill], MiningBargeSkill, null, null, 4d, 4);
        Assert.Equal(GrantedHold, Hold(reopened, owner.PilotId));
        clock.AdvanceWallTime(TimeSpan.FromTicks(expires - clock.Now - 1));
        Assert.Equal(GrantedHold, Hold(reopened, owner.PilotId));
        restored.Advance();
        Assert.Empty(restored.Notices());
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(BaseHold, Hold(reopened, owner.PilotId));
        Assert.IsType<PilotCallResult.Failure>(restored.Fit(spare));
        restored.Advance();

        var expiration = restored.Notices();
        AssertSystemNotice(expiration, false);
        var removal = Assert.Single(expiration.OfType<ServerSkillsRemoved>());
        Assert.Equal(expires, removal.Time);
        var deleted = Assert.IsType<Dictionary<long, object?>>(WireAssertions.Materialize(removal.Arguments[0]));
        var tombstone = Assert.IsType<BlueConstructor>(deleted[MiningBargeSkill]);
        Assert.Equal(-1L, tombstone.Arguments[2]);
        Assert.Null(tombstone.Arguments[4]);
        Assert.DoesNotContain((long)SpaceshipCommand, deleted.Keys);
        Assert.Empty(restored.Systems());
        Assert.DoesNotContain((long)MiningBargeSkill, restored.Skills().Keys);
        AssertSkill(restored.Skills()[SpaceshipCommand], SpaceshipCommand, 2, trainedPoints, 1d, null);
        var inbox = reopened.GetRequiredService<IPilotInbox>();
        var expiryNotice = Assert.Single(
            inbox.Read(owner.PilotId, new()),
            notice => notice.Type == InboxNotificationType.ExpertSystemExpired
        );
        Assert.Equal(MiningBargeOperations, expiryNotice.ExpertSystemTypeId);
        Assert.Equal(expires, expiryNotice.Created);

        Assert.Same(PilotCallResult.None, restored.Consume(charges));
        AssertSystemNotice(restored.Notices(), true, expires, expires + Duration);
        AssertSystem(restored.Systems(), expires, expires + Duration);
        Assert.Equal(0, Quantity(reopened.GetRequiredService<IPilotInventory>(), owner, charges));
        Assert.Equal(GrantedHold, Hold(reopened, owner.PilotId));
        Assert.Same(PilotCallResult.None, restored.Remove(MiningBargeOperations));
        AssertSystemNotice(restored.Notices(), false);
        Assert.Empty(restored.Systems());
        Assert.Equal(BaseHold, Hold(reopened, owner.PilotId));
        Assert.Equal(learned, reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(owner.PilotId).OrderBy(skill => skill.TypeId));
        Assert.Single(inbox.Read(owner.PilotId, new()), notice => notice.Type == InboxNotificationType.ExpertSystemExpired);
    }

    [Fact]
    public void NativeExpertSystemLimitsPreserveItemsAndAllowTopUpWhileSlotsAreFull()
    {
        TestClock clock = new(s_start);
        using TestWorld world = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(world.Path);
        LoadCatalog(database.CatalogPath, includeLimitSystems: true);
        database.Catalog.PrepareRuntime();
        var owner = world.Store.FindPilot(OwnerAccount)!;
        var inventory = world.Services.GetRequiredService<IPilotInventory>();
        long mining,
            core,
            exploration,
            warfare;
        using (var setup = world.Services.GetRequiredService<LiveWorld>().Economy.Begin(new(pilots: [owner.PilotId])))
        {
            mining = Stock(setup, owner, MiningBargeOperations, 6);
            core = Stock(setup, owner, CoreShipOperations, 1);
            exploration = Stock(setup, owner, SpaceExploration, 1);
            warfare = Stock(setup, owner, FactionWarfare, 1);
            setup.Commit();
        }

        using Client client = new(world.Services, OwnerAccount);
        using (world.Services.GetRequiredService<IPilotOperationScope>().Begin())
        using (GameplaySqlGuard.Begin())
        {
            Assert.Empty(client.Systems());
            Assert.Same(PilotCallResult.None, client.Consume(mining));
            Assert.Same(PilotCallResult.None, client.Consume(core));
            Assert.Same(PilotCallResult.None, client.Consume(exploration));
            Assert.Equal(new long[] { MiningBargeOperations, CoreShipOperations, SpaceExploration }, client.Systems().Keys.Order());
        }
        _ = client.Notices();
        Assert.IsType<PilotCallResult.Failure>(client.Consume(warfare));
        Assert.Equal(1, Quantity(inventory, owner, warfare));
        Assert.Empty(client.Notices());

        for (int copy = 2; copy <= 5; copy++)
        {
            Assert.Same(PilotCallResult.None, client.Consume(mining));
            Assert.Equal(new object?[] { clock.Now, clock.Now + copy * Duration }, client.Systems()[MiningBargeOperations]);
        }
        _ = client.Notices();
        Assert.IsType<PilotCallResult.Failure>(client.Consume(mining));
        Assert.Equal(1, Quantity(inventory, owner, mining));
        Assert.Equal(new object?[] { clock.Now, clock.Now + 5 * Duration }, client.Systems()[MiningBargeOperations]);
        Assert.Empty(client.Notices());

        Assert.Same(PilotCallResult.None, client.Remove(CoreShipOperations));
        Assert.Same(PilotCallResult.None, client.Consume(warfare));
        Assert.Equal(0, Quantity(inventory, owner, warfare));
        Assert.Equal(new long[] { MiningBargeOperations, SpaceExploration, FactionWarfare }, client.Systems().Keys.Order());
    }

    private static long Stock(EconomyTransaction setup, DockedPilot pilot, int type, int quantity)
    {
        long id = setup.NextItemId();
        setup.Add(
            new OwnedItem
            {
                Id = id,
                OwnerId = pilot.PilotId,
                LocationId = pilot.StationId,
                FlagId = (int)InventoryFlag.Hangar,
                TypeId = type,
                Quantity = quantity,
            }
        );
        return id;
    }

    private static int Quantity(IPilotInventory inventory, DockedPilot pilot, long item) =>
        inventory.List(pilot.PilotId, pilot.StationId).SingleOrDefault(row => row.Id == item)?.Quantity ?? 0;

    private static double Hold(IServiceProvider services, long pilot) =>
        services.GetRequiredService<IPilotFitting>().ShipAttributes(pilot)[DogmaAttributeId.GeneralMiningHoldCapacity];

    private static void AssertSystem(IReadOnlyDictionary<long, List<object?>> systems, long installed, long expiry) =>
        Assert.Equal(new object?[] { installed, expiry }, Assert.Single(systems, system => system.Key == MiningBargeOperations).Value);

    private static void AssertSystemNotice(PilotNotification[] notices, bool added, long? installed = null, long? expiry = null)
    {
        var notice = Assert.Single(notices.OfType<ExpertSystemsUpdated>());
        var args = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(notice.Arguments)));
        Assert.Equal(3, args.Length);
        Assert.Equal(added, args[1]);
        Assert.Equal((long)MiningBargeOperations, args[2]);
        var systems = ReadSystems(args[0]);
        if (installed is long start && expiry is long end)
        {
            AssertSystem(systems, start, end);
        }
        else
        {
            Assert.Empty(systems);
        }
    }

    private static void AssertSkill(object? row, int type, int? level, long? points, double rank, int? virtualLevel)
    {
        var entry = Assert.IsType<BlueConstructor>(row);
        Assert.Equal("characterskills.common.character_skill_entry.CharacterSkillEntry", entry.Name);
        Assert.Equal(new object?[] { type, level, points, rank, virtualLevel }, entry.Arguments);
    }

    private static IReadOnlyDictionary<long, List<object?>> ReadSystems(object? wire) =>
        wire is Dictionary<string, object?> { Count: 0 }
            ? new Dictionary<long, List<object?>>()
            : Assert.IsType<Dictionary<long, object?>>(wire).ToDictionary(row => row.Key, row => Assert.IsType<List<object?>>(row.Value));

    private sealed class Client : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotCallRouter _router;
        private readonly ExpertSystemHandler _systems;
        private readonly PilotCallContext _context;

        public Client(IServiceProvider services, long account, bool select = true)
        {
            _scope = services.CreateScope();
            _router = _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            _systems = _scope.ServiceProvider.GetRequiredService<ExpertSystemHandler>();
            var factory = _scope.ServiceProvider.GetRequiredService<FlightSessionFactory>();
            var pilot = services.GetRequiredService<SqliteWorld>().FindPilot(account)!;
            _context = new(account, pilot.Account, [pilot], factory.Create, account, SignedSettings.Empty);
            if (select)
            {
                Select();
            }
        }

        public DockedPilot Pilot => _context.Pilot;

        public void Select()
        {
            _context.Select(Assert.Single(_context.Roster));
            _systems.Enter(_context);
        }

        public PilotCallResult Consume(long id) => Call(ExpertService, "ConsumeExpertSystem", [id]);

        public PilotCallResult Remove(int type) => Call(ExpertService, "RemoveMyExpertSystem", [(long)type]);

        public PilotCallResult Board(long ship) => Call("ship", "Board", [ship, Pilot.ShipId]);

        public PilotCallResult Fit(long item) =>
            Call(Bind("invbroker", "GetInventoryFromId", [Pilot.ShipId]), "Add", [item, (long)Pilot.StationId], new() { ["flag"] = 0L });

        public void Advance() => _systems.Advance(_context);

        public IReadOnlyDictionary<long, List<object?>> Systems() =>
            ReadSystems(BlueMarshal.Decode(BlueMarshal.Encode(Reply(Call(ExpertService, "GetMyExpertSystems", [])))));

        public Dictionary<long, object?> Skills() =>
            Assert.IsType<Dictionary<long, object?>>(
                WireAssertions.Materialize(Reply(Call(Bind("skillMgr2", "GetMySkillHandler", []), "GetSkills", [])))
            );

        public PilotNotification[] Notices()
        {
            var notices = _context.NotificationsBeforeFlight.Concat(_context.Notifications).ToArray();
            _context.NotificationsBeforeFlight.Clear();
            _context.Notifications.Clear();
            return notices;
        }

        private string Bind(string service, string method, object?[] args)
        {
            var reference = Assert.IsType<BlueObjectReference>(Reply(Call(service, method, args)).ToBlue());
            return Assert.IsType<string>(_context.ResolveReference(reference.Id));
        }

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _router.Call(_context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        private static BlueModel Reply(PilotCallResult call) => Assert.IsType<PilotCallResult.Success>(call).Reply;

        public void Dispose()
        {
            _context.Dispose();
            _scope.Dispose();
        }
    }
}
