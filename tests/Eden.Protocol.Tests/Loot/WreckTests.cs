using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Loot;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Loot;

public sealed class WreckTests
{
    private static readonly DateTimeOffset s_start = DateTimeOffset.UtcNow;
    private static long Now => s_start.UtcDateTime.ToFileTimeUtc();

    private sealed class Clock : TimeProvider
    {
        public int Seconds { get; set; }
        public override long TimestampFrequency => 1;

        public override long GetTimestamp() => Seconds;

        public override DateTimeOffset GetUtcNow() => s_start.AddSeconds(Seconds);
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; } = new();
        public DockedPilot Pilot => Db.Store.FindPilot(1000001)!;
        public IWrecks Store => Db.Services.GetRequiredService<IWrecks>();
        public WreckContact Wreck { get; }
        public long Item => Store.Read(Pilot.PilotId, Wreck.Id, Now).Items.Single().Id;

        public Fixture()
        {
            NpcEncounterTests.Prepare(Db);
            var npc = Db.Services.GetRequiredService<Eden.Protocol.Combat.INpcEncounters>().List(30000144, Now)[0];
            Db.Store.SetPosition(Pilot.ShipId, npc.Position + new SpacePosition(1000, 0, 0), npc.SystemId);
            using (WorldDbContext world = new(Db.Path))
            using (var transaction = world.Database.BeginTransaction())
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath))
            {
                var source = world.Set<WorldNpc>().Find(npc.Id)!;
                source.DestroyedAt = Now;
                WreckStore.Create(world, catalog, source, world.Pilots.Single(p => p.Id == Pilot.PilotId), Now);
                world.SaveChanges();
                transaction.Commit();
            }
            Wreck = Assert.Single(Store.List(npc.SystemId, Now));
        }

        public IReadOnlyList<InventoryChange> Take(int? quantity = null) =>
            Store.Take(Pilot.PilotId, Pilot.ShipId, Wreck.Id, [Item], quantity, Now);

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public void CatalogWreckAndItsSingleLootStackSurviveRestartAndKeepTheKillersRights()
    {
        using Fixture f = new();
        var inventory = f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now);
        Assert.Equal(26557, inventory.Container.TypeId);
        Assert.Equal(WreckDefinitions.Group, inventory.Container.GroupId);
        Assert.Equal(f.Pilot.PilotId, inventory.Container.OwnerId);
        Assert.Equal(WreckDefinitions.StarterLootType, Assert.Single(inventory.Items).TypeId);
        Assert.Equal(100, inventory.Items[0].Quantity);
        Assert.False(f.Wreck.Empty);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(inventory.Container, f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Container);
        Assert.Equal(inventory.Items, f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items);
        Assert.Equal(f.Wreck, Assert.Single(f.Store.List(f.Wreck.SystemId, Now)));
        Assert.Empty(f.Store.List(30000142, Now));
    }

    [Fact]
    public void FullPickupMovesTheExistingStackOnceAndLeavesAnEmptyWreck()
    {
        using Fixture f = new();
        long id = f.Item;
        var moved = Assert.Single(f.Take());
        Assert.Equal(id, moved.Item.Id);
        Assert.Equal(f.Wreck.Id, moved.Previous!.LocationId);
        Assert.Equal(f.Pilot.ShipId, moved.Item.LocationId);
        Assert.Equal((int)InventoryFlag.Cargo, moved.Item.FlagId);
        Assert.Empty(f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items);
        Assert.True(Assert.Single(f.Store.List(f.Wreck.SystemId, Now)).Empty);
        Assert.Throws<InvalidDataException>(() => f.Store.Take(f.Pilot.PilotId, f.Pilot.ShipId, f.Wreck.Id, [id], null, Now));
        _ = new SqliteWorld(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100, saved.Set<OwnedItem>().Single(i => i.Id == id).Quantity);
        Assert.Equal(f.Pilot.ShipId, saved.Set<OwnedItem>().Find(id)!.LocationId);
    }

    [Fact]
    public void PartialPickupSplitsWithoutDuplicationAndTheRemainderCanBeTaken()
    {
        using Fixture f = new();
        long original = f.Item;
        var split = f.Take(25);
        Assert.Equal(2, split.Count);
        Assert.Equal(75, f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items.Single().Quantity);
        Assert.Equal(original, split[0].Item.Id);
        Assert.NotEqual(original, split[1].Item.Id);
        Assert.Equal(25, split[1].Item.Quantity);
        Assert.Equal(original, Assert.Single(f.Take()).Item.Id);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100, saved.Set<OwnedItem>().Where(i => i.TypeId == WreckDefinitions.StarterLootType).Sum(i => i.Quantity));
        Assert.All(
            saved.Set<OwnedItem>().Where(i => i.TypeId == WreckDefinitions.StarterLootType),
            i => Assert.Equal(f.Pilot.ShipId, i.LocationId)
        );
    }

    [Fact]
    public void CapacityFailurePreservesContentsAndAnExactFittingPartialQuantitySucceeds()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new OwnedItem
                {
                    Id = PilotInventoryStore.NextId(world),
                    OwnerId = f.Pilot.PilotId,
                    TypeId = WreckDefinitions.StarterLootType,
                    LocationId = f.Pilot.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    Quantity = 12475,
                }
            );
            world.SaveChanges();
        }
        var failure = Assert.Throws<CargoCapacityException>(() => f.Take());
        Assert.Equal(0.25, failure.Available, 8);
        Assert.Equal(1, failure.Required);
        Assert.Equal(100, f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items.Single().Quantity);
        Assert.Equal(25, f.Take(25).Single(c => c.Previous is null).Item.Quantity);
        Assert.Throws<CargoCapacityException>(() => f.Take(1));
    }

    [Fact]
    public async Task CompetingPickupsCannotDuplicateTheWreckContents()
    {
        using Fixture f = new();
        long item = f.Item;
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return f.Store.Take(f.Pilot.PilotId, f.Pilot.ShipId, f.Wreck.Id, [item], null, Now);
                        }
                        catch (InvalidDataException)
                        {
                            return null;
                        }
                    })
                )
        );
        Assert.Single(results, r => r is not null);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(100, saved.Set<OwnedItem>().Single(i => i.TypeId == WreckDefinitions.StarterLootType).Quantity);
    }

    [Fact]
    public void TransfersRequireOwnershipActiveShipSameSystemRangeAndValidQuantities()
    {
        using Fixture f = new();
        long id = f.Item;
        var wing = f.Db.Store.FindPilot(1000002)!;
        f.Db.Store.SetPosition(wing.ShipId, f.Wreck.Position, f.Wreck.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Store.Read(wing.PilotId, f.Wreck.Id, Now));
        Assert.Throws<InvalidDataException>(() => f.Store.Take(wing.PilotId, wing.ShipId, f.Wreck.Id, [id], null, Now));
        Assert.Throws<InvalidDataException>(() => f.Store.Take(f.Pilot.PilotId, wing.ShipId, f.Wreck.Id, [id], null, Now));
        Assert.Throws<InvalidDataException>(() => f.Store.Take(f.Pilot.PilotId, f.Pilot.ShipId, f.Wreck.Id, [id, id], null, Now));
        Assert.Throws<InvalidDataException>(() => f.Take(101));
        Assert.Throws<InvalidDataException>(() => f.Take(0));
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Wreck.Position + new SpacePosition(2562, 0, 0), f.Wreck.SystemId);
        Assert.Throws<InvalidDataException>(() => f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now));
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Wreck.Position + new SpacePosition(2561, 0, 0), f.Wreck.SystemId);
        Assert.NotEmpty(f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items);
        f.Db.Store.SetPosition(f.Pilot.ShipId, f.Wreck.Position, 30000142);
        Assert.Throws<InvalidDataException>(() => f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now));
        f.Db.Store.SetPosition(f.Pilot.ShipId, null);
        Assert.Throws<InvalidDataException>(() => f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now));
    }

    [Fact]
    public void ExpiryDisablesAccessAndConsumesOnlyContentsStillInsideTheWreck()
    {
        using Fixture f = new();
        f.Take(25);
        long expiry = Now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond;
        Assert.Single(f.Store.List(f.Wreck.SystemId, expiry - 1));
        Assert.Throws<InvalidDataException>(() => f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, expiry));
        Assert.Empty(f.Store.List(f.Wreck.SystemId, expiry));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal(25, saved.Set<OwnedItem>().Where(i => i.TypeId == WreckDefinitions.StarterLootType).Sum(i => i.Quantity));
        Assert.Single(saved.Set<WorldWreck>());
    }

    [Fact]
    public void NativeLootCallsMoveInventoryAndUpdateTheEmptyWreckSlimAndExpireTheBinding()
    {
        using Fixture f = new();
        Clock clock = new();
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        var pilot = f.Pilot;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, clock, new(), [], wrecks: f.Store),
            7,
            []
        );
        object? Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            router.CallWire(context, service, method, args, _ => { }, keywords);
        context.Select(pilot);
        context.Flight.Snapshot();
        var reference = Assert.IsType<BlueObjectReference>(Call("invbroker", "GetInventoryFromId", [f.Wreck.Id, false]));
        string service = context.ResolveReference(reference.Id)!;
        Assert.Single(Assert.IsType<List<object?>>(Call(service, "List", [], new() { ["flag"] = null })));
        Assert.IsType<BlueConstructor>(Call(service, "GetSelfInvItem", []));
        context.Flight.Updates.Clear();
        Assert.Null(Call("@ship", "MultiAdd", [new List<object?> { f.Item }, f.Wreck.Id], new() { ["flag"] = (long)InventoryFlag.Cargo }));
        var change = Assert.Single(context.Notifications, n => n.Name == "OnItemChange");
        Assert.Equal(f.Wreck.Id, Assert.IsType<Dictionary<long, object?>>(change.Arguments[1])[(long)InventoryColumn.Location]);
        Assert.Empty(Assert.IsType<List<object?>>(Call(service, "List", [])));
        var action = Assert.Single(
            context.Flight.Updates.Cast<object?[]>().Select(u => (object?[])u[1]!),
            a => Equals(a[0], "OnSlimItemChange")
        );
        var args = Assert.IsType<object?[]>(action[1]);
        Assert.True(Assert.IsType<bool>(Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(args[1]).State)["isEmpty"]));
        Assert.NotEmpty(BlueMarshal.Encode(context.Flight.Updates));
        context.ReleaseLocationReferences();
        Assert.Null(context.ResolveReference(reference.Id));
    }

    [Fact]
    public void WreckWireRecordIsACompleteLocalFreeBallWithSourceAndLootRights()
    {
        using Fixture f = new();
        var payload = Ballpark.AddWrecks([f.Wreck], 10);
        using BinaryReader reader = new(new MemoryStream(Assert.IsType<byte[]>(payload[0])));
        Assert.Equal(105, reader.BaseStream.Length);
        Assert.Equal(0, reader.ReadByte());
        Assert.Equal(10, reader.ReadInt32());
        Assert.Equal(f.Wreck.Id, reader.ReadInt64());
        Assert.Equal((byte)DestinyBallMode.Stop, reader.ReadByte());
        Assert.Equal(14, reader.ReadSingle());
        Assert.Equal(f.Wreck.Position.X, reader.ReadDouble());
        Assert.Equal(f.Wreck.Position.Y, reader.ReadDouble());
        Assert.Equal(f.Wreck.Position.Z, reader.ReadDouble());
        Assert.Equal((byte)(DestinyBallFlags.Free | DestinyBallFlags.Interactive), reader.ReadByte());
        var slim = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<BlueInstance>(Assert.Single(Assert.IsType<List<object?>>(payload[1]))).State
        );
        Assert.Equal(f.Wreck.SourceId, slim["launcherID"]);
        Assert.Equal(false, slim["isEmpty"]);
        Assert.Equal(new object?[] { f.Pilot.PilotId, f.Pilot.CorpId, null, false }, slim["lootRights"]);
    }

    [Fact]
    public void StreamingUnloadsExpiredWrecksWithoutReplacingTheBallpark()
    {
        using Fixture f = new();
        Clock clock = new();
        using FlightSession flight = new(f.Db.Store, f.Pilot, clock, new(), [], wrecks: f.Store);
        flight.Snapshot();
        Assert.Single(flight.VisibleWrecks);
        Assert.Null(flight.CallWire("beyonce", "CmdFollowBall", [f.Wreck.Id, 1000d]));
        flight.Updates.Clear();
        clock.Seconds = WreckDefinitions.LifetimeSeconds;
        flight.RefreshWrecks();
        var removed = Assert.Single(flight.Updates.Cast<object?[]>().Select(u => (object?[])u[1]!));
        Assert.Equal("RemoveBall", removed[0]);
        Assert.Equal(new object?[] { f.Wreck.Id, false }, removed[1]);
        Assert.Empty(flight.VisibleWrecks);
    }

    [Fact]
    public void VersionTwentyTwoUpgradeDoesNotInventLootForEarlierKills()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSql($"DELETE FROM WorldItem WHERE LocationId={f.Wreck.Id}");
            world.Database.ExecuteSqlRaw(
                TestWorld.DropNpcNavigationColumns
                    + "DROP TABLE WorldWreck; ALTER TABLE WorldShipLoss DROP COLUMN DroppedItemIdsJson; ALTER TABLE "
                    + "WorldWeaponCooldown DROP COLUMN Kind; PRAGMA user_version=22"
            );
        }
        _ = new SqliteWorld(f.Db.Path);
        Assert.Empty(f.Store.List(f.Wreck.SystemId, Now));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.NotNull(saved.Set<WorldNpc>().Find(f.Wreck.SourceId)!.DestroyedAt);
        Assert.DoesNotContain(saved.Set<OwnedItem>(), i => i.TypeId == WreckDefinitions.StarterLootType);
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void VersionTwentyThreeUpgradePreservesNpcWreckContentsAndDoesNotInventPlayerDrops()
    {
        using Fixture f = new();
        long itemId = f.Item;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropNpcNavigationColumns
                    + """
                    CREATE TABLE LegacyWreck(Id INTEGER PRIMARY KEY, NpcId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE RESTRICT,
                        OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, CorporationId INTEGER NOT NULL,
                        TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Name TEXT NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z
                            REAL NOT NULL,
                        CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                    INSERT INTO LegacyWreck SELECT Id,NpcId,OwnerId,CorporationId,TypeId,SystemId,Name,X,Y,Z,CreatedAt,ExpiresAt FROM
                        WorldWreck;
                    DROP TABLE WorldWreck;
                    ALTER TABLE LegacyWreck RENAME TO WorldWreck;
                    CREATE UNIQUE INDEX IX_WorldWreck_NpcId ON WorldWreck(NpcId);
                    CREATE INDEX IX_WorldWreck_SystemId_ExpiresAt ON WorldWreck(SystemId,ExpiresAt);
                    ALTER TABLE WorldShipLoss DROP COLUMN DroppedItemIdsJson;
                    ALTER TABLE WorldWeaponCooldown DROP COLUMN Kind;
                    PRAGMA user_version=23;
                    """
            );
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldShipLoss(ShipId,PilotId,NpcId,CapsuleId,TypeId,SystemId,Time,Name,ItemsJson,X,Y,Z)
                    VALUES (123,{f.Pilot.PilotId},{f.Wreck.SourceId},124,601,{f.Wreck.SystemId},{Now},'Earlier loss','[]',0,0,0)
                """
            );
        }
        _ = new SqliteWorld(f.Db.Path);
        _ = new SqliteWorld(f.Db.Path);
        Assert.Equal(f.Wreck, Assert.Single(f.Store.List(f.Wreck.SystemId, Now)));
        Assert.Equal(itemId, Assert.Single(f.Store.Read(f.Pilot.PilotId, f.Wreck.Id, Now).Items).Id);
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Equal("[]", saved.Set<ShipLoss>().Single().DroppedItemIdsJson);
        Assert.Empty(saved.Database.SqlQueryRaw<string>("PRAGMA foreign_key_check").AsEnumerable());
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }
}
