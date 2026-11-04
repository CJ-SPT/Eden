using System.Globalization;

namespace Eden.Gameplay.Mail;

public enum MailingListAccess
{
    Blocked = 0,
    Allowed = 1,
}

public enum MailingListRole
{
    Muted = 0,
    Member = 1,
    Operator = 2,
    Owner = 3,
}

public sealed record MailingListName(string Key, string Display);

public sealed record MailingListInfo(long Id, MailingListName Name, MailingListRole Role);

public sealed record MailingListDefaults(MailingListAccess Access, MailingListRole Role);

public sealed record MailingListSettings(MailingListDefaults Defaults, IReadOnlyDictionary<long, MailingListAccess> Access);

public sealed record MailingListWelcome(string Title, string Body);

public enum MailingListChangeKind
{
    Joined,
    Role,
    Left,
    Deleted,
}

public sealed record MailingListChange(long Id, long ListId, MailingListChangeKind Kind, MailingListRole Role, Guid Origin);

public sealed record MailingListChanges(long Cursor, bool More, IReadOnlyList<MailingListChange> Items);

public interface IPilotMailingLists
{
    IReadOnlyList<MailingListInfo> Joined(long pilotId);
    MailingListInfo? Info(long pilotId, long listId);
    long Create(long pilotId, string name, MailingListDefaults defaults, Guid origin);
    MailingListInfo Join(long pilotId, string name, Guid origin);
    void Leave(long pilotId, long listId, Guid origin);
    void Delete(long pilotId, long listId, Guid origin);
    IReadOnlyDictionary<long, MailingListRole> Members(long pilotId, long listId);
    void ChangeMembers(long pilotId, long listId, IReadOnlyList<long> members, MailingListRole? role, Guid origin);
    MailingListSettings Settings(long pilotId, long listId);
    void SetDefaults(long pilotId, long listId, MailingListDefaults defaults);
    void SetAccess(long pilotId, long listId, IReadOnlyDictionary<long, MailingListAccess> access, Guid origin);
    void ClearAccess(long pilotId, long listId, long entityId);
    MailingListWelcome? Welcome(long pilotId, long listId);
    void SaveWelcome(long pilotId, long listId, MailingListWelcome? welcome, bool send, Guid origin);
    long Observe(long pilotId);
    MailingListChanges Changes(long pilotId, long cursor);
}

public static class MailingListRules
{
    // Build 3662168 appConst and format.GetKeyAndNormalize.
    public const int MaximumName = 60;
    public const int MaximumMembers = 3000;
    public const int MaximumUpdated = 1000;

    // Pinned Python 2.7 Unicode 5.2 isspace(), including historical U+180E.
    private const string NativeWhitespace =
        "\u0009\u000A\u000B\u000C\u000D\u001C\u001D\u001E\u001F\u0020\u0085\u00A0"
        + "\u1680\u180E\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009"
        + "\u200A\u2028\u2029\u202F\u205F\u3000";
    private static readonly IReadOnlyDictionary<char, string> FoldMap = ReadFoldMap();

    private static IReadOnlyDictionary<char, string> ReadFoldMap()
    {
        using var stream =
            typeof(MailingListRules).Assembly.GetManifestResourceStream("Eden.Gameplay.Mail.MailingListCaseFold.txt")
            ?? throw new InvalidDataException("Missing pinned mailing-list case-fold data.");
        using StreamReader reader = new(stream);
        Dictionary<char, string> result = [];
        while (reader.ReadLine() is string line)
        {
            if (line.StartsWith('#'))
            {
                continue;
            }
            var pair = line.Split('=');
            char source = (char)ushort.Parse(pair[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string target = new(
                pair[1].Split(',').Select(unit => (char)ushort.Parse(unit, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray()
            );
            result.Add(source, target);
        }
        return result;
    }

    public static MailingListName Name(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumName || name.Contains('\0'))
        {
            throw new InvalidDataException("Mailing lists need a name of 1 to 60 characters.");
        }
        string display = name;
        string key = name;
        foreach (char space in name.Where(NativeWhitespace.Contains).Distinct())
        {
            key = key.Replace(space.ToString(), "", StringComparison.Ordinal);
            string repeated = new(space, 2);
            while (display.Contains(repeated, StringComparison.Ordinal))
            {
                display = display.Replace(repeated, space.ToString(), StringComparison.Ordinal);
            }
        }
        key = string.Concat(key.Split('\\')[^1].Select(unit => FoldMap.GetValueOrDefault(unit, unit.ToString())));
        if (key.Length == 0)
        {
            throw new InvalidDataException("Mailing list names need a nonempty lookup key.");
        }
        return new(key, display);
    }

    public static void Defaults(MailingListDefaults defaults)
    {
        if (!Enum.IsDefined(defaults.Access) || defaults.Role is not (MailingListRole.Member or MailingListRole.Muted))
        {
            throw new InvalidDataException("Choose allowed/blocked access and member/muted defaults.");
        }
    }

    public static void Manager(MailingListRole role)
    {
        if (role is not (MailingListRole.Operator or MailingListRole.Owner))
        {
            throw new InvalidDataException("Only mailing-list operators and the owner can manage this list.");
        }
    }

    public static void Members(IReadOnlyList<long> members)
    {
        if (members.Count is < 1 or > MaximumUpdated || members.Any(id => id <= 0) || members.Distinct().Count() != members.Count)
        {
            throw new InvalidDataException("Choose up to 1000 distinct list members.");
        }
    }

    public static void Capacity(int count)
    {
        if (count >= MaximumMembers)
        {
            throw new InvalidDataException("This mailing list already has 3000 members.");
        }
    }

    public static MailingListAccess Access(
        long pilot,
        long corporation,
        MailingListAccess fallback,
        IReadOnlyDictionary<long, MailingListAccess> entries
    ) => entries.TryGetValue(pilot, out var personal) ? personal : entries.GetValueOrDefault(corporation, fallback);
}
