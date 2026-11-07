using System.Xml.Linq;
using Eden.Data.Characters;
using Eden.Data.Combat;
using Eden.Gameplay.Characters;
using Eden.Protocol.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Combat;
using Eden.Protocol.Tests.Persistence;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class CharacterInteractionHistoryTests
{
    [Fact]
    public void ActualKillAndStandingRowsRoundTripWithNativeFieldsAndRefreshNotifications()
    {
        using PilotCombatTests.Fixture combat = new();
        InteractionHistoryPersistenceTests.PrepareFaction(combat);
        combat.AlmostDefeated();
        var shot = combat.Fire();
        using Fixture fixture = new(combat.Db);
        fixture.Context.Select(fixture.Db.Store.FindPilot(1000001)!);
        var rows = Assert.IsType<List<object?>>(Reply(fixture.Call("charMgr", "GetRecentShipKillsAndLosses", [25L, null])));
        var row = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(rows)).State);
        Assert.Equal(combat.Npc.SystemId, Convert.ToInt32(row["solarSystemID"]));
        Assert.Equal(90000001L, row["finalCharacterID"]);
        Assert.Null(row["victimCharacterID"]);
        Assert.Equal(30380L, row["victimShipTypeID"]);
        Assert.Equal((long)shot.Weapon.TypeId, row["finalWeaponTypeID"]);
        Assert.Equal(combat.Clock.Now, row["killTime"]);
        Assert.Equal(shot.Applied, row["victimDamageTaken"]);
        foreach (
            string optional in new[] { "moonID", "warID", "iskLost", "iskDestroyed", "bountyClaimed", "loyaltyPoints", "killRightSupplied" }
        )
        {
            Assert.True(row.ContainsKey(optional));
        }
        var blob = XElement.Parse("<doc>" + BlueMarshal.Text(row["killBlob"]) + "</doc>");
        Assert.NotEmpty(blob.Element("items")!.Elements("i"));
        Assert.Empty(blob.Element("attackers")!.Elements("a"));
        var transactions = Assert.IsType<List<object?>>(
            Reply(fixture.Call("standingMgr", "GetStandingTransactions", [500001L, 90000001L]))
        );
        var transaction = BlueRead.Fields(Assert.IsType<BlueInstance>(Assert.Single(transactions)).State);
        Assert.Equal(combat.Clock.Now, transaction["eventDateTime"]);
        Assert.Equal((long)StandingEvent.CombatShipKill, transaction["eventTypeID"]);
        Assert.Equal(500001L, transaction["fromID"]);
        Assert.Equal(90000001L, transaction["toID"]);
        Assert.Equal(-0.001, Assert.IsType<double>(transaction["modification"]), 10);
        Assert.Equal((long)combat.Npc.CorporationId, transaction["int_1"]);
        Assert.Equal((long)combat.Npc.SystemId, transaction["int_2"]);
        Assert.Equal((long)combat.Npc.TypeId, transaction["int_3"]);
        var standings = Assert.IsType<Dictionary<long, object?>>(Reply(fixture.Call("standingMgr", "GetCharStandings", [])));
        var standing = BlueRead.Fields(Assert.IsType<BlueInstance>(standings[500001]).State);
        Assert.Equal(-0.01, Assert.IsType<double>(standing["standing"]), 10);
        Assert.Equal(90000001L, standing["toID"]);
        fixture.Scope.ServiceProvider.GetRequiredService<CharacterInteractionHistoryHandler>().NotifyDestruction(fixture.Context);
        Assert.Equal(new[] { "OnKillNotification", "OnStandingSet" }, fixture.Context.Notifications.Select(n => n.Name));
        Assert.Empty(fixture.Context.Notifications[0].Arguments);
        Assert.Equal(new object?[] { 500001L, 90000001L, -0.01 }, fixture.Context.Notifications[1].Arguments);
    }

    [Fact]
    public void ReportBlobPreservesAdditionalAttackersAndNestedDroppedAndDestroyedItems()
    {
        CombatParty player = new(1, 90000001, 1000044, null, 601, 3638, 0.1, 12.5);
        CombatParty npc = new(2, null, 1000127, 500010, 30380, 562, null, 20);
        CombatLostItem item = new(3638, 27, true, 0, 1, [new(212, 5, false, 3, 0, [])]);
        ShipCombatReport report = new(1, 90000001, 100, 30000144, player with { Damage = 32.5 }, npc, [player], [item]);
        var row = BlueRead.Fields(
            Assert.IsType<BlueInstance>(BlueMarshal.Decode(BlueMarshal.Encode(new ShipCombatReportRow(report)))).State
        );
        var blob = XElement.Parse("<doc>" + BlueMarshal.Text(row["killBlob"]) + "</doc>");
        var attacker = Assert.Single(blob.Element("attackers")!.Elements("a"));
        Assert.Equal("90000001", attacker.Attribute("c")!.Value);
        Assert.Equal("12.5", attacker.Attribute("d")!.Value);
        var lost = Assert.Single(blob.Element("items")!.Elements("i"));
        Assert.Equal("27", lost.Attribute("f")!.Value);
        Assert.Equal("1", lost.Attribute("x")!.Value);
        Assert.Equal("3", Assert.Single(lost.Elements("i")).Attribute("d")!.Value);
    }

    [Fact]
    public void NativeCombatAndStandingReadsRequireSelectionAndReturnEmptyListsWithoutWritingState()
    {
        using Fixture fixture = new();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("charMgr", "GetRecentShipKillsAndLosses", [25L, null]));
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("standingMgr", "GetStandingTransactions", [500001L, pilot.PilotId]));
        fixture.Context.Select(pilot);
        using WorldDbContext world = new(fixture.Db.Path);
        var before = TableCounts(world);
        for (int read = 0; read < 3; read++)
        {
            // Both native combat filters call this same endpoint. Later pages
            // use the previous kill ID as their cursor; zero is also accepted.
            foreach (long? cursor in new long?[] { null, 0, 123456, long.MaxValue })
            {
                AssertEmptyList(fixture.Call("charMgr", "GetRecentShipKillsAndLosses", [25L, cursor]));
            }
            foreach (long source in new long[] { 500001, 1000044, 3019499 })
            {
                AssertEmptyList(fixture.Call("standingMgr", "GetStandingTransactions", [source, pilot.PilotId]));
                AssertEmptyList(fixture.Call("standingMgr", "GetStandingTransactions", [source, (long)pilot.CorpId]));
            }
            AssertEmptyList(fixture.Call("charMgr", "GetRecentShipKillsAndLosses", [1L, null], new() { ["machoVersion"] = 1L }));
            AssertEmptyList(
                fixture.Call("standingMgr", "GetStandingTransactions", [500001L, pilot.PilotId], new() { ["machoVersion"] = 1L })
            );
        }
        Assert.Equal(before, TableCounts(world));
        Assert.Equal(pilot, fixture.Db.Store.FindPilot(1000001));
        Assert.Empty(fixture.Context.Notifications);
        Assert.Empty(fixture.Context.NotificationsBeforeFlight);
    }

    [Fact]
    public void InvalidHistoryArgumentsAndPrivateTargetsFailWithoutWritingState()
    {
        using Fixture fixture = new();
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Context.Select(pilot);
        using WorldDbContext world = new(fixture.Db.Path);
        var before = TableCounts(world);
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [25L],
                [25L, null, pilot.PilotId],
                [null, null],
                [true, null],
                [25d, null],
                [0L, null],
                [-1L, null],
                [long.MaxValue, null],
                [25L, -1L],
                [25L, "0"],
                [25L, true],
                [25L, 1.5],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("charMgr", "GetRecentShipKillsAndLosses", args));
        }
        foreach (
            object?[] args in new object?[][]
            {
                [],
                [500001L],
                [500001L, pilot.PilotId, null],
                [0L, pilot.PilotId],
                [-1L, pilot.PilotId],
                ["500001", pilot.PilotId],
                [500001L, null],
                [500001L, 0L],
                [500001L, -1L],
                [500001L, 90000002L],
                [500001L, 1000001L],
                [500001L, true],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call("standingMgr", "GetStandingTransactions", args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("charMgr", "GetRecentShipKillsAndLosses", [25L, null], new() { ["characterID"] = 90000002L })
        );
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("standingMgr", "GetStandingTransactions", [500001L, pilot.PilotId], new() { ["unexpected"] = true })
        );
        Assert.Same(PilotCallResult.Unsupported, fixture.Call("charMgr", "UnknownHistoryCall", []));
        Assert.Equal(before, TableCounts(world));
        Assert.Empty(fixture.Context.Notifications);
        Assert.Empty(fixture.Context.NotificationsBeforeFlight);
    }

    private static void AssertEmptyList(PilotCallResult result) =>
        Assert.Empty(
            Assert.IsType<List<object?>>(BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply)))
        );

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private static int[] TableCounts(WorldDbContext world) =>
        [world.Pilots.Count(), world.Ships.Count(), world.Set<ShipLoss>().Count(), world.Set<SavedCharacterSheet>().Count()];

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; }
        private readonly bool _ownsDatabase;
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }

        public Fixture(TestWorld? database = null)
        {
            _ownsDatabase = database is null;
            Db = database ?? new();
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                42,
                SignedSettings.Empty
            );
        }

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            if (_ownsDatabase)
            {
                Db.Dispose();
            }
        }
    }
}
