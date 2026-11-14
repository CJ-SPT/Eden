using System.Globalization;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Loot;

[Injectable]
internal sealed class JettisonHandler(ICargoJettison cargo, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("ship", "Jettison"))
        {
            return PilotCallResult.Unsupported;
        }
        if (
            args is not [List<object?> items]
            || items.Count is 0 or > JetcanRules.MaximumItemsPerCall
            || items.Any(i => i is not long or <= 0L)
            || keywords is { Count: > 0 }
        )
        {
            return new PilotCallResult.Failure("Select cargo items to jettison.");
        }
        try
        {
            if (context.Flight.CanTarget)
            {
                context.Flight.Checkpoint();
            }
            if (!context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Jettison cargo from your active ship in space outside warp.");
            }
            var result = cargo.Jettison(
                context.Pilot.PilotId,
                context.Pilot.ShipId,
                items.Cast<long>().ToArray(),
                clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()
            );
            if (result.RetryAfter > 0)
            {
                return new PilotCallResult.NamedFailure("ShpJettisonPending", new JettisonCooldownValues(result.RetryAfter));
            }
            if (result.Changes.Count > 0)
            {
                foreach (var change in result.Changes)
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                if (result.Contact is { } contact)
                {
                    context.Flight.AddWreck(contact);
                }
            }
            return new JettisonReply(result.Changes.Select(c => c.Item.Id).ToArray());
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record JettisonReply(IReadOnlyList<long> ItemIds) : BlueModel
{
    // The client computes rejected items by subtracting both success lists from
    // its selection. The second list is reserved for items launched as themselves.
    public override object ToBlue() => new object?[] { new BlueList<long>(ItemIds), new BlueList<long>([]) };
}

internal sealed record JettisonCooldownValues(long RemainingTicks) : BlueModel
{
    public override object ToBlue()
    {
        int seconds = (int)Math.Ceiling((double)RemainingTicks / TimeSpan.TicksPerSecond);
        string eta = TimeSpan.FromSeconds(seconds).ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return new Dictionary<string, object?> { ["eta"] = eta };
    }
}
