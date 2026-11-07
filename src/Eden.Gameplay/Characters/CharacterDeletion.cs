namespace Eden.Gameplay.Characters;

public sealed record CharacterDeletionState(long ReadyAt, long? DeletedAt);

public static class CharacterDeletionRules
{
    // Eden uses the traditional ten-hour biomass cooling-off period.
    public static readonly TimeSpan PreparationDelay = TimeSpan.FromHours(10);

    public static long ReadyAt(long now) => checked(now + PreparationDelay.Ticks);

    public static void RequireReady(CharacterDeletionState? state, long now)
    {
        if (state is null || state.DeletedAt is not null || now < state.ReadyAt)
        {
            throw new InvalidDataException("The character is not ready for biomass.");
        }
    }
}

public interface ICharacterDeletion
{
    CharacterDeletionState? Read(long accountId, long pilotId);
    long Prepare(long accountId, long pilotId);
    void Cancel(long accountId, long pilotId);
    void Delete(long accountId, long pilotId);
}
