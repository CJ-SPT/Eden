using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable(InjectionType.Scoped)]
internal sealed class CargoContainerHandler(ICargoContainers containers) : ISelectedPilotCallHandler
{
    internal const string InventoryPrefix = "@cargoContainer:";
    private readonly ConditionalWeakTable<PilotCallContext, AccessCache> _access = new();

    private sealed class AccessCache
    {
        internal FlightSession? Flight { get; set; }
        internal Dictionary<long, CargoContainerAccess> Passwords { get; } = [];
    }

    private AccessCache Cache(PilotCallContext context)
    {
        var cache = _access.GetOrCreateValue(context);
        if (cache.Flight != context.ActiveFlight)
        {
            cache.Passwords.Clear();
            cache.Flight = context.ActiveFlight;
        }
        return cache;
    }

    internal CargoContainerAccess Access(PilotCallContext context, long item) => Cache(context).Passwords.GetValueOrDefault(item) ?? new();

    private void Authorize(PilotCallContext context, long item, CargoContainerPasswordType type, string password)
    {
        var previous = Access(context, item);
        Cache(context).Passwords[item] =
            type == CargoContainerPasswordType.General ? previous with { General = password } : previous with { Configuration = password };
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "invbroker" && method is "ValidateItemCanBeOpened" or "ValidateItemListCanBeOpened")
        {
            return ValidateOpen(context, method, args, keywords);
        }
        bool manager = service == "invbroker" && method is "GetInventoryFromId" or "GetContainerContents";
        long id;
        if (manager)
        {
            if (args is not [long itemId, ..] || !containers.Handles(itemId))
            {
                return PilotCallResult.Unsupported;
            }
            id = itemId;
        }
        else if (
            !service.StartsWith(InventoryPrefix, StringComparison.Ordinal) || !long.TryParse(service.AsSpan(InventoryPrefix.Length), out id)
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long pilot = context.Pilot.PilotId;
            var access = Access(context, id);
            if (manager)
            {
                if (keywords is { Count: > 0 })
                {
                    throw new InvalidDataException("Unexpected container arguments.");
                }
                if (method == "GetContainerContents" && args is [long, long station])
                {
                    return Rows(containers.Preview(pilot, id, station, access).Items);
                }
                if (method == "GetInventoryFromId" && args.Length is 1 or 2)
                {
                    containers.Read(pilot, id, access, includeContents: false);
                    return context.Reference($"{InventoryPrefix}{id}", PilotBindingLifetime.Location);
                }
                throw new InvalidDataException("Invalid container request.");
            }
            if (method is "Add" or "MultiAdd")
            {
                return Move(context, id, MoveRequest(method, args, keywords), access);
            }
            if (method == "List" && args.Length <= 1)
            {
                object? flag = args.Length == 1 ? args[0] : keywords?.GetValueOrDefault("flag");
                if (flag is not (null or long))
                {
                    throw new InvalidDataException("Invalid container contents flag.");
                }
                return Rows(containers.Read(pilot, id, access).Items.Where(item => flag is null || Equals(flag, (long)item.FlagId)));
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected container arguments.");
            }
            if (method == "GetSelfInvItem" && args.Length == 0)
            {
                return InventoryRow.From(containers.Read(pilot, id, access, includeContents: false).Item);
            }
            if (method == "GetCapacity" && args.Length <= 1 && (args.Length == 0 || args[0] is null or long))
            {
                var contents = containers.Read(pilot, id, access);
                return new CargoContainerCapacity(contents.Capacity, contents.Items.Sum(item => item.Volume * item.Quantity));
            }
            if (method == "ALSCConfigGet" && args.Length == 0)
            {
                return (int)containers.Configuration(pilot, id, access);
            }
            if (method == "ALSCConfigSet" && args is [long configuration] && configuration is >= 0 and <= int.MaxValue)
            {
                containers.Configure(pilot, id, (CargoContainerConfiguration)configuration, access);
                return PilotCallResult.None;
            }
            if (method is "ALSCLockItems" or "ALSCUnlockItems" && args is [List<object?> ids] && ids.All(item => item is long))
            {
                return Notify(context, containers.Lock(pilot, id, ids.Cast<long>().ToArray(), method == "ALSCLockItems", access));
            }
            if (
                args is [long passwordType, ..]
                && passwordType is (long)CargoContainerPasswordType.General or (long)CargoContainerPasswordType.Configuration
            )
            {
                var type = (CargoContainerPasswordType)passwordType;
                if (method == "HasExistingPasswordSet" && args.Length == 1)
                {
                    return containers.HasPassword(pilot, id, type);
                }
                if (method == "RetrievePassword" && args.Length == 1)
                {
                    string? password = containers.RetrievePassword(pilot, id, type);
                    // menuFunctions.RetrievePasswordALSC discards the return. Present the result through the native information dialog.
                    return new PilotCallResult.Failure(password is null ? "No password is set." : $"Container password: {password}", true);
                }
                if (method == "EnterPassword" && args is [long, string or byte[]])
                {
                    string password = BlueMarshal.Text(args[1]);
                    containers.EnterPassword(pilot, id, type, password);
                    Authorize(context, id, type, password);
                    return PilotCallResult.None;
                }
                if (method == "SetPassword" && args is [long, null or string or byte[], string or byte[]])
                {
                    string password = BlueMarshal.Text(args[2]);
                    containers.SetPassword(pilot, id, type, args[1] is null ? null : BlueMarshal.Text(args[1]), password);
                    Authorize(context, id, type, password);
                    return PilotCallResult.None;
                }
            }
            return new PilotCallResult.Failure("Invalid or unavailable container operation.");
        }
        catch (CargoContainerPasswordRequiredException error)
        {
            return PasswordRequired(error);
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

    internal PilotCallResult Move(PilotCallContext context, long destination, InventoryMoveRequest request, CargoContainerAccess access) =>
        Notify(context, containers.Move(context.Pilot.PilotId, destination, request, access));

    internal static PilotCallResult PasswordRequired(CargoContainerPasswordRequiredException error) =>
        new PilotCallResult.NamedFailure(
            error.Type == CargoContainerPasswordType.General ? "SCGeneralPasswordRequired" : "SCConfigPasswordRequired",
            BlueEmpty.Dictionary
        );

    private PilotCallResult ValidateOpen(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected container validation arguments.");
            }
            var rows = (method, args) switch
            {
                ("ValidateItemCanBeOpened", [BluePackedRow row]) => new[] { row },
                ("ValidateItemListCanBeOpened", [List<object?> list]) when list.All(row => row is BluePackedRow) =>
                    list.Cast<BluePackedRow>().ToArray(),
                _ => throw new InvalidDataException("Choose inventory items to open."),
            };
            Span<byte> data = stackalloc byte[InventoryWire.PackedSize];
            foreach (var row in rows)
            {
                if (!InventoryWire.IsInventoryDescriptor(row.Descriptor))
                {
                    throw new InvalidDataException("Invalid container inventory row.");
                }
                row.Unpack(data);
                long id = BinaryPrimitives.ReadInt64LittleEndian(data);
                // Treat every client-supplied ownership, type and location field as untrusted.
                containers.Read(context.Pilot.PilotId, id, Access(context, id), includeContents: false);
            }
            return true;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static PilotCallResult Notify(PilotCallContext context, IReadOnlyList<InventoryChange> changes)
    {
        foreach (var change in changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        return PilotCallResult.None;
    }

    private static BlueList<InventoryRow> Rows(IEnumerable<InventoryItem> items) => new(items.Select(InventoryRow.From).ToArray());

    private static InventoryMoveRequest MoveRequest(string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args is not [var selection, long source] || keywords?.Keys.Any(key => key is not ("flag" or "qty")) == true)
        {
            throw new InvalidDataException("Choose cargo and a source inventory.");
        }
        long[] ids = (method, selection) switch
        {
            ("Add", long id) => [id],
            ("MultiAdd", List<object?> list) when list.All(item => item is long) => list.Cast<long>().ToArray(),
            _ => throw new InvalidDataException("Invalid cargo selection."),
        };
        object? flag = keywords?.GetValueOrDefault("flag");
        object? quantity = keywords?.GetValueOrDefault("qty");
        if (
            flag is not (null or (long)InventoryFlag.None or (long)InventoryFlag.Locked or (long)InventoryFlag.Unlocked)
            || quantity is not (null or long and > 0 and <= int.MaxValue)
        )
        {
            throw new InvalidDataException("Invalid container flag or quantity.");
        }
        return new(ids, source, flag is long value ? (int)value : (int)InventoryFlag.None, quantity is long count ? (int)count : null);
    }
}

internal sealed record CargoContainerCapacity(double Capacity, double Used) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["capacity"] = Capacity, ["used"] = Used });
}
