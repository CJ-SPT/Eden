using Eden.Data.World.Bookmarks;
using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Structures;
using Eden.Protocol.Bookmarks;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed partial class BookmarkServiceTests
{
    private sealed class BookmarkObjects(IReadOnlyList<StaticSpaceObject> objects) : ISystemObjects
    {
        public FlightSystem GetSystem(int systemId) => new(systemId, 20000020, 10000002);

        public IReadOnlyList<StaticSpaceObject> Load(int systemId) => objects;
    }

    [Theory]
    [InlineData(AsteroidDefinitions.Category, 462)]
    [InlineData(11, 226)]
    [InlineData(StructureRules.CategoryId, 1657)]
    public void VisibleObjectBookmarksSaveCoordinatesAndRemainWarpableAfterTheObjectDisappears(int category, int group)
    {
        long objectId = AsteroidDefinitions.FirstAsteroidId;
        StaticSpaceObject target = new(objectId, 1230, group, category, 1, Fixture.Start + new SpacePosition(10_000, 0, 0), 10);
        using Fixture f = new([target]);
        f.Select(true);
        long folder = f.Folder("Objects");
        var handler = Handler(f, [target]);
        var reply = BlueRead.Tuple(
            Reply(handler.Call(f.Context, "beyonce", "BookmarkLocation", [target.Id, folder, "Saved object", "", 0L])),
            8
        );
        long id = Assert.IsType<long>(reply[0]);
        Assert.Null(reply[1]);
        Assert.Equal((long)BookmarkRules.SolarSystemTypeId, reply[2]);
        Assert.Equal(target.Position, f.Store.Find(90000001, id).Target.Position);
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, Fixture.Start + new SpacePosition(1_000_000_000, 0, 0), f.Context.Pilot.SystemId);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        f.Context.Flight.Relocate(f.Context.Pilot, []);
        f.Context.Flight.Snapshot();
        Assert.IsType<PilotCallResult.Success>(f.Call("CmdWarpToStuff", ["bookmark", id], "beyonce"));
        var reopened = new Eden.World.Bookmarks.PilotBookmarksStore(new WorldDatabase(f.Db.Path), f.Clock);
        Assert.Equal(f.Store.Find(90000001, id), reopened.Find(90000001, id));
    }

    [Fact]
    public void NativeStructureStaticBookmarkSavesItsCurrentSystemLocation()
    {
        StaticSpaceObject structure = new(
            StructureRules.FirstStructureId,
            35832,
            1657,
            StructureRules.CategoryId,
            1,
            Fixture.Start + new SpacePosition(1_000_000, 0, 0),
            1000
        );
        using Fixture f = new([structure]);
        f.Select(true);
        long folder = f.Folder("Structures");
        var created = BlueRead.Tuple(
            Reply(
                Handler(f, [structure])
                    .Call(f.Context, "accessGroupBookmarkMgr", "BookmarkStaticLocation", [structure.Id, folder, "Structure", "", 0L])
            ),
            8
        );
        Assert.Null(created[1]);
        Assert.Equal(structure.Position, f.Store.Find(90000001, (long)created[0]!).Target.Position);
    }

    [Fact]
    public void UnknownAndOutOfGridTemporaryObjectsCannotCreateBookmarks()
    {
        StaticSpaceObject asteroid = new(
            AsteroidDefinitions.FirstAsteroidId,
            1230,
            462,
            AsteroidDefinitions.Category,
            1,
            Fixture.Start + new SpacePosition(AsteroidDefinitions.VisibilityRange + 1, 0, 0),
            1
        );
        using Fixture f = new([asteroid]);
        f.Select(true);
        long folder = f.Folder("Private");
        var handler = Handler(f, [asteroid]);
        foreach (long id in new[] { asteroid.Id, asteroid.Id + 1 })
        {
            Assert.IsType<PilotCallResult.Failure>(handler.Call(f.Context, "beyonce", "BookmarkLocation", [id, folder, "Hidden", "", 0L]));
        }
        Assert.Empty(f.Store.Read(90000001).Bookmarks);
    }

    [Fact]
    public void LegacyTemporaryObjectBookmarksReadCopyAndWarpAsCoordinatesAcrossReopen()
    {
        using Fixture f = new();
        f.Select(true);
        long folder = f.Folder("Old bookmarks");
        Assert.Throws<InvalidDataException>(() =>
            f.Store.Create(
                90000001,
                new(AsteroidDefinitions.FirstAsteroidId, 0, f.Context.Pilot.SystemId, Fixture.Start),
                folder,
                null,
                new("Invalid type", ""),
                BookmarkExpiry.Never
            )
        );
        var saved = f.Store.Create(
            90000001,
            new(null, BookmarkRules.SolarSystemTypeId, f.Context.Pilot.SystemId, Fixture.Start + new SpacePosition(1_000_000_000, 0, 0)),
            folder,
            null,
            new("Old asteroid", ""),
            BookmarkExpiry.Never
        );
        using (WorldDbContext world = new(f.Db.Path))
        {
            var row = world.Set<SavedBookmark>().Single(bookmark => bookmark.Id == saved.Id);
            row.ItemId = AsteroidDefinitions.FirstAsteroidId;
            row.TypeId = 1230;
            world.SaveChanges();
        }
        Assert.Equal(saved.Target, Assert.Single(f.Store.Read(90000001).Bookmarks).Target);
        Assert.Equal(saved.Target, f.Store.Find(90000001, saved.Id).Target);
        Assert.Equal(saved.Target, Assert.Single(f.Store.MoveOrCopy(90000001, folder, folder, null, [saved.Id], true)).Target);
        Assert.IsType<PilotCallResult.Success>(f.Call("CmdWarpToStuff", ["bookmark", saved.Id], "beyonce"));
        Assert.Equal(
            saved.Target,
            new Eden.World.Bookmarks.PilotBookmarksStore(new WorldDatabase(f.Db.Path), f.Clock).Find(90000001, saved.Id).Target
        );
    }

    private static BookmarksHandler Handler(Fixture f, IReadOnlyList<StaticSpaceObject> objects) =>
        new(
            f.Store,
            new BookmarkMonitor(f.Store, f.Clock),
            new BookmarkObjects(objects),
            f.Db.Services.GetRequiredService<ICombatAnomalies>(),
            f.Db.Services.GetRequiredService<IPilotProbeScanning>(),
            f.Clock
        );
}
