namespace Eden.Gameplay.Characters;

public sealed record PersonalContact(long ContactId, double Standing, bool InWatchlist, long LabelMask);

public sealed record ContactLabel(long Id, string Name, int? Color);

public sealed record PersonalContacts(IReadOnlyList<PersonalContact> Addresses, IReadOnlyList<long> Blocked);

public sealed record CharacterIdentity(long Id, string Name, int Gender, long? NameId = null, bool IsNpc = false);

public interface ICharacterDirectory
{
    IReadOnlyList<CharacterIdentity> Read(IReadOnlyList<long> ids);
}

public interface IPilotContacts
{
    PersonalContacts Read(long pilotId);
    void Add(long pilotId, long contactId, double standing, bool inWatchlist, string? notification = null);
    void AddAgent(long pilotId, long agentId);
    void Edit(long pilotId, long contactId, double standing, bool inWatchlist, string? notification = null);
    void EditStanding(long pilotId, IReadOnlyList<long> contacts, double standing);
    void Delete(long pilotId, IReadOnlyList<long> contacts);
    void SetBlocked(long pilotId, IReadOnlyList<long> contacts, bool blocked);
    IReadOnlyList<ContactLabel> Labels(long pilotId);
    long CreateLabel(long pilotId, string name, int? color);
    void EditLabel(long pilotId, long labelId, string? name, int? color);
    void DeleteLabel(long pilotId, long labelId);
    void SetLabels(long pilotId, IReadOnlyList<long> contacts, long mask, bool assign);
}

public static class ContactRules
{
    public const int MaximumBatchSize = 1024;
    public const int MaximumLabelNameLength = 40;

    // Contact labels are individual bits in the client's signed long labelMask.
    public const int MaximumLabels = 63;
    public const double MinimumStanding = -10;
    public const double MaximumStanding = 10;

    public static void CheckStanding(double standing)
    {
        if (!double.IsFinite(standing) || standing is < MinimumStanding or > MaximumStanding)
        {
            throw new InvalidDataException("Contact standing must be between -10 and 10.");
        }
    }

    public static void CheckContacts(long pilotId, IReadOnlyList<long> contacts)
    {
        if (contacts.Count is < 1 or > MaximumBatchSize || contacts.Any(id => id <= 0 || id == pilotId))
        {
            throw new InvalidDataException("Choose other known pilots as contacts.");
        }
    }

    public static void CheckLabelName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumLabelNameLength || name.Contains('\0'))
        {
            throw new InvalidDataException($"Contact labels need a name of at most {MaximumLabelNameLength} characters.");
        }
    }

    public static void CheckLabelColor(int? color)
    {
        if (color < 0)
        {
            throw new InvalidDataException("Choose a valid contact label color.");
        }
    }

    public static void CheckLabelId(long id)
    {
        if (id <= 0 || (id & (id - 1)) != 0)
        {
            throw new InvalidDataException("Choose one contact label.");
        }
    }
}
