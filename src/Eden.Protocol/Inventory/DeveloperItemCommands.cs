using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Redemption;

namespace Eden.Protocol.Inventory;

[Injectable]
internal sealed class DeveloperItemCommands(IPilotItemSpawning spawning)
{
    internal PilotCallResult Create(PilotCallContext context, string[] command)
    {
        try
        {
            bool cargo = command[0].Equals("/load", StringComparison.OrdinalIgnoreCase);
            long? expectedHangar = null;
            if (command[0].Equals("/create", StringComparison.OrdinalIgnoreCase) && command.Length == 4)
            {
                if (!long.TryParse(command[3], NumberStyles.None, CultureInfo.InvariantCulture, out long hangar) || hangar <= 0)
                {
                    throw new InvalidDataException("Choose the current docked location.");
                }
                expectedHangar = hangar;
                command = command[..3];
            }
            int index = cargo ? 2 : 1;
            if (
                (cargo && (command.Length < 2 || !command[1].Equals("me", StringComparison.OrdinalIgnoreCase)))
                || command.Length < index + 1
                || command.Length > index + 2
                || !int.TryParse(command[index], NumberStyles.None, CultureInfo.InvariantCulture, out int typeId)
                || typeId <= 0
            )
            {
                throw new InvalidDataException(
                    "Use /createitem typeID [quantity], /create typeID [quantity], or /load me typeID [quantity]."
                );
            }
            int quantity = 1;
            if (
                command.Length == index + 2
                && (!int.TryParse(command[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out quantity) || quantity <= 0)
            )
            {
                throw new InvalidDataException("The item quantity must be a positive 32-bit integer.");
            }
            var result = spawning.Create(
                context.Pilot.PilotId,
                context.Pilot.ShipId,
                new(typeId, quantity, cargo ? ItemSpawnDestination.ShipCargo : ItemSpawnDestination.StationHangar, expectedHangar)
            );
            if (result is ItemSpawnResult.Redemption reward)
            {
                context.Notifications.Add(new RedeemingQueueUpdated());
                return reward.Token.Id;
            }
            var change = ((ItemSpawnResult.Inventory)result).Change;
            InventoryChangesWire.NotifyItemChange(context, change);
            // Native create/load callers may use the returned item identity.
            return change.Item.Id;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
    }
}
