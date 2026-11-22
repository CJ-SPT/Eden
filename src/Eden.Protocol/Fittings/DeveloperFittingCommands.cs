using System.Globalization;
using Eden.Gameplay.Fittings;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class DeveloperFittingCommands(IPilotDeveloperFittings fittings, FittingOperations updates, MagazineWire magazines)
{
    internal PilotCallResult Execute(PilotCallContext context, string[] command)
    {
        long ship = context.Pilot.ShipId;
        if (command[0].Equals("/getshipsetup", StringComparison.OrdinalIgnoreCase))
        {
            if (command is not [_, var subject])
            {
                throw new InvalidDataException("Use /getshipsetup me or specify a ship ID.");
            }
            return LegacyDna(fittings.Setup(Self(subject) ? ship : Id(subject)));
        }
        if (context.ControlledStructureId.HasValue)
        {
            throw new InvalidDataException("Release structure control before changing your ship's fitting.");
        }
        if (command[0] == "/copyship")
        {
            if (command is not [_, var source, "1"] || Id(source) != ship)
            {
                throw new InvalidDataException("Use /copyship <activeShipID> 1 while docked.");
            }
            var copy = fittings.CopyDocked(context.Pilot.PilotId, ship);
            context.Notifications.Add(new ShipLabelChanged(copy.ShipId, copy.Name));
            foreach (var change in copy.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return copy.ShipId;
        }
        if (context.Pilot.Position is not null)
        {
            InventoryTransfers.CheckRefitFlight(context);
        }
        long pilot = context.Pilot.PilotId;
        var beforeBanks = updates.ReadBanks(context);
        var result = command[0].ToLowerInvariant() switch
        {
            "/fit" when command.Length is >= 3 and <= 5 && Self(command[1]) => fittings.Fit(pilot, ship, Request(command)),
            "/unload" when command is [_, var subject, var item] && Self(subject) => fittings.Unload(pilot, ship, item),
            "/online" when command is [_, var subject] && (Self(subject) || Id(subject) == ship) => fittings.Online(pilot, ship),
            _ => throw new InvalidDataException("Use /fit me <type> [quantity] [flag=N], /unload me <item|type|all>, or /online me."),
        };
        magazines.NotifyChanges(context, result.Magazines.Where(change => change.Current is null).ToArray());
        foreach (var change in result.Items)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        updates.NotifyOnlineChanges(context, result.Items);
        magazines.NotifyChanges(context, result.Magazines.Where(change => change.Current is not null).ToArray(), result.Reload);
        updates.NotifyShipAttributes(context, beforeBanks);
        return PilotCallResult.None;
    }

    private static DeveloperFitRequest Request(string[] command)
    {
        int quantity = 1;
        int? flag = null;
        int index = 3;
        if (index < command.Length && int.TryParse(command[index], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
        {
            quantity = count;
            index++;
        }
        if (index < command.Length)
        {
            string token = command[index++];
            if (
                !token.StartsWith("flag=", StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(token.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int slot)
            )
            {
                throw new InvalidDataException("Use flag=N to select a fitting slot.");
            }
            flag = slot;
        }
        if (index != command.Length || quantity <= 0)
        {
            throw new InvalidDataException("Supply a positive fitting quantity and at most one flag.");
        }
        return new(command[2], quantity, flag);
    }

    private static long Id(string token) =>
        long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
            ? id
            : throw new InvalidDataException("Supply a valid ship ID.");

    private static bool Self(string token) => token.Equals("me", StringComparison.OrdinalIgnoreCase);

    // Build 3662168 dna.py expects numeric rack markers for interior holes in legacy DNA.
    private static string LegacyDna(DeveloperShipSetup ship)
    {
        (InventoryFlag First, InventoryFlag Last)[] racks =
        [
            (InventoryFlag.HighSlot0, InventoryFlag.HighSlot7),
            (InventoryFlag.MediumSlot0, InventoryFlag.MediumSlot7),
            (InventoryFlag.LowSlot0, InventoryFlag.LowSlot7),
            (InventoryFlag.RigSlot0, InventoryFlag.RigSlot2),
        ];
        List<int> fields = [ship.ShipTypeId];
        for (int rack = 0; rack < racks.Length; rack++)
        {
            var (first, last) = racks[rack];
            for (int slot = (int)first; slot <= (int)last; slot++)
            {
                if (ship.SlotTypes.TryGetValue(slot, out int type))
                {
                    fields.Add(type);
                }
                else if (ship.SlotTypes.Keys.Any(flag => flag > slot && flag <= (int)last))
                {
                    fields.Add(rack);
                }
            }
        }
        return "DNA:" + string.Join(":", fields.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }
}
