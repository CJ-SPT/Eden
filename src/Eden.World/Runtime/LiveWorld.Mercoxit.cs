using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private const int MercoxitExpeditionsPerSystem = 2;
    private readonly Dictionary<long, int> _mercoxitDestinations = [];
    private readonly Dictionary<int, HashSet<long>> _mercoxitMiners = [];
    private bool _mercoxitExpeditionsLoaded;

    // Reservations use the same owner and checkpoint as the miner's saved flight.
    internal bool MercoxitDepartureAvailable(long pilot, int destination)
    {
        LoadMercoxitExpeditions();
        return !_mercoxitMiners.TryGetValue(destination, out var miners)
            || miners
                .Where(other => other != pilot && Economy.Peek<SavedCapsuleerSuspension>(other) is null)
                .Take(MercoxitExpeditionsPerSystem)
                .Count() < MercoxitExpeditionsPerSystem;
    }

    internal void WarmMercoxitExpeditions()
    {
        using var owner = EnterShared("restore mercoxit expeditions");
        LoadMercoxitExpeditions();
    }

    private void LoadMercoxitExpeditions()
    {
        if (_mercoxitExpeditionsLoaded)
        {
            return;
        }
        foreach (var saved in Economy.SavedMercoxitExpeditions())
        {
            TrackMercoxitDestination(saved.Pilot, saved.Destination);
        }
        foreach (var miner in ProjectedMiners.Values.Concat(MinerCheckpoints.Values))
        {
            TrackMercoxitDestination(miner.PilotId, MercoxitDestination(miner.Work.Mercoxit));
        }
        _mercoxitExpeditionsLoaded = true;
    }

    internal void PublishMercoxitExpedition(long pilot, MercoxitExpedition? expedition)
    {
        if (_mercoxitExpeditionsLoaded)
        {
            TrackMercoxitDestination(pilot, MercoxitDestination(expedition));
        }
    }

    private static int? MercoxitDestination(MercoxitExpedition? expedition) =>
        expedition?.Systems is [.., int destination] ? destination : null;

    private void TrackMercoxitDestination(long pilot, int? destination)
    {
        if (_mercoxitDestinations.TryGetValue(pilot, out int previous))
        {
            if (previous == destination)
            {
                return;
            }
            _mercoxitDestinations.Remove(pilot);
            var previousMiners = _mercoxitMiners[previous];
            if (previousMiners.Remove(pilot) && previousMiners.Count == 0)
            {
                _mercoxitMiners.Remove(previous);
            }
        }
        if (destination is int system)
        {
            _mercoxitDestinations.Add(pilot, system);
            if (!_mercoxitMiners.TryGetValue(system, out var miners))
            {
                miners = [];
                _mercoxitMiners.Add(system, miners);
            }
            miners.Add(pilot);
        }
    }
}
