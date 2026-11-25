using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Flight;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class BookmarkServiceTests
{
    [Fact]
    public void NativeApproachBookmarkHomesToTheSavedPointAndCanBeStopped()
    {
        using Fixture fixture = new(liveWorld: true);
        fixture.Select(true);
        long folder = fixture.Folder("Travel");
        var point = Fixture.Start + new SpacePosition(1_500, 0, 0);
        var saved = fixture.Store.Create(
            90000001,
            new(null, BookmarkRules.SolarSystemTypeId, fixture.Context.Pilot.SystemId, point),
            folder,
            null,
            new("Point", ""),
            BookmarkExpiry.Never
        );
        Assert.Null(Reply(fixture.Call("CmdGotoBookmark", [saved.Id], "beyonce")));
        var flight = fixture.Context.Flight;
        var order = Assert.Single(flight.Updates.Select(update => update.Command).OfType<DestinyCommand.GotoPoint>());
        Assert.Equal(new DestinyCommand.GotoPoint(fixture.Context.Pilot.ShipId, point.X, point.Y, point.Z), order);

        double peakSpeed = 0;
        for (int tick = 0; tick < 90; tick++)
        {
            fixture.Clock.Seconds++;
            flight.Checkpoint();
            peakSpeed = Math.Max(peakSpeed, flight.Velocity.Length);
            Assert.False(flight.InWarp);
        }
        Assert.True(peakSpeed > 30);
        Assert.InRange(flight.Velocity.Length, 0, 1);
        Assert.InRange(fixture.Context.Pilot.Position!.DistanceTo(point), 0, 70);
        Assert.Null(Reply(fixture.Call("CmdStop", [], "beyonce")));
        fixture.Clock.Seconds += 25;
        flight.Checkpoint();
        Assert.False(flight.Moving);
    }

    [Theory]
    [InlineData(20_000, 2_000)]
    [InlineData(9_000_000, 2_000)]
    [InlineData(12_000_000, 2_001_000)]
    public void NativeApproachBookmarkFollowsVisibleItemsAndRejectsUnavailableDestinations(int centerDistance, int radius)
    {
        const long stationId = 60003760;
        StaticSpaceObject station = new(stationId, 1531, 15, 3, 1000035, Fixture.Start + new SpacePosition(centerDistance, 0, 0), radius);
        using Fixture fixture = new([station], liveWorld: true);
        Assert.Same(PilotCallResult.Unsupported, Approach([1L]));
        fixture.Select(true);
        long folder = fixture.Folder("Travel");
        var home = fixture.Store.CreateStatic(90000001, stationId, folder, null, new("Station", ""), BookmarkExpiry.Never);
        var flight = fixture.Context.Flight;
        Assert.Null(Reply(Approach([home.Id])));
        var order = Assert.Single(flight.Updates.Select(update => update.Command).OfType<DestinyCommand.Follow>());
        Assert.Equal(new DestinyCommand.Follow(NavigationMode.Follow, fixture.Context.Pilot.ShipId, stationId, 0), order);
        fixture.Clock.Seconds += 3;
        flight.Checkpoint();
        Assert.True(fixture.Context.Pilot.Position!.X > Fixture.Start.X);
        Assert.Null(Reply(fixture.Call("CmdStop", [], "beyonce")));
        var stoppedAt = fixture.Context.Pilot.Position!;
        flight.Updates.Clear();

        var remote = Create(90_000_001, folder, 30_000_144, Fixture.Start, BookmarkExpiry.Never);
        var far = Create(
            90_000_001,
            folder,
            fixture.Context.Pilot.SystemId,
            stoppedAt + new SpacePosition(BookmarkRules.MaximumApproachDistance, 0, 0),
            BookmarkExpiry.Never
        );
        var expired = Create(90_000_001, folder, fixture.Context.Pilot.SystemId, Fixture.Start, BookmarkExpiry.FourHours);
        var map = fixture.Store.CreateStatic(
            90000001,
            fixture.Context.Pilot.SystemId,
            folder,
            null,
            new("System", ""),
            BookmarkExpiry.Never
        );
        var privateFolder = fixture.Store.AddFolder(90_000_002, "Private", "");
        var foreign = Create(90_000_002, privateFolder.Id, fixture.Context.Pilot.SystemId, Fixture.Start, BookmarkExpiry.Never);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromHours(4));
        foreach (long id in new[] { remote.Id, far.Id, expired.Id, map.Id, foreign.Id })
        {
            Assert.IsType<PilotCallResult.Failure>(Approach([id]));
        }
        Assert.Same(PilotCallResult.Unsupported, Approach([home.Id, 0L]));
        fixture.Store.SetFolderState(90000001, folder, false);
        Assert.IsType<PilotCallResult.Failure>(Approach([home.Id]));
        Assert.Empty(flight.Updates);
        Assert.Equal(stoppedAt, fixture.Context.Pilot.Position);

        PilotCallResult Approach(object?[] args) => fixture.Call("CmdGotoBookmark", args, "beyonce");

        PersonalBookmark Create(long pilot, long ownerFolder, int system, SpacePosition point, BookmarkExpiry expiry) =>
            fixture.Store.Create(
                pilot,
                new(null, BookmarkRules.SolarSystemTypeId, system, point),
                ownerFolder,
                null,
                new("Point", ""),
                expiry
            );
    }
}
