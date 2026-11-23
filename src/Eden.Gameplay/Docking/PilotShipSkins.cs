using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Docking;

public sealed record ShipSkinViewer(long PilotId, long ShipId, int SystemId, SpacePosition? Position);

public sealed record AppliedShipSkin(long ShipId, long PilotId, int? SkinId, long? Expires);

public sealed record ShipSkinApplication(AppliedShipSkin State, bool Changed);

public interface IPilotShipSkins
{
    ShipSkinViewer? Viewer(long accountId, long pilotId);
    AppliedShipSkin? ReadOwned(long pilotId, long shipId);
    IReadOnlyList<AppliedShipSkin> ReadVisible(IReadOnlyCollection<long> shipIds);
    ShipSkinApplication Apply(long pilotId, long shipId, int? skinId);
}
