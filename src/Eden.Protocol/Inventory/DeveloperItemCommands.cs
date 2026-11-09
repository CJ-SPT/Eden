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
            PilotRole allowed = cargo ? PilotRole.GameMasterLow | PilotRole.GameMasterHigh : PilotRole.WorldMod;
            if ((context.Roles & (long)allowed) == 0)
            {
                throw new InvalidDataException("Item creation requires the corresponding world-modifier or game-master role.");
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
                new(typeId, quantity, cargo ? ItemSpawnDestination.ShipCargo : ItemSpawnDestination.StationHangar)
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
