using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class MultifitCapabilityHandler(IPilotItemSpawning spawning) : ISelectedPilotCallHandler
{
    private const int MaximumBatchLength = 4096;
    private const int MaximumDistinctTypes = 128;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "publicQaToolsServer" || method is not ("CanGiveItemForMultifit" or "SlashCmd"))
        {
            return PilotCallResult.Unsupported;
        }
        bool available =
            (context.Roles & (long)PilotRole.WorldMod) != 0
            && context.Pilot.Position is null
            && !context.ControlledStructureId.HasValue
            && !context.Flight.Translocating;
        if (method == "CanGiveItemForMultifit")
        {
            return args.Length == 0 && keywords is not { Count: > 0 }
                ? (PilotCallResult)available
                : new PilotCallResult.Failure("The multifit item-creation capability check takes no arguments.");
        }
        if (!available)
        {
            return new PilotCallResult.Failure("Fitting item creation requires a docked World Modifier.");
        }
        try
        {
            if (args is not [string or byte[]] || keywords is { Count: > 0 })
            {
                throw Usage();
            }
            var batch = ReadBatch(BlueRead.Text(args[0]));
            var changes = spawning.CreateHangarBatch(context.Pilot.PilotId, context.Pilot.ShipId, batch);
            foreach (var change in changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    // Build 3662168 MultiFitWnd.GiveAllPlayer sends comma-separated typeID|quantity pairs.
    private static HangarSpawnStack[] ReadBatch(string command)
    {
        const string giveItem = "/giveitem ";
        if (command.Length > MaximumBatchLength || !command.StartsWith(giveItem, StringComparison.OrdinalIgnoreCase))
        {
            throw Usage();
        }
        var entries = command[giveItem.Length..].Split(',');
        if (entries.Length > MaximumDistinctTypes)
        {
            throw Usage();
        }
        HashSet<int> seen = [];
        List<HangarSpawnStack> batch = [];
        foreach (string entry in entries)
        {
            int delimiter = entry.IndexOf('|');
            if (
                delimiter <= 0
                || !int.TryParse(entry.AsSpan(0, delimiter), NumberStyles.None, CultureInfo.InvariantCulture, out int type)
                || !int.TryParse(entry.AsSpan(delimiter + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int quantity)
                || type <= 0
                || quantity <= 0
                || !seen.Add(type)
            )
            {
                throw Usage();
            }
            batch.Add(new(type, quantity));
        }
        return batch.ToArray();
    }

    private static InvalidDataException Usage() => new("Use /giveitem typeID|quantity[,typeID|quantity] for missing fitting items.");
}
