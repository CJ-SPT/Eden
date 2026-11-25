using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Characters;

public sealed record HomeStation(int Id, int TypeId, int SystemId, bool IsFallback = false);

public enum HomeStationError
{
    Wormhole = 1,
    AlreadyHome = 2,
    RemoteCooldown = 3,
    InvalidCandidate = 5,
    Triglavian = 6,
}

public sealed record HomeStationCandidate(HomeStation Station, bool Current, bool School, IReadOnlyList<HomeStationError> Errors)
{
    public bool Remote => !Current && !School;
}

public sealed class HomeStationRemoteConfirmationException() : Exception("Confirm the remote home station change.");

public sealed class HomeStationRemoteValidationException(IReadOnlyList<HomeStationError> errors)
    : Exception("That corporation office cannot be selected as your home station.")
{
    public IReadOnlyList<HomeStationError> Errors { get; } = errors;
}

public sealed record HomeStationChange(HomeStation Station, long BalanceCents);

public sealed record HomeStationPayment(
    long Id,
    long PilotId,
    int StationId,
    long OperatorId,
    long AmountCents,
    long BalanceCents,
    long Time
);

public sealed record HomeCloneTransfer(DockedPilot Pilot, IReadOnlyList<InventoryChange> Changes);

public interface IPilotHomeStation
{
    HomeStation Read(long pilotId);
    IReadOnlyList<HomeStationCandidate> Candidates(long pilotId);
    long? NextRemoteChange(long pilotId);
    HomeStationChange Set(long pilotId, int stationId, bool allowRemote);
    HomeCloneTransfer SelfDestruct(long pilotId, long expectedShipId);
}

public static class HomeStationRules
{
    // CCP's Rhea clone change established a 100,000 ISK home relocation fee.
    public const long RelocationFeeCents = 100_000 * MarketDefinitions.CentsPerIsk;
    public const long FirstJournalId = 10_000_000_000_000;
    public const int PochvenRegion = 10000070;
    public const int FirstWormholeSystem = 31000000;
    public const int LastWormholeSystem = 31999999;

    // Build 3662168 homestation.const uses timedelta(days=365).
    public const long RemoteChangeCooldownTicks = 365 * TimeSpan.TicksPerDay;

    public static IReadOnlyList<HomeStationError> Errors(int stationId, int homeId, int systemId, int regionId, bool remoteCooldown = false)
    {
        List<HomeStationError> errors = [];
        if (stationId == homeId)
        {
            errors.Add(HomeStationError.AlreadyHome);
        }
        if (systemId is >= FirstWormholeSystem and <= LastWormholeSystem)
        {
            errors.Add(HomeStationError.Wormhole);
        }
        if (regionId == PochvenRegion)
        {
            errors.Add(HomeStationError.Triglavian);
        }
        if (remoteCooldown)
        {
            errors.Add(HomeStationError.RemoteCooldown);
        }
        return errors;
    }

    public static long Pay(long balance)
    {
        if (balance < RelocationFeeCents)
        {
            throw new InvalidDataException("You need 100,000 ISK to change your home station.");
        }
        return balance - RelocationFeeCents;
    }

    public static void CheckSelfDestruct(bool docked, bool capsule, bool atHome)
    {
        if (!docked || !capsule || atHome)
        {
            throw new InvalidDataException("Self-destruct your clone only while docked in a capsule away from your home station.");
        }
    }
}

public sealed record NpcCorporationInfo(
    int Id,
    string Name,
    string Ticker,
    int? StationId,
    int? SystemId,
    long? FactionId,
    long? CeoId,
    double TaxRate,
    long Shares,
    long MemberLimit,
    string Url
);

public interface INpcCorporationDirectory
{
    NpcCorporationInfo Read(int id);
    IReadOnlyList<NpcCorporationInfo> Read(IReadOnlyList<int> ids);
}
