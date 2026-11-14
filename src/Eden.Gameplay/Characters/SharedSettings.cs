namespace Eden.Gameplay.Characters;

// Build 3662168 sharedSettings/constants.py defines Items=2 and Broadcast=3.
// Overview uses a separate service; zero is Eden's storage discriminator.
public enum SharedSettingKind
{
    Overview = 0,
    InventoryFilter = 2,
    FleetBroadcast = 3,
}

public sealed record SharedSettingKey(string Hash, long Id, SharedSettingKind Kind);

public interface ISharedSettings
{
    SharedSettingKey Store(long pilotId, SharedSettingKind kind, string payload);
    string? Read(long pilotId, SharedSettingKey key);
}

public static class SharedSettingRules
{
    // Eden storage budget per immutable shared profile.
    public const int MaximumPayloadBytes = 256 * 1024;

    public static void Check(SharedSettingKind kind, string payload)
    {
        if (
            !Enum.IsDefined(kind)
            || string.IsNullOrEmpty(payload)
            || System.Text.Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes
            || payload.Contains('\0')
        )
        {
            throw new InvalidDataException("Invalid shared settings.");
        }
    }
}
