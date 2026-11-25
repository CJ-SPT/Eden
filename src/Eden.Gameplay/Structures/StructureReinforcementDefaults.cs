namespace Eden.Gameplay.Structures;

public interface IStructureReinforcementDefaults
{
    int ReadHour(long pilotId);
    void SetHour(long pilotId, int hour);
}
