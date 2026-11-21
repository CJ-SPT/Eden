using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;

namespace Eden.World.Characters;

[Injectable(InjectionType.Singleton)]
internal sealed class ShipCloneOffers
{
    private readonly Lock _gate = new();
    private readonly Dictionary<long, ShipCloneOffer> _offers = [];

    internal bool Pending(long pilotId, long now)
    {
        lock (_gate)
        {
            return _offers.TryGetValue(pilotId, out var offer) && offer.ExpiresAt > now;
        }
    }

    internal void Add(ShipCloneOffer offer, long now)
    {
        lock (_gate)
        {
            foreach (var expired in _offers.Values.Where(value => value.ExpiresAt <= now).ToArray())
            {
                _offers.Remove(expired.PilotId);
            }
            if (_offers.ContainsKey(offer.PilotId) || _offers.Values.Any(current => current.HostId == offer.HostId))
            {
                throw new InvalidDataException("A clone installation offer is already pending.");
            }
            _offers.Add(offer.PilotId, offer);
        }
    }

    internal ShipCloneInstallation Accept(long pilotId, long now, Func<ShipCloneOffer, ShipCloneInstallation> install)
    {
        lock (_gate)
        {
            if (!_offers.TryGetValue(pilotId, out var offer) || offer.ExpiresAt <= now)
            {
                _offers.Remove(pilotId);
                throw new InvalidDataException("The ship clone installation offer expired.");
            }
            var result = install(offer);
            _offers.Remove(pilotId);
            return result;
        }
    }

    internal ShipCloneOffer? Cancel(long pilotId)
    {
        lock (_gate)
        {
            var offer = _offers.GetValueOrDefault(pilotId) ?? _offers.Values.FirstOrDefault(current => current.HostId == pilotId);
            if (offer is not null)
            {
                _offers.Remove(offer.PilotId);
            }
            return offer;
        }
    }
}
