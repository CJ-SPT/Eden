using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Loot;

[Injectable]
internal sealed class LootRightsHandler(ISpaceLootRights rights, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("beyonce", "CmdAbandonLoot"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                keywords is { Count: > 0 }
                || args is not [List<object?> items]
                || items.Any(item => item is not long)
                || !context.Flight.CanTarget
            )
            {
                throw new InvalidDataException("Choose your wrecks or cargo containers while in space.");
            }
            context.Flight.Checkpoint();
            rights.Abandon(context.Pilot.PilotId, items.Cast<long>().ToArray(), clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            context.Flight.RefreshWrecks();
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
