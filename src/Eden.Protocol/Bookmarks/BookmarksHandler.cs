using Eden.Gameplay.Bookmarks;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Bookmarks;

[Injectable]
internal sealed class BookmarksHandler(
    IPilotBookmarks bookmarks,
    BookmarkMonitor monitor,
    ISystemObjects systems,
    ICombatAnomalies sites,
    IPilotProbeScanning scanning,
    TimeProvider clock
) : ISelectedPilotCallHandler
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
        bool park = service == "beyonce" && method is "BookmarkLocation" or "BookmarkScanResult";
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
                throw new InvalidDataException("Unexpected location arguments.");
            }
            long pilotId = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetMyActiveBookmarks", []):
                    return new BookmarkContentsReply(monitor.Read(context));
                case ("AddFolder", [true, var name, var description, null, null, null, null]):
                    return new BookmarkFolderReply(bookmarks.AddFolder(pilotId, Text(name), Text(description)));
                case ("AddFolder", [false, var name, var description, var admin, var manage, var use, var view]):
                    return new BookmarkFolderReply(
                        bookmarks.AddSharedFolder(
                            pilotId,
                            Text(name),
                            Text(description),
                            new(OptionalId(admin), OptionalId(manage), OptionalId(use), OptionalId(view))
                        )
                    );
                case ("UpdateFolder", [long folder, var name, var description, var admin, var manage, var use, var view]):
                    if (bookmarks.Folder(pilotId, folder).IsPersonal)
                    {
                        if (admin is not null || manage is not null || use is not null || view is not null)
                        {
                            throw new InvalidDataException("Personal folders cannot use access lists.");
                        }
                        bookmarks.UpdateFolder(pilotId, folder, Text(name), Text(description));
                        return PersonalAccessLevel;
                    }
                    return (int)
                        bookmarks.UpdateSharedFolder(
                            pilotId,
                            folder,
                            Text(name),
                            Text(description),
                            new(OptionalId(admin), OptionalId(manage), OptionalId(use), OptionalId(view))
                        );
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
                        : StaticLocation(context, item, folder, subfolderId, text, duration);
                    return new BookmarkCreationReply(saved);
                case ("BookmarkScanResult", [long system, var name, var note, var result, long folder, long expiry]):
                    var scanTarget = ScanLocation(context, system, result);
                    return new BookmarkCreationReply(
                        bookmarks.Create(
                            pilotId,
                            scanTarget,
                            folder,
                            OptionalId(keywords?.GetValueOrDefault("subfolderID")),
                            new(Text(name), Text(note)),
                            Expiry(expiry)
                        )
                    );
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
                    return new PilotCallResult.Failure("Invalid location folder arguments.");
                default:
                    return new PilotCallResult.Failure("Invalid location request.");
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
        if (itemId <= int.MaxValue && systems.Load(pilot.SystemId).FirstOrDefault(body => body.Id == itemId) is { } body)
        {
            return new(body.Id, body.TypeId, pilot.SystemId, body.Position);
        }
        if (context.Flight.FindBookmarkObject(itemId) is { } target)
        {
            // Generated rocks, scenery, ships and other temporary objects remain useful
            // as coordinates after depletion, destruction, movement or removal.
            return new(null, BookmarkRules.SolarSystemTypeId, pilot.SystemId, target.Position);
        }
        throw new InvalidDataException("Choose your ship or a visible location in this solar system.");
    }

    private PersonalBookmark StaticLocation(
        PilotCallContext context,
        long item,
        long folder,
        long? subfolder,
        BookmarkText text,
        BookmarkExpiry expiry
    )
    {
        if (item <= int.MaxValue)
        {
            return bookmarks.CreateStatic(context.Pilot.PilotId, item, folder, subfolder, text, expiry);
        }
        // The native client routes Upwell structures through BookmarkStaticLocation too.
        var structure =
            systems.Load(context.Pilot.SystemId).SingleOrDefault(body => body.Id == item && body.CategoryId == StructureRules.CategoryId)
            ?? throw new InvalidDataException("Choose a known structure in your current solar system.");
        return bookmarks.Create(
            context.Pilot.PilotId,
            new(null, BookmarkRules.SolarSystemTypeId, context.Pilot.SystemId, structure.Position),
            folder,
            subfolder,
            text,
            expiry
        );
    }

    private BookmarkTarget ScanLocation(PilotCallContext context, long system, object? result)
    {
        context.Flight.Checkpoint();
        if (!context.Flight.InBallpark || context.Pilot.Position is null || system != context.Pilot.SystemId)
        {
            throw new InvalidDataException("Bookmark scan results while in space in their solar system.");
        }
        string? target = result is string or byte[] ? BlueMarshal.Text(result) : null;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var site = sites
            .List(context.Pilot.SystemId, now)
            .Concat(sites.Signatures(context.Pilot.SystemId, now))
            .SingleOrDefault(site => target is not null ? site.TargetId == target : result is long id && site.Id == id);
        if (site is null || site.IsSignature && !scanning.Resolved(context.Pilot.PilotId, site.Id, site.SystemId, now))
        {
            throw new InvalidDataException("Choose a current public anomaly or a cosmic signature you have resolved to 100%.");
        }
        return new(null, BookmarkRules.SolarSystemTypeId, site.SystemId, site.Position);
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
