using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Characters;

public sealed record ShipClone(long Id, long PilotId, long ShipId);

public sealed record ShipCloneOffer(long HostId, long PilotId, long ShipId, long ExpiresAt);

public sealed record ShipCloneInstallation(ShipCloneOffer Offer, long BalanceCents);

public sealed record ShipCloneJump(JumpCloneTransfer Transfer, long HostId);

public interface IPilotShipClones
{
    IReadOnlyList<ShipClone> ShipClones(long hostId, long shipId);
    ShipCloneOffer Offer(long hostId, long shipId, long pilotId);
    long OfferPrice(long pilotId);
    ShipCloneInstallation Accept(long pilotId, long shipId);
    ShipCloneOffer? Cancel(long pilotId);
    ShipCloneJump JumpToShip(long pilotId, long expectedShipId, long shipId, long cloneId, long quotedCents);
}
