using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Ammunition;

[Injectable]
internal sealed class AmmunitionHandler(TimeProvider clock, IPilotAmmunition ammunition, MagazineWire magazines) : ISelectedPilotCallHandler
{
    private static long[]? Ids(object? value) =>
        value switch
        {
            long id => [id],
            List<object?> ids when ids.All(i => i is long) => ids.Cast<long>().ToArray(),
            _ => null,
        };

    private static AmmunitionDestination? Destination(object? value, DockedPilot pilot) =>
        value switch
        {
            object?[] { Length: 3 } parts when parts is [long location, long owner, long flag] && flag is >= 0 and <= int.MaxValue => new(
                location,
                owner,
                (int)flag
            ),
            long location => new(location, pilot.PilotId, location == pilot.ShipId ? (int)InventoryFlag.Cargo : (int)InventoryFlag.Hangar),
            _ => null,
        };

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM" || method is not ("LoadAmmo" or "UnloadAmmo"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            AmmunitionTransfer transfer;
            if (
                method == "LoadAmmo"
                && args is [long ship, var modules, var stacks, long source]
                && Ids(modules) is { } moduleIds
                && Ids(stacks) is { } chargeIds
            )
            {
                transfer = ammunition.Load(context.Pilot.PilotId, ship, moduleIds, chargeIds, source, now);
            }
            else if (
                method == "UnloadAmmo"
                && args.Length is 3 or 4
                && args[0] is long hull
                && Ids(args[1]) is { } weapons
                && Destination(args[2], context.Pilot) is { } destination
                && destination.Owner == context.Pilot.PilotId
                && (args.Length == 3 || args[3] is null || args[3] is long requested && requested is > 0 and <= int.MaxValue)
            )
            {
                int? quantity = args.ElementAtOrDefault(3) is long count ? (int)count : null;
                transfer = ammunition.Unload(context.Pilot.PilotId, hull, weapons, destination.Location, destination.Flag, quantity, now);
            }
            else
            {
                return PilotCallResult.Unsupported;
            }
            magazines.NotifyChanges(context, transfer.Magazines, transfer.ReloadMilliseconds);
            foreach (var change in transfer.Items)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The ammunition quantity or reload time exceeds the local limit.");
        }
    }
}
