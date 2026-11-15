namespace Eden.Gameplay.Inventory;

// Build 3662168 appConst.SCCPasswordType* and ALSC* constants.
public enum CargoContainerPasswordType
{
    General = 1,
    Configuration = 2,
}

[Flags]
public enum CargoContainerConfiguration
{
    None = 0,
    PasswordToOpen = 1,
    PasswordToLock = 2,
    PasswordToUnlock = 4,
    PasswordToViewLog = 8,
    LockAddedItems = 16,
    All = PasswordToOpen | PasswordToLock | PasswordToUnlock | PasswordToViewLog | LockAddedItems,
}

public sealed record CargoContainerAccess(string? General = null, string? Configuration = null)
{
    public string? Password(CargoContainerPasswordType type) => type == CargoContainerPasswordType.General ? General : Configuration;
}

public sealed record CargoContainerContents(InventoryItem Item, IReadOnlyList<InventoryItem> Items, double Capacity);

public interface ICargoContainers
{
    bool Handles(long itemId);
    IReadOnlyList<PersonalAssetName> Names(long pilotId, IReadOnlySet<long> itemIds);
    CargoContainerContents Read(long pilotId, long itemId, CargoContainerAccess access, bool includeContents = true);
    CargoContainerContents Preview(long pilotId, long itemId, long stationId, CargoContainerAccess access);
    string Rename(long pilotId, long itemId, string name);
    bool HasPassword(long pilotId, long itemId, CargoContainerPasswordType type);
    void SetPassword(long pilotId, long itemId, CargoContainerPasswordType type, string? oldPassword, string newPassword);
    string? RetrievePassword(long pilotId, long itemId, CargoContainerPasswordType type);
    void EnterPassword(long pilotId, long itemId, CargoContainerPasswordType type, string password);
    CargoContainerConfiguration Configuration(long pilotId, long itemId, CargoContainerAccess access);
    void Configure(long pilotId, long itemId, CargoContainerConfiguration configuration, CargoContainerAccess access);
    IReadOnlyList<InventoryChange> Move(long pilotId, long destination, InventoryMoveRequest request, CargoContainerAccess access);
    IReadOnlyList<InventoryChange> StackAll(long pilotId, long itemId, int? flagId, CargoContainerAccess access);
    IReadOnlyList<InventoryChange> Merge(
        long pilotId,
        long itemId,
        IReadOnlyList<InventoryStackMerge> operations,
        CargoContainerAccess access
    );
    IReadOnlyList<InventoryChange> Lock(long pilotId, long itemId, IReadOnlyList<long> ids, bool locked, CargoContainerAccess access);
}

public sealed class CargoContainerPasswordRequiredException(CargoContainerPasswordType type) : Exception
{
    public CargoContainerPasswordType Type { get; } = type;
}

public static class CargoContainerRules
{
    public const int MaximumNameLength = 100;
    public const int MinimumPasswordLength = 3;
    public const int MaximumPasswordLength = 16;

    public static bool SupportsPassword(int group) =>
        group is (int)PersonalAssetContainerGroup.SecureCargo or (int)PersonalAssetContainerGroup.AuditLogSecure;

    public static void CheckPasswordType(CargoContainerPasswordType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new InvalidDataException("Choose a general or configuration password.");
        }
    }

    public static void CheckPassword(string password)
    {
        if (password.Length is < MinimumPasswordLength or > MaximumPasswordLength || password.Any(char.IsControl))
        {
            throw new InvalidDataException("Container passwords must contain 3 to 16 characters without control characters.");
        }
    }

    public static string Name(string name, string fallback)
    {
        if (name.Length > MaximumNameLength || name.Any(char.IsControl))
        {
            throw new InvalidDataException("Choose a valid container name of at most 100 characters.");
        }
        return string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
    }

    public static void CheckConfiguration(CargoContainerConfiguration configuration)
    {
        if ((configuration & ~CargoContainerConfiguration.All) != 0)
        {
            throw new InvalidDataException("Invalid container configuration.");
        }
    }
}
