using Eden.Gameplay.Exploration;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private IncursionAgencyUpdate? _incursionAgency;

    internal IncursionAgencyUpdate? IncursionAgency => Volatile.Read(ref _incursionAgency);

    internal void SetIncursionAgency(IncursionAgencyUpdate update)
    {
        // Both the shared resident read and the post-commit callback use the world owner.
        if (
            IncursionAgency is { } previous
            && (
                previous.TaleId > update.TaleId
                || previous.TaleId == update.TaleId && (previous.LastUpdated > update.LastUpdated || !previous.IsActive && update.IsActive)
            )
        )
        {
            return;
        }
        Volatile.Write(ref _incursionAgency, update);
    }
}
