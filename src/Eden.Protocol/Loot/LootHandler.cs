using System.Globalization;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Loot;

[Injectable]
internal sealed class LootHandler(IWrecks wrecks, ICargoJettison jettison, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool bind = service == "invbroker" && method == "GetInventoryFromId" && args is [long, ..];
        long id;
        if (bind)
        {
            id = (long)args[0]!;
        }
        else if (
            !service.StartsWith(WreckWireDefinitions.BindingPrefix, StringComparison.Ordinal)
            || !long.TryParse(
                service.AsSpan(WreckWireDefinitions.BindingPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out id
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (bind && !JetcanRules.IsJetcan(id) && !context.Flight.VisibleWrecks.Any(w => w.Id == id))
        {
            return PilotCallResult.Unsupported;
        }
        if (!context.Flight.CanTarget)
        {
            return new PilotCallResult.Failure("Approach the container in a ship outside warp before opening it.");
        }
        try
        {
            context.Flight.Checkpoint();
            if (!context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Approach the container in a ship outside warp before opening it.");
            }
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            if (!bind && JetcanRules.IsJetcan(id) && method is "Add" or "MultiAdd")
            {
                var request = DepositRequest(method, args, keywords);
                foreach (var change in jettison.Deposit(context.Pilot.PilotId, id, request, now))
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return PilotCallResult.None;
            }
            var contents = wrecks.Read(context.Pilot.PilotId, id, now);
            if (bind)
            {
                return context.Reference(
                    WreckWireDefinitions.BindingPrefix + id.ToString(CultureInfo.InvariantCulture),
                    PilotBindingLifetime.Location
                );
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                return InventoryRow.From(contents.Container);
            }
            if (method == "GetCapacity" && contents.Capacity is double capacity)
            {
                return new JetcanCapacityReply(capacity, contents.Items.Sum(i => i.Volume * i.Quantity));
            }
            if (method == "List")
            {
                object? flag = args.FirstOrDefault() ?? keywords?.GetValueOrDefault("flag");
                return new BlueList<InventoryRow>(
                    contents.Items.Where(item => flag is null || Equals(flag, (long)item.FlagId)).Select(InventoryRow.From).ToArray()
                );
            }
            return PilotCallResult.Unsupported;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static InventoryMoveRequest DepositRequest(string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args is not [var selection, long source])
        {
            throw new InvalidDataException("Select cargo items and a source hold.");
        }
        long[] ids = (method, selection) switch
        {
            ("Add", long id) => [id],
            ("MultiAdd", List<object?> list) when list.All(i => i is long) => list.Cast<long>().ToArray(),
            _ => throw new InvalidDataException("Invalid cargo container transfer."),
        };
        object? flag = keywords?.GetValueOrDefault("flag");
        object? quantity = keywords?.GetValueOrDefault("qty");
        if (flag is not (null or (long)InventoryFlag.None) || quantity is not (null or long and > 0 and <= int.MaxValue))
        {
            throw new InvalidDataException("Invalid cargo container flag or quantity.");
        }
        return new(ids, source, (int)InventoryFlag.None, quantity is long count ? (int)count : null);
    }
}

internal sealed record JetcanCapacityReply(double Capacity, double Used) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["capacity"] = Capacity, ["used"] = Used });
}
