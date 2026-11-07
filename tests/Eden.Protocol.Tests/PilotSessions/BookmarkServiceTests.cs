using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class BookmarkServiceTests
{
    [Fact]
    public void NativePersonalFolderLocationAndCopyCallsPreserveEveryReplyShapeAndSavedField()
    {
        using Fixture fixture = new();
        Assert.IsType<PilotCallResult.UnsupportedCall>(fixture.Call("GetMyActiveBookmarks", []));
        fixture.Select();
        Assert.All(
            BlueRead.Tuple(Reply(fixture.Call("GetMyActiveBookmarks", [])), 3),
            value => Assert.Empty(Assert.IsType<List<object?>>(value))
        );
        long folder = fixture.Folder("星間旅行");
        long destination = fixture.Folder("Destinations");
        var subfolder = Fields(Reply(fixture.Call("CreateSubfolder", [folder, "Trade"])));
        long subfolderId = Assert.IsType<long>(subfolder["subfolderID"]);
        Assert.Equal(folder, subfolder["folderID"]);
        Assert.Equal(1L, Fields(Reply(fixture.Call("GetFolderInfo", [folder])))["accessLevel"]);
        Assert.Equal(1L, Reply(fixture.Call("UpdateFolder", [folder, "Locations", "Notes", null, null, null, null])));
        Assert.Equal(true, Reply(fixture.Call("UpdateSubfolder", [folder, subfolderId, "Mining"])));
        var created = BlueRead.Tuple(
            Reply(
                fixture.Call(
                    "BookmarkStaticLocation",
                    [60003760L, folder, new BlueUnicode("Home 🚀"), "Note", 1L],
                    keywords: new() { ["subfolderID"] = subfolderId }
                )
            ),
            8
        );
        long id = Assert.IsType<long>(created[0]);
        Assert.Equal(60003760L, created[1]);
        Assert.Equal(fixture.Context.Pilot.StationTypeId, Convert.ToInt32(created[2]));
        Assert.Equal((long)fixture.Context.Pilot.SystemId, created[6]);
        Assert.Equal(fixture.Clock.Now + TimeSpan.FromHours(4).Ticks, created[7]);
        var initial = BlueRead.Tuple(Reply(fixture.Call("GetMyActiveBookmarks", [])), 3);
        Assert.Equal(2, Assert.IsType<List<object?>>(initial[0]).Count);
        var bookmark = Fields(Assert.Single(Assert.IsType<List<object?>>(initial[1])));
        Assert.Equal("Home 🚀", bookmark["memo"]);
        Assert.Equal(subfolderId, bookmark["subfolderID"]);
        Assert.Equal(90000001L, bookmark["creatorID"]);
        Assert.Equal(fixture.Clock.Now, bookmark["created"]);
        Assert.Equal("Note", BlueMarshal.Text(bookmark["note"]));
        Assert.Null(bookmark["flag"]);
        var copy = BlueRead.Tuple(Reply(fixture.Call("CopyBookmarksToFolderAndSubfolder", [folder, destination, null, Set(id)])), 2);
        var copies = Assert.IsType<Dictionary<long, object?>>(copy[0]);
        long copyId = Assert.Single(copies).Key;
        Assert.NotEqual(id, copyId);
        Assert.Equal(destination, Fields(copies[copyId])["folderID"]);
        Assert.Null(copy[1]);
        Assert.Null(Reply(fixture.Call("UpdateBookmark", [copyId, destination, "Unchanged expiry", "", null, destination, 0L])));
        Assert.Equal(created[7], fixture.Store.Find(90000001, copyId).ExpiresAt);
        var move = BlueRead.Tuple(
            Reply(fixture.Call("MoveBookmarksToFolderAndSubfolder", [destination, folder, subfolderId, Set(copyId)])),
            2
        );
        Assert.Equal(copyId, Fields(Assert.Single(Assert.IsType<List<object?>>(move[0])))["bookmarkID"]);
        Assert.Null(Reply(fixture.Call("UpdateBookmark", [copyId, folder, "Renamed", "Updated", null, destination, true])));
        Assert.Null(fixture.Store.Find(90000001, copyId).ExpiresAt);
        Assert.Equal("Renamed", fixture.Store.Find(90000001, copyId).Text.Name);
        var inactive = BlueRead.Tuple(Reply(fixture.Call("UpdateKnownFolderState", [folder, false])), 3);
        Assert.False(Assert.IsType<bool>(Fields(inactive[0])["isActive"]));
        Assert.Empty(Assert.IsType<List<object?>>(inactive[1]));
        Reply(fixture.Call("RemoveFromKnownFolders", [folder]));
        Assert.Single(Assert.IsType<List<object?>>(BlueRead.Tuple(Reply(fixture.Call("GetMyActiveBookmarks", [])), 3)[0]));
        var restored = BlueRead.Tuple(Reply(fixture.Call("AddToKnownFolders", [folder, true])), 3);
        Assert.Equal(id, Fields(Assert.Single(Assert.IsType<List<object?>>(restored[1])))["bookmarkID"]);
        Assert.Equal(new List<object?> { id }, Assert.IsType<List<object?>>(Reply(fixture.Call("DeleteSubfolder", [folder, subfolderId]))));
        Assert.Equal(
            new List<object?> { copyId },
            Assert.IsType<List<object?>>(Reply(fixture.Call("DeleteBookmarks", [destination, Set(copyId)])))
        );
        Assert.Null(Reply(fixture.Call("DeleteFolder", [destination])));
        Assert.Empty(fixture.Store.Read(90000001).Bookmarks);
        Assert.Empty(fixture.Context.Notifications);
    }

    [Fact]
    public void NativeInFlightBookmarkUsesAuthoritativePositionAndRejectsForgedCoordinatesAndOtherPilotsFolders()
    {
        using Fixture fixture = new();
        fixture.Select(true);
        long folder = fixture.Folder("Safe spots");
        var other = fixture.Store.AddFolder(90000002, "Wing", "");
        var saved = BlueRead.Tuple(
            Reply(fixture.Call("BookmarkLocation", [fixture.Context.Pilot.ShipId, folder, "Safe spot", "", 0L], "beyonce")),
            8
        );
        Assert.Null(saved[1]);
        Assert.Equal(5L, saved[2]);
        Assert.Equal(fixture.Context.Pilot.Position!.X, saved[3]);
        Assert.Equal(fixture.Context.Pilot.Position.Y, saved[4]);
        Assert.Equal(fixture.Context.Pilot.Position.Z, saved[5]);
        foreach (
            (string method, object?[] args) in new (string, object?[])[]
            {
                ("AddFolder", [false, "Shared", "", null, null, null, null]),
                ("AddFolder", [true, "Shared", "", 123L, null, null, null]),
                ("UpdateFolder", [other.Id, "Stolen", "", null, null, null, null]),
                ("BookmarkStaticLocation", [60003760L, other.Id, "Stolen", "", 0L]),
                ("BookmarkStaticLocation", [long.MaxValue, folder, "Invalid", "", 0L]),
                ("BookmarkStaticLocation", [60003760L, folder, "Invalid", "", 4L]),
                ("BookmarkStaticLocation", [60003760L, folder, "", "", 0L]),
                ("BookmarkStaticLocation", [60003760L, folder, "Long note", new string('n', 3901), 0L]),
                ("DeleteBookmarks", [folder, Set((long)saved[0]!, 999L)]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(fixture.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("BookmarkLocation", [fixture.Context.Pilot.ShipId, folder, "Forged", "", 0L], "beyonce", new() { ["x"] = 123d })
        );
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("BookmarkLocation", [999999999L, folder, "Unknown", "", 0L], "beyonce"));
        Assert.Single(fixture.Store.Read(90000001).Bookmarks);
        Assert.Empty(fixture.Store.Read(90000002).Bookmarks);
        Assert.Equal("Wing", fixture.Store.Folder(90000002, other.Id).Name);
        Assert.Empty(fixture.Context.Notifications);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, 0L)]
    [InlineData(true, 0L)]
    public void BookmarkWarpUsesExistingNavigationWithStableRetriesAndSavedArrival(bool staticLocation, object? fleet)
    {
        using Fixture fixture = new();
        fixture.Select(true);
        long folder = fixture.Folder("Travel");
        var target = staticLocation
            ? fixture.Store.CreateStatic(90000001, 60003760, folder, null, new("Station", ""), BookmarkExpiry.Never)
            : fixture.Store.Create(
                90000001,
                new(null, 5, fixture.Context.Pilot.SystemId, fixture.Context.Pilot.Position! + new SpacePosition(1_000_000_000, 0, 0)),
                folder,
                null,
                new("Point", ""),
                BookmarkExpiry.Never
            );
        var foreignFolder = fixture.Store.AddFolder(90000002, "Wing", "");
        var foreign = fixture.Store.CreateStatic(90000002, 60003760, foreignFolder.Id, null, new("Private", ""), BookmarkExpiry.Never);
        Dictionary<string, object?> keywords = new() { ["minRange"] = fleet is null ? (object)10_000L : 10_000d };
        if (fleet is not null)
        {
            keywords["fleet"] = fleet;
        }
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuff", ["bookmark", foreign.Id], "beyonce", keywords));
        Assert.False(fixture.Context.Flight.Moving);
        Assert.Null(Reply(Warp()));
        fixture.Context.Flight.Updates.Clear();
        Assert.Null(Reply(Warp()));
        Assert.Empty(fixture.Context.Flight.Updates);
        bool enteredWarp = false;
        for (int tick = 0; tick < 240; tick++)
        {
            fixture.Clock.Seconds++;
            fixture.Context.Flight.Checkpoint();
            enteredWarp |= fixture.Context.Flight.InWarp;
            if (!fixture.Context.Flight.Moving)
            {
                break;
            }
        }
        Assert.True(enteredWarp);
        Assert.False(fixture.Context.Flight.Moving);
        var arrival = fixture.Context.Pilot.Position!;
        var anchor = staticLocation
            ? CelestialWarpPoint.Resolve(fixture.Db.Objects.Single(body => body.Id == 60003760), Fixture.Start)
            : target.Target.Position;
        Assert.InRange(arrival.DistanceTo(anchor), 10_000, 12_500);
        Assert.Equal(arrival, fixture.Db.Store.FindPilot(1000001)!.Position);
        Assert.Equal(target, fixture.Store.Find(90000001, target.Id));

        PilotCallResult Warp() => fixture.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce", keywords);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(1L)]
    [InlineData(null)]
    [InlineData("False")]
    [InlineData(0d)]
    [InlineData(-1L)]
    public void FleetAndMalformedBookmarkWarpOptionsLeaveFlightAndBookmarksUnchanged(object? fleet)
    {
        using Fixture fixture = new();
        fixture.Select(true);
        long folder = fixture.Folder("Travel");
        var target = fixture.Store.CreateStatic(90000001, 60003760, folder, null, new("Station", ""), BookmarkExpiry.Never);
        var result = fixture.Call(
            "CmdWarpToStuff",
            ["bookmark", target.Id],
            "beyonce",
            new() { ["minRange"] = 10_000d, ["fleet"] = fleet }
        );
        var failure = Assert.IsType<PilotCallResult.Failure>(result);
        Assert.Equal(fleet is true or 1L ? "Fleet bookmark warp is not supported." : "Invalid bookmark warp fleet flag.", failure.Message);
        Assert.Equal(Fixture.Start, fixture.Context.Pilot.Position);
        Assert.Equal(Fixture.Start, fixture.Db.Store.FindPilot(1000001)!.Position);
        Assert.False(fixture.Context.Flight.Moving);
        Assert.Empty(fixture.Context.Flight.Updates);
        Assert.Empty(fixture.Context.Notifications);
        Assert.Equal(target, fixture.Store.Find(90000001, target.Id));
    }

    [Fact]
    public void InactiveExpiredRemoteAndMalformedWarpRequestsLeaveFlightUnchanged()
    {
        using Fixture fixture = new();
        fixture.Select(true);
        long folder = fixture.Folder("Travel");
        var target = fixture.Store.Create(
            90000001,
            new(null, 5, fixture.Context.Pilot.SystemId, Fixture.Start + new SpacePosition(1_000_000_000, 0, 0)),
            folder,
            null,
            new("Point", ""),
            BookmarkExpiry.FourHours
        );
        fixture.Store.SetFolderState(90000001, folder, false);
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce"));
        fixture.Store.SetFolderState(90000001, folder, true);
        Assert.IsType<PilotCallResult.Failure>(
            fixture.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce", new() { ["fleet"] = false, ["x"] = 1d })
        );
        var remote = fixture.Store.Create(
            90000001,
            target.Target with
            {
                LocationId = 30000144,
            },
            folder,
            null,
            new("Remote", ""),
            BookmarkExpiry.Never
        );
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuff", ["bookmark", remote.Id], "beyonce"));
        foreach (double range in new[] { -1d, double.NaN, double.PositiveInfinity, 100001d })
        {
            Assert.IsType<PilotCallResult.UnsupportedCall>(
                fixture.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce", new() { ["minRange"] = range })
            );
        }
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(4));
        Assert.IsType<PilotCallResult.Failure>(fixture.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce"));
        Assert.Equal(Fixture.Start, fixture.Context.Pilot.Position);
        Assert.False(fixture.Context.Flight.Moving);
        Assert.Empty(fixture.Context.Notifications);
    }

    private static BlueConstructor Set(params long[] ids) => new("__builtin__.set", [ids.Cast<object?>().ToList()]);

    private static Dictionary<string, object?> Fields(object? value) => BlueRead.Fields(Assert.IsType<BlueInstance>(value).State);

    private static object? Reply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));

    private sealed class Fixture : IDisposable
    {
        public static SpacePosition Start { get; } = new(10_000_000_000, 5_000_000_000, 7_000_000_000);
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        private readonly IServiceScope _scope;
        public PilotCallContext Context { get; }
        public IPilotBookmarks Store => Db.Services.GetRequiredService<IPilotBookmarks>();

        public Fixture()
        {
            Db = new(clock: Clock);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select(bool space = false)
        {
            if (space)
            {
                Db.Store.SetPosition(1000000000001, Start);
            }
            Context.Select(Db.Store.FindPilot(1000001)!);
            if (space)
            {
                Context.Flight.Snapshot();
                Context.Flight.Updates.Clear();
            }
        }

        public long Folder(string name) =>
            Assert.IsType<long>(Fields(Reply(Call("AddFolder", [true, name, "", null, null, null, null])))["folderID"]);

        public PilotCallResult Call(
            string method,
            object?[] args,
            string service = "accessGroupBookmarkMgr",
            Dictionary<string, object?>? keywords = null
        ) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(
                    Context,
                    service,
                    method,
                    BlueMarshal.Decode(BlueMarshal.Encode(args)),
                    _ => { },
                    keywords is null ? null : BlueRead.Fields(BlueMarshal.Decode(BlueMarshal.Encode(keywords)))
                );

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
