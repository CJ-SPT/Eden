using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Flight;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotBookmarksTests
{
    private const long Pilot = 90000001;
    private const long Wing = 90000002;

    [Theory]
    [InlineData(10000002, BookmarkRules.RegionTypeId)]
    [InlineData(20000020, BookmarkRules.ConstellationTypeId)]
    [InlineData(30000142, BookmarkRules.SolarSystemTypeId)]
    public void StaticMapDestinationsRetainTheirNativeTypeAndLocation(long itemId, int typeId)
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var folder = store.AddFolder(Pilot, "Map", "");
        var saved = store.CreateStatic(Pilot, itemId, folder.Id, null, new("Destination", ""), BookmarkExpiry.Never);
        Assert.Equal(itemId, saved.Target.ItemId);
        Assert.Equal(typeId, saved.Target.TypeId);
        Assert.Equal(itemId, saved.Target.LocationId);
        Assert.Equal(saved, store.Find(Pilot, saved.Id));
    }

    [Fact]
    public void InvalidTextCoordinatesExpiryAndBatchesLeaveSavedStateUnchangedAndExactTextLimitsAreAccepted()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var folder = store.AddFolder(Pilot, "Original", "Keep me");
        BookmarkTarget target = new(null, BookmarkRules.SolarSystemTypeId, 30000142, new(1, 2, 3));
        var saved = store.Create(Pilot, target, folder.Id, null, new("Saved", ""), BookmarkExpiry.Never);
        foreach (string name in new[] { "", " ", "bad\0name", new string('n', BookmarkRules.MaximumNameLength + 1) })
        {
            Assert.Throws<InvalidDataException>(() => store.AddFolder(Pilot, name, ""));
            Assert.Throws<InvalidDataException>(() => store.UpdateFolder(Pilot, folder.Id, name, ""));
            Assert.Throws<InvalidDataException>(() => store.Create(Pilot, target, folder.Id, null, new(name, ""), BookmarkExpiry.Never));
        }
        foreach (var position in new[] { new SpacePosition(double.NaN, 0, 0), new(0, double.PositiveInfinity, 0), new(0, 0, double.NaN) })
        {
            Assert.Throws<InvalidDataException>(() =>
                store.Create(Pilot, target with { Position = position }, folder.Id, null, new("Invalid", ""), BookmarkExpiry.Never)
            );
        }
        Assert.Throws<InvalidDataException>(() =>
            store.CreateStatic(Pilot, long.MaxValue, folder.Id, null, new("Bad", ""), BookmarkExpiry.Never)
        );
        Assert.Throws<InvalidDataException>(() => store.Create(Pilot, target, folder.Id, null, new("Bad", ""), (BookmarkExpiry)4));
        Assert.Throws<InvalidDataException>(() => store.Delete(Pilot, folder.Id, []));
        Assert.Throws<InvalidDataException>(() => store.Delete(Pilot, folder.Id, new long[BookmarkRules.MaximumBookmarksPerFolder + 1]));
        Assert.Equal(folder, Assert.Single(store.Read(Pilot).Folders));
        Assert.Equal(saved, Assert.Single(store.Read(Pilot).Bookmarks));
        var longest = store.Create(
            Pilot,
            target,
            folder.Id,
            null,
            new(new string('n', BookmarkRules.MaximumNameLength), new string('d', BookmarkRules.MaximumDescriptionLength)),
            BookmarkExpiry.Never
        );
        Assert.Equal(longest, store.Find(Pilot, longest.Id));
    }

    [Fact]
    public void PersonalFoldersLocationsCopiesAndSubfoldersSurviveRestartAndCascadeTogether()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        Assert.Empty(store.Read(Pilot).Folders);
        var folder = store.AddFolder(Pilot, "星間旅行", "<b>Notes</b>");
        var other = store.AddFolder(Pilot, "Destinations", "");
        var subfolder = store.AddSubfolder(Pilot, folder.Id, "Mining");
        var saved = store.CreateStatic(Pilot, 60003760, folder.Id, subfolder.Id, new("Home", "Unicode 🚀"), BookmarkExpiry.Never);
        store.UpdateFolder(Pilot, folder.Id, "Personal", "Updated");
        store.UpdateSubfolder(Pilot, folder.Id, subfolder.Id, "Trade");
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var persistent = reopened.GetRequiredService<IPilotBookmarks>();
        var contents = persistent.Read(Pilot);
        Assert.Equal("Personal", contents.Folders[0].Name);
        Assert.Equal("Trade", Assert.Single(contents.Subfolders).Name);
        Assert.Equal(saved, Assert.Single(contents.Bookmarks));
        Assert.Empty(persistent.Read(Wing).Folders);
        var copied = Assert.Single(persistent.MoveOrCopy(Pilot, folder.Id, other.Id, null, [saved.Id], true));
        Assert.NotEqual(saved.Id, copied.Id);
        Assert.Equal(saved.Target, copied.Target);
        Assert.Equal(saved.Text, copied.Text);
        Assert.Null(copied.SubfolderId);
        Assert.Equal(2, persistent.Read(Pilot).Bookmarks.Count);
        persistent.Update(Pilot, new(copied.Id, other.Id, other.Id, null, new("Copy", "Changed"), true));
        Assert.Equal("Home", persistent.Find(Pilot, saved.Id).Text.Name);
        Assert.Equal("Copy", persistent.Find(Pilot, copied.Id).Text.Name);
        persistent.MoveOrCopy(Pilot, other.Id, folder.Id, subfolder.Id, [copied.Id], false);
        Assert.Equal(new[] { saved.Id, copied.Id }, persistent.DeleteSubfolder(Pilot, folder.Id, subfolder.Id));
        Assert.Empty(persistent.Read(Pilot).Bookmarks);
        Assert.Empty(persistent.Read(Pilot).Subfolders);
        persistent.DeleteFolder(Pilot, folder.Id);
        WorldInitialization.Open(db.Path);
        Assert.Equal(other.Id, Assert.Single(persistent.Read(Pilot).Folders).Id);
    }

    [Fact]
    public void ForeignAndMixedBatchOperationsRejectWithoutChangingEitherPilotsLocations()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var own = store.AddFolder(Pilot, "Own", "");
        var foreign = store.AddFolder(Wing, "Wing", "");
        var subfolder = store.AddSubfolder(Wing, foreign.Id, "Wing subfolder");
        var saved = store.CreateStatic(Pilot, 60003760, own.Id, null, new("Home", ""), BookmarkExpiry.Never);
        var theirs = store.CreateStatic(Wing, 60003760, foreign.Id, null, new("Their home", ""), BookmarkExpiry.Never);
        Assert.Throws<InvalidDataException>(() => store.Folder(Pilot, foreign.Id));
        Assert.Throws<InvalidDataException>(() => store.Find(Pilot, theirs.Id));
        Assert.Throws<InvalidDataException>(() => store.UpdateFolder(Pilot, foreign.Id, "Stolen", ""));
        Assert.Throws<InvalidDataException>(() => store.DeleteFolder(Pilot, foreign.Id));
        Assert.Throws<InvalidDataException>(() => store.SetFolderState(Pilot, foreign.Id, false));
        Assert.Throws<InvalidDataException>(() =>
            store.CreateStatic(Pilot, 60003760, own.Id, subfolder.Id, new("Bad", ""), BookmarkExpiry.Never)
        );
        Assert.Throws<InvalidDataException>(() => store.DeleteSubfolder(Pilot, foreign.Id, subfolder.Id));
        Assert.Throws<InvalidDataException>(() => store.Delete(Pilot, own.Id, [saved.Id, theirs.Id]));
        Assert.Throws<InvalidDataException>(() => store.MoveOrCopy(Pilot, own.Id, foreign.Id, null, [saved.Id], false));
        Assert.Throws<InvalidDataException>(() => store.MoveOrCopy(Pilot, own.Id, own.Id, null, [saved.Id, theirs.Id], true));
        Assert.Throws<InvalidDataException>(() => store.Update(Pilot, new(saved.Id, own.Id, foreign.Id, null, new("Bad", ""), true)));
        Assert.Equal(saved, Assert.Single(store.Read(Pilot).Bookmarks));
        Assert.Equal(theirs, Assert.Single(store.Read(Wing).Bookmarks));
        Assert.Single(store.Read(Wing).Subfolders);
    }

    [Theory]
    [InlineData(BookmarkExpiry.FourHours, 4)]
    [InlineData(BookmarkExpiry.OneDay, 24)]
    [InlineData(BookmarkExpiry.TwoDays, 48)]
    public void ExpiryBoundaryAndCancellationPersistWithoutResurrectingExpiredLocations(BookmarkExpiry expiry, int hours)
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var folder = store.AddFolder(Pilot, "Travel", "");
        var expiring = store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Expires", ""), expiry);
        var retained = store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Keep", ""), expiry);
        Assert.Equal(clock.Now + TimeSpan.FromHours(hours).Ticks, expiring.ExpiresAt);
        store.Update(Pilot, new(retained.Id, folder.Id, folder.Id, null, retained.Text, true));
        clock.AdvanceWallTime(TimeSpan.FromHours(hours) - TimeSpan.FromTicks(1));
        Assert.Equal(2, store.Read(Pilot).Bookmarks.Count);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(retained.Id, Assert.Single(store.Read(Pilot).Bookmarks).Id);
        Assert.Throws<InvalidDataException>(() => store.Find(Pilot, expiring.Id));
        Assert.Throws<InvalidDataException>(() => store.Update(Pilot, new(expiring.Id, folder.Id, folder.Id, null, expiring.Text, true)));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var persistent = reopened.GetRequiredService<IPilotBookmarks>();
        Assert.Null(persistent.Find(Pilot, retained.Id).ExpiresAt);
        Assert.Equal(retained.Id, Assert.Single(persistent.Read(Pilot).Bookmarks).Id);
    }

    [Fact]
    public void FolderVisibilityAndNativeLimitsPreserveSavedContents()
    {
        using TestWorld db = new();
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var first = store.AddFolder(Pilot, "First", "");
        var bookmark = store.CreateStatic(Pilot, 60003760, first.Id, null, new("Home", ""), BookmarkExpiry.Never);
        for (int index = 1; index < BookmarkRules.MaximumActiveFolders; index++)
        {
            store.AddFolder(Pilot, $"Folder {index}", "");
        }
        Assert.Throws<InvalidDataException>(() => store.AddFolder(Pilot, "Too many active", ""));
        store.SetFolderState(Pilot, first.Id, false);
        Assert.Empty(store.Read(Pilot).Bookmarks);
        Assert.Throws<InvalidDataException>(() => store.Find(Pilot, bookmark.Id));
        for (int index = BookmarkRules.MaximumActiveFolders; index < BookmarkRules.MaximumFolders; index++)
        {
            var folder = store.AddFolder(Pilot, $"Folder {index}", "");
            store.SetFolderState(Pilot, folder.Id, false);
        }
        Assert.Throws<InvalidDataException>(() => store.AddFolder(Pilot, "Too many folders", ""));
        store.SetFolderState(Pilot, first.Id, false, false);
        Assert.Equal(BookmarkRules.MaximumFolders - 1, store.Read(Pilot).Folders.Count);
        Assert.Equal(BookmarkRules.MaximumFolders, store.SearchFolders(Pilot).Count);
        var restored = store.SetFolderState(Pilot, first.Id, true);
        Assert.Equal(bookmark, Assert.Single(restored.Bookmarks));
        Assert.Equal(bookmark, store.Find(Pilot, bookmark.Id));
    }

    [Fact]
    public void FullFolderRejectsCopiesAndMovesAtomicallyAndExpiredSlotsBecomeAvailable()
    {
        TestClock clock = new();
        using TestWorld db = new(clock: clock);
        var store = db.Services.GetRequiredService<IPilotBookmarks>();
        var source = store.AddFolder(Pilot, "Source", "");
        var destination = store.AddFolder(Pilot, "Full", "");
        var original = store.CreateStatic(Pilot, 60003760, source.Id, null, new("Home", ""), BookmarkExpiry.Never);
        using (WorldDbContext world = new(db.Path))
        {
            for (int index = 0; index < BookmarkRules.MaximumBookmarksPerFolder; index++)
            {
                world.Add(
                    new SavedBookmark
                    {
                        FolderId = destination.Id,
                        CreatorId = Pilot,
                        TypeId = 5,
                        LocationId = 30000142,
                        Name = $"Existing {index}",
                        ExpiresAt = clock.Now + TimeSpan.FromHours(4).Ticks,
                    }
                );
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => store.MoveOrCopy(Pilot, source.Id, destination.Id, null, [original.Id], false));
        Assert.Throws<InvalidDataException>(() => store.MoveOrCopy(Pilot, source.Id, destination.Id, null, [original.Id], true));
        Assert.Equal(original, store.Find(Pilot, original.Id));
        Assert.Equal(BookmarkRules.MaximumBookmarksPerFolder + 1, store.Read(Pilot).Bookmarks.Count);
        clock.AdvanceWallTime(TimeSpan.FromHours(4));
        var moved = Assert.Single(store.MoveOrCopy(Pilot, source.Id, destination.Id, null, [original.Id], false));
        Assert.Equal(destination.Id, moved.FolderId);
        Assert.Equal(moved, Assert.Single(store.Read(Pilot).Bookmarks));
    }

    [Fact]
    public void Version41MigrationPreservesContactsAndSettingsAndCreatesIndependentBookmarkStorage()
    {
        using TestWorld db = new();
        db.Services.GetRequiredService<IPilotContacts>().Add(Pilot, Wing, 5, false);
        db.Services.GetRequiredService<IPilotCharacterSettings>().Save(Pilot, new("layout", new CharacterSettingValue.Text("keep")));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldBookmark; DROP TABLE WorldBookmarkSubfolder; DROP TABLE WorldBookmarkFolder; PRAGMA user_version=41"
            );
        }
        WorldInitialization.Open(db.Path);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var store = reopened.GetRequiredService<IPilotBookmarks>();
        Assert.Empty(store.Read(Pilot).Folders);
        Assert.Equal(Wing, Assert.Single(reopened.GetRequiredService<IPilotContacts>().Read(Pilot).Addresses).ContactId);
        Assert.Equal(
            "keep",
            Assert
                .IsType<CharacterSettingValue.Text>(Assert.Single(reopened.GetRequiredService<IPilotCharacterSettings>().Read(Pilot)).Value)
                .Value
        );
        var folder = store.AddFolder(Pilot, "Migrated", "");
        store.CreateStatic(Pilot, 60003760, folder.Id, null, new("Home", ""), BookmarkExpiry.Never);
        Assert.Single(store.Read(Pilot).Bookmarks);
    }
}
