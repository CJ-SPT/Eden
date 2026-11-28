using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Ammunition;

[Injectable]
internal sealed class AmmunitionHandler(
    TimeProvider clock,
    IPilotAmmunition ammunition,
    MagazineWire magazines,
    IPilotStructureAmmunition? structureAmmunition = null,
    IPilotStructures? structures = null,
    Eden.Protocol.Structures.StructureDogmaBuilder? structureDogma = null
) : ISelectedPilotCallHandler
{
    private static long[]? Ids(object? value) =>
        value switch
        {
            long id => [id],
            HashSet<long> ids => ids.Order().ToArray(),
            List<object?> ids when ids.All(i => i is long) => ids.Cast<long>().ToArray(),
            _ => null,
        };

    private static AmmunitionDestination? Destination(object? value, DockedPilot pilot, long? structure = null) =>
        value switch
        {
            object?[] { Length: 3 } parts when parts is [long location, long owner, long flag] && flag is >= 0 and <= int.MaxValue => new(
                location,
                owner,
                (int)flag
            ),
            long location when location == structure => new(location, pilot.CorpId, (int)InventoryFlag.Cargo),
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
            magazines.Advance(context);
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            AmmunitionTransfer transfer;
            long? structureId = null;
            if (
                method == "LoadAmmo"
                && args is [long ship, var modules, var stacks, long source]
                && Ids(modules) is { } moduleIds
                && Ids(stacks) is { } chargeIds
            )
            {
                if (structureAmmunition?.Handles(ship) == true)
                {
                    structureId = ship;
                    transfer = (structureAmmunition ?? throw new InvalidDataException("Structure ammunition is unavailable.")).Load(
                        context.Pilot.PilotId,
                        ship,
                        moduleIds,
                        chargeIds,
                        source
                    );
                }
                else
                {
                    transfer = ammunition.Load(context.Pilot.PilotId, ship, moduleIds, chargeIds, source, now);
                }
            }
            else if (
                method == "UnloadAmmo"
                && args.Length is 3 or 4
                && args[0] is long hull
                && Ids(args[1]) is { } weapons
                && Destination(args[2], context.Pilot, structureAmmunition?.Handles(hull) == true ? hull : null) is { } destination
                && (destination.Owner == context.Pilot.PilotId || structureAmmunition?.Handles(hull) == true)
                && (args.Length == 3 || args[3] is null || args[3] is long requested && requested is > 0 and <= int.MaxValue)
            )
            {
                int? quantity = args.ElementAtOrDefault(3) is long count ? (int)count : null;
                if (structureAmmunition?.Handles(hull) == true)
                {
                    structureId = hull;
                    transfer = (structureAmmunition ?? throw new InvalidDataException("Structure ammunition is unavailable.")).Unload(
                        context.Pilot.PilotId,
                        hull,
                        weapons,
                        new(destination.Location, destination.Owner, destination.Flag),
                        quantity
                    );
                }
                else
                {
                    transfer = ammunition.Unload(
                        context.Pilot.PilotId,
                        hull,
                        weapons,
                        destination.Location,
                        destination.Flag,
                        quantity,
                        now
                    );
                }
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
            if (structureId is long id && structures?.Read(context.Pilot.PilotId, id) is { } structure)
            {
                structureDogma?.NotifyAttributes(context, structure);
            }
            // FitCharges loads from the first new stack when dragging a charge between fitting slots.
            return method == "UnloadAmmo" && transfer.Items.Count > 0
                ? new BlueList<long>(transfer.Items.Select(change => change.Item.Id).ToArray())
                : PilotCallResult.None;
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
