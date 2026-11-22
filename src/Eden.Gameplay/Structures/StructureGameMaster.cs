namespace Eden.Gameplay.Structures;

public abstract record StructureGameMasterCommand
{
    private StructureGameMasterCommand() { }

    public sealed record SetState(StructureState State) : StructureGameMasterCommand;

    public sealed record SetSkyhookState(Sovereignty.SkyhookState State) : StructureGameMasterCommand;

    public sealed record SetTimer(TimeSpan Remaining) : StructureGameMasterCommand;

    public sealed record SetDeployTimer(TimeSpan Remaining) : StructureGameMasterCommand;

    public sealed record StartUnanchoring : StructureGameMasterCommand;

    public sealed record CancelUnanchoring : StructureGameMasterCommand;

    public sealed record SetUnanchoringTimer(TimeSpan Remaining) : StructureGameMasterCommand;

    public sealed record SetAbandonTimer(TimeSpan Remaining) : StructureGameMasterCommand;
}

public interface IStructureGameMaster
{
    void Execute(long structureId, StructureGameMasterCommand command);
}
