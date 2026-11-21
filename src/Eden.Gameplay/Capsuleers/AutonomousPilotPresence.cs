namespace Eden.Gameplay.Capsuleers;

public sealed record AutonomousPilotContact(PilotSighting Sighting, bool Protected);

// Combat reads canonical autonomous contacts even when no native scene is materialized.
public interface IAutonomousPilotPresence
{
    bool Owns(long shipId);
    AutonomousPilotContact? Read(int systemId, long shipId);
    PilotSighting[] Observe(int systemId, long ownShipId, Eden.Gameplay.Flight.SpacePosition origin, long sample);
}
