using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Bookmarks;

[Injectable]
internal sealed class BookmarksHandler(IPilotBookmarks bookmarks, ISystemObjects systems) : ISelectedPilotCallHandler
{
    private const int PersonalAccessLevel = 1;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool park = service == "beyonce" && method == "BookmarkLocation";
        if (
            !park
            && (
                service != "accessGroupBookmarkMgr"
                || method
                    is not (
                        "GetMyActiveBookmarks"
                        or "AddFolder"
                        or "UpdateFolder"
                        or "DeleteFolder"
                        or "GetFolderInfo"
                        or "AddToKnownFolders"
                        or "RemoveFromKnownFolders"
                        or "UpdateKnownFolderState"
                        or "SearchFoldersWithAdminAccess"
                        or "CreateSubfolder"
                        or "UpdateSubfolder"
                        or "DeleteSubfolder"
                        or "BookmarkStaticLocation"
                        or "UpdateBookmark"
                        or "DeleteBookmarks"
                        or "MoveBookmarksToFolderAndSubfolder"
                        or "CopyBookmarksToFolderAndSubfolder"
                    )
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            bool creating = park || method == "BookmarkStaticLocation";
            if (keywords is { Count: > 0 } && (!creating || keywords.Keys.Any(key => key != "subfolderID")))
            {
                throw new InvalidDataException("Unexpected personal location arguments.");
            }
            long pilotId = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetMyActiveBookmarks", []):
                    return new BookmarkContentsReply(bookmarks.Read(pilotId));
                case ("AddFolder", [true, var name, var description, null, null, null, null]):
                    return new BookmarkFolderReply(bookmarks.AddFolder(pilotId, Text(name), Text(description)));
                case ("UpdateFolder", [long folder, var name, var description, null, null, null, null]):
                    bookmarks.UpdateFolder(pilotId, folder, Text(name), Text(description));
                    return PersonalAccessLevel;
                case ("DeleteFolder", [long folder]):
                    bookmarks.DeleteFolder(pilotId, folder);
                    break;
                case ("GetFolderInfo", [long folder]):
                    return new BookmarkFolderReply(bookmarks.Folder(pilotId, folder));
                case ("AddToKnownFolders" or "UpdateKnownFolderState", [long folder, bool active]):
                    return new BookmarkFolderContentsReply(bookmarks.SetFolderState(pilotId, folder, active));
                case ("RemoveFromKnownFolders", [long folder]):
                    bookmarks.SetFolderState(pilotId, folder, false, false);
                    break;
                case ("SearchFoldersWithAdminAccess", []):
                    return new BlueList<BookmarkFolderReply>(
                        bookmarks.SearchFolders(pilotId).Select(f => new BookmarkFolderReply(f)).ToArray()
                    );
                case ("CreateSubfolder", [long folder, var name]):
                    return new BookmarkSubfolderReply(bookmarks.AddSubfolder(pilotId, folder, Text(name)));
                case ("UpdateSubfolder", [long folder, long subfolder, var name]):
                    bookmarks.UpdateSubfolder(pilotId, folder, subfolder, Text(name));
                    return true;
                case ("DeleteSubfolder", [long folder, long subfolder]):
                    return new BlueList<long>(bookmarks.DeleteSubfolder(pilotId, folder, subfolder));
                case ("BookmarkStaticLocation" or "BookmarkLocation", [long item, long folder, var name, var note, long expiry]):
                    var text = new BookmarkText(Text(name), Text(note));
                    var duration = Expiry(expiry);
                    long? subfolderId = OptionalId(keywords?.GetValueOrDefault("subfolderID"));
                    var saved = park
                        ? bookmarks.Create(pilotId, CurrentLocation(context, item), folder, subfolderId, text, duration)
                        : bookmarks.CreateStatic(pilotId, item, folder, subfolderId, text, duration);
                    return new BookmarkCreationReply(saved);
                case ("UpdateBookmark", [long id, long folder, var name, var note, var subfolder, long destination, var cancel]):
                    bookmarks.Update(
                        pilotId,
                        new(id, folder, destination, OptionalId(subfolder), new(Text(name), Text(note)), Flag(cancel))
                    );
                    break;
                case ("DeleteBookmarks", [long folder, var ids]):
                    return new BlueList<long>(bookmarks.Delete(pilotId, folder, Ids(ids)));
                case (
                    "MoveBookmarksToFolderAndSubfolder"
                        or "CopyBookmarksToFolderAndSubfolder",
                    [long source, long destination, var subfolder, var ids]
                ):
                    bool copy = method == "CopyBookmarksToFolderAndSubfolder";
                    return new BookmarkMoveReply(
                        bookmarks.MoveOrCopy(pilotId, source, destination, OptionalId(subfolder), Ids(ids), copy),
                        copy
                    );
                case ("AddFolder" or "UpdateFolder", _):
                    return new PilotCallResult.Failure("Choose a private personal folder; shared access groups are not available yet.");
                default:
                    return new PilotCallResult.Failure("Invalid personal location request.");
            }
            // The native bookmark service mutates its local cache after each RPC.
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private BookmarkTarget CurrentLocation(PilotCallContext context, long itemId)
    {
        context.Flight.Checkpoint();
        var pilot = context.Pilot;
        if (!context.Flight.InBallpark || pilot.Position is not { } position)
        {
            throw new InvalidDataException("Enter space before saving an in-flight location.");
        }
        if (itemId == pilot.ShipId)
        {
            return new(null, BookmarkRules.SolarSystemTypeId, pilot.SystemId, position);
        }
        if (systems.Load(pilot.SystemId).FirstOrDefault(body => body.Id == itemId) is { } body)
        {
            return new(body.Id, body.TypeId, pilot.SystemId, body.Position);
        }
        if (context.Flight.FindTarget(itemId) is { } target)
        {
            // Mobile objects are saved as coordinates, so disappearance does not
            // turn a bookmark into a reference to a different later object.
            return new(null, BookmarkRules.SolarSystemTypeId, pilot.SystemId, target.Position);
        }
        throw new InvalidDataException("Choose your ship or a visible location in this solar system.");
    }

    private static string Text(object? value) =>
        value is string or byte[] ? BlueMarshal.Text(value) : throw new InvalidDataException("Expected a location name or note.");

    private static long? OptionalId(object? value) =>
        value switch
        {
            null => null,
            long id when id > 0 => id,
            _ => throw new InvalidDataException("Choose a valid location subfolder."),
        };

    private static bool Flag(object? value) =>
        value switch
        {
            bool flag => flag,
            // BookmarkLocationWindow initializes expiryCancel to the Python integer 0.
            long number when number is 0 or 1 => number != 0,
            _ => throw new InvalidDataException("Choose whether to cancel the location expiry."),
        };

    private static BookmarkExpiry Expiry(long value) =>
        value is >= (long)BookmarkExpiry.Never and <= (long)BookmarkExpiry.OneDay
            ? (BookmarkExpiry)value
            : throw new InvalidDataException("Choose a valid location expiry.");

    private static IReadOnlyList<long> Ids(object? value)
    {
        IEnumerable<long> ids = value switch
        {
            HashSet<long> set => set,
            List<object?> list when list.All(item => item is long) => list.Cast<long>(),
            _ => throw new InvalidDataException("Expected a list or set of personal location IDs."),
        };
        long[] result = ids.ToArray();
        BookmarkRules.CheckBatch(result);
        return result;
    }
}
