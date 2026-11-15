namespace Eden.Gameplay.Characters;

public sealed record PilotActivity(long? LoggedOnAt, long? LoggedOffAt);

public interface IPilotActivity
{
    void Record(long pilotId, bool online);
}
