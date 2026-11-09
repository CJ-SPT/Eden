using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Capsuleers;

public enum HaulerActivity
{
    Docked,
    Flying,
    Jumping,
    Waiting,
    Inbound,
}

public sealed record HaulerProgress(
    HaulerActivity Activity,
    int Stamp,
    int DueStamp,
    long ShipId,
    int DestinationStationId,
    long TargetId,
    NavigationFrame Navigation,
    string? WaitingReason = null,
    int ArrivalUntil = 0,
    long ActiveTicks = 0,
    long DueTicks = 0,
    long ClockOffsetTicks = 0,
    int RouteIndex = 0,
    int RouteDirection = 1,
    bool Retreating = false,
    WatchedTrafficGate? TrafficLane = null,
    bool LaneDeparting = false,
    bool InboundPaused = false,
    long? ArrivalGateId = null,
    long? ArrivalAt = null,
    long? DepartureAt = null
);

public sealed record HaulerObservation(DockedPilot Pilot, HaulerProgress Work, ShipHealth Health);

public interface ICapsuleerHauling
{
    void ObserveAggression(long shipId);
}

public sealed record HaulerRouteLeg(int Index, int Direction, int? NextSystemId);

public static class HaulingRouteRules
{
    public static HaulerRouteLeg Leg(IReadOnlyList<int> systems, int systemId, int index, int direction, bool roundTrip)
    {
        if (systems.Count == 0)
        {
            throw new InvalidDataException("The hauling route has no systems.");
        }
        if (index < 0 || index >= systems.Count || systems[index] != systemId)
        {
            index = -1;
            for (int candidate = 0; candidate < systems.Count; candidate++)
            {
                if (systems[candidate] == systemId)
                {
                    index = candidate;
                    break;
                }
            }
            if (index < 0)
            {
                throw new InvalidDataException("The ship is outside its saved hauling route.");
            }
        }
        direction = direction < 0 ? -1 : 1;
        if (roundTrip && systems.Count > 1 && index == systems.Count - 1 && direction > 0)
        {
            direction = -1;
        }
        int next = index + direction;
        return new(index, direction, next >= 0 && next < systems.Count ? systems[next] : null);
    }
}

public static class HaulingRules
{
    public const int BadgerType = 648;
    public const long StartingCents = 100000 * MarketDefinitions.CentsPerIsk;
    public const int PollSeconds = 30;
    public const int OrderDays = 30;
    public const decimal DestinationMultiplier = 1.25m;
    public const long MaximumLoadCents = 100000 * MarketDefinitions.CentsPerIsk;

    public static int InboundDwellSeconds(int gates, int stations, long pilotId)
    {
        const int busyMinimumSeconds = 35;
        const int mediumMinimumSeconds = 65;
        const int quietMinimumSeconds = 95;
        const int cadenceSpreadSeconds = 41;
        int minimum =
            gates >= 4 || stations >= 3 ? busyMinimumSeconds
            : gates >= 2 ? mediumMinimumSeconds
            : quietMinimumSeconds;
        return minimum + (int)((ulong)pilotId % cadenceSpreadSeconds);
    }

    // This is the founder's quote policy, using the catalog reference price per ore portion.
    public static long OrePrice(double referencePrice, int portion) =>
        referencePrice > 0 && double.IsFinite(referencePrice) && portion > 0
            ? Math.Max(1, PersonalMarketRules.Round((decimal)referencePrice * MarketDefinitions.CentsPerIsk / portion))
            : 0;

    public static int PurchaseQuantity(int available, long price, long balance, double capacity, double volume)
    {
        if (available <= 0 || price <= 0 || volume <= 0 || !double.IsFinite(volume) || capacity <= 0 || !double.IsFinite(capacity))
        {
            return 0;
        }
        // Retain a conservative 3% brokerage reserve to list the delivered load.
        long spending = Math.Min(MaximumLoadCents, Math.Max(0, balance - PersonalMarketRules.MinimumBrokerFeeCents));
        long affordable = (long)Math.Floor(spending / (price * (1 + 0.03m * DestinationMultiplier)));
        return (int)Math.Min(available, Math.Min(affordable, Math.Floor(capacity / volume)));
    }
}
