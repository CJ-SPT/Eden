using Eden.Data.World.Exploration;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Protocol.Bookmarks;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Bookmarks;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Fixture = Eden.Protocol.Tests.Persistence.PassiveFittingTests.Fixture;

namespace Eden.Protocol.Tests.Exploration;

public sealed partial class ProbeScanningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullyResolvedSignatureBookmarksUseNativeArgumentsAndPersistCoordinates(bool numericResult)
    {
        using var f = Prepare();
        long launcher = Load(f);
        var (scanning, sites) = Stores(f);
        var site = sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now).First();
        var probes = scanning.Launch(f.PilotId, launcher, 8, f.Clock.Now).Probes;
        var scan = scanning.Begin(f.PilotId, Formation(probes, site.Position), f.Clock.Now);
        f.Clock.Milliseconds += (scan.ReadyAt - f.Clock.Now) / TimeSpan.TicksPerMillisecond;
        Assert.Equal(1, Assert.Single(scanning.Complete(f.PilotId, scan, f.Clock.Now), result => result.Site.Id == site.Id).Certainty);
        using var scope = f.Db.Services.CreateScope();
        using var context = BookmarkContext(f, scope.ServiceProvider);
        var bookmarks = scope.ServiceProvider.GetRequiredService<IPilotBookmarks>();
        var folder = bookmarks.AddFolder(f.PilotId, "Signatures", "");
        var subfolder = bookmarks.AddSubfolder(f.PilotId, folder.Id, "Resolved");
        object resultId = numericResult ? site.Id : site.TargetId;
        var result = BookmarkCall(
            scope.ServiceProvider,
            context,
            [site.SystemId, "Resolved site", "Note", resultId, folder.Id, (long)BookmarkExpiry.FourHours],
            new() { ["subfolderID"] = subfolder.Id }
        );
        var reply = BlueRead.Tuple(BookmarkReply(result), 8);
        Assert.Null(reply[1]);
        Assert.Equal((long)BookmarkRules.SolarSystemTypeId, reply[2]);
        long id = Assert.IsType<long>(reply[0]);
        var saved = bookmarks.Find(f.PilotId, id);
        Assert.Equal(site.Position, saved.Target.Position);
        Assert.Equal(site.SystemId, saved.Target.LocationId);
        Assert.Equal(subfolder.Id, saved.SubfolderId);
        Assert.Equal(f.Clock.Now + TimeSpan.FromHours(4).Ticks, saved.ExpiresAt);
        Assert.Equal(saved, new PilotBookmarksStore(new WorldDatabase(f.Db.Path), f.Clock).Find(f.PilotId, id));
        var foreign = bookmarks.AddFolder(90000002, "Foreign", "");
        Assert.IsType<PilotCallResult.Failure>(
            BookmarkCall(scope.ServiceProvider, context, [site.SystemId, "Stolen", "", resultId, foreign.Id, 0L])
        );
        Assert.Single(bookmarks.Read(f.PilotId).Bookmarks);
        Assert.Empty(bookmarks.Read(90000002).Bookmarks);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<WorldCombatAnomaly>().Single(row => row.Id == site.Id).CompletedAt = f.Clock.Now;
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Failure>(
            BookmarkCall(scope.ServiceProvider, context, [site.SystemId, "Expired result", "", resultId, folder.Id, 0L])
        );
        Assert.Equal(saved, bookmarks.Find(f.PilotId, id));
    }

    [Fact]
    public void UnresolvedUnknownRemoteAndMalformedScanResultsCannotRevealCoordinates()
    {
        using var f = Prepare();
        _ = Load(f);
        var (_, sites) = Stores(f);
        var signature = sites.Signatures(CombatAnomalyRules.SystemId, f.Clock.Now).First();
        var anomaly = sites.List(CombatAnomalyRules.SystemId, f.Clock.Now).First();
        using var scope = f.Db.Services.CreateScope();
        using var context = BookmarkContext(f, scope.ServiceProvider);
        var bookmarks = scope.ServiceProvider.GetRequiredService<IPilotBookmarks>();
        var folder = bookmarks.AddFolder(f.PilotId, "Sites", "");
        foreach (
            object?[] arguments in new object?[][]
            {
                [signature.SystemId, "Unresolved", "", signature.TargetId, folder.Id, 0L],
                [signature.SystemId, "Unknown", "", "ZZZ-999", folder.Id, 0L],
                [signature.SystemId + 1, "Remote", "", anomaly.TargetId, folder.Id, 0L],
                [signature.SystemId, "Malformed", "", null, folder.Id, 0L],
                [signature.SystemId, "Malformed", "", true, folder.Id, 0L],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(BookmarkCall(scope.ServiceProvider, context, arguments));
        }
        Assert.Empty(bookmarks.Read(f.PilotId).Bookmarks);
        Assert.IsType<PilotCallResult.Failure>(
            BookmarkCall(
                scope.ServiceProvider,
                context,
                [anomaly.SystemId, "Forged", "", anomaly.TargetId, folder.Id, 0L],
                new() { ["x"] = 1d }
            )
        );
        var created = BlueRead.Tuple(
            BookmarkReply(
                BookmarkCall(scope.ServiceProvider, context, [anomaly.SystemId, "Public anomaly", "", anomaly.TargetId, folder.Id, 0L])
            ),
            8
        );
        Assert.Equal(anomaly.Position, bookmarks.Find(f.PilotId, (long)created[0]!).Target.Position);
    }

    private static PilotCallContext BookmarkContext(Fixture f, IServiceProvider services)
    {
        var pilot = f.Db.Store.FindPilot(1000001)!;
        PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new FlightSession(
                f.Db.Store,
                p,
                f.Clock,
                services.GetRequiredService<SharedSpace>(),
                services.GetRequiredService<ISystemObjects>().Load(p.SystemId)
            ),
            42,
            SignedSettings.Empty
        );
        context.Select(pilot);
        context.Flight.Snapshot();
        return context;
    }

    private static PilotCallResult BookmarkCall(
        IServiceProvider services,
        PilotCallContext context,
        object?[] arguments,
        Dictionary<string, object?>? keywords = null
    ) =>
        services
            .GetRequiredService<PilotCallRouter>()
            .Call(context, "beyonce", "BookmarkScanResult", BlueMarshal.Decode(BlueMarshal.Encode(arguments)), _ => { }, keywords);

    private static object? BookmarkReply(PilotCallResult result) =>
        BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(result).Reply));
}
