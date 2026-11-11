using Eden.Gameplay.AccessGroups;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class BookmarkServiceTests
{
    [Fact]
    public void SharedBookmarkNavigationEnforcesTheExactDelayAndCurrentAccessAfterGrantRevocation()
    {
        using Fixture f = new();
        f.Select(true);
        var groups = f.Db.Services.GetRequiredService<IPilotAccessGroups>();
        var admin = groups.Create(90000002, "Admins", "");
        var view = groups.Create(90000002, "Viewers", "");
        groups.ChangeMembers(90000002, view.Id, [new(90000001, AccessGroupRole.Member)]);
        var folder = f.Store.AddSharedFolder(90000002, "Shared", "", new(admin.Id, null, null, view.Id));
        f.Store.SetFolderState(90000001, folder.Id, true);
        var target = f.Store.Create(
            90000002,
            new(null, BookmarkRules.SolarSystemTypeId, f.Context.Pilot.SystemId, Fixture.Start + new SpacePosition(1_000_000_000, 0, 0)),
            folder.Id,
            null,
            new("Shared point", ""),
            BookmarkExpiry.Never
        );
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
        Assert.IsType<PilotCallResult.Failure>(Warp());
        Assert.False(f.Context.Flight.Moving);
        f.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Null(Reply(Warp()));
        Assert.True(f.Context.Flight.PreparingWarp);
        Assert.Null(Reply(f.Call("CmdStop", [], "beyonce")));
        groups.ChangeMembers(90000002, view.Id, [new(90000001, AccessGroupRole.Excluded)]);
        Assert.IsType<PilotCallResult.Failure>(Warp());
        Assert.IsType<PilotCallResult.Failure>(f.Call("CmdAlignTo", [], "beyonce", new() { ["dstID"] = null, ["bookmarkID"] = target.Id }));
        Assert.False(f.Context.Flight.Moving);

        PilotCallResult Warp() => f.Call("CmdWarpToStuff", ["bookmark", target.Id], "beyonce");
    }
}
