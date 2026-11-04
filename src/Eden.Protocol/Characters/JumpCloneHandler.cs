using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class JumpCloneHandler(IPilotJumpClones clones, DeferredRefitPrimes refits, PilotBrain brain) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "jumpCloneSvc"
            || method
                is not (
                    "GetCloneState"
                    or "GetPriceForClone"
                    or "ValidateInstallJumpClone"
                    or "InstallCloneInStation"
                    or "SetJumpCloneName"
                    or "DestroyInstalledClone"
                    or "CloneJump"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected jump clone arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetCloneState", []):
                    return new JumpCloneState(clones.Read(pilot));
                case ("GetPriceForClone", []):
                    return (double)clones.Price(pilot) / MarketDefinitions.CentsPerIsk;
                case ("ValidateInstallJumpClone", []):
                    return new JumpCloneInstallValidation(clones.InstallLimit(pilot), clones.Read(pilot).Clones.Count);
                case ("InstallCloneInStation", []):
                    var installed = clones.Install(pilot);
                    Changed(context, installed.StationId);
                    Balance(context, installed.BalanceCents);
                    return PilotCallResult.None;
                case ("SetJumpCloneName", [long id, string or byte[]]):
                    clones.Rename(pilot, id, BlueMarshal.Text(args[1]));
                    context.Notifications.Add(new JumpCloneCacheInvalidated());
                    return PilotCallResult.None;
                case ("DestroyInstalledClone", [long id]):
                    Changed(context, clones.Destroy(pilot, id));
                    return PilotCallResult.None;
                case ("CloneJump", [long station, long id, var price, var confirmed]) when station is > 0 and <= int.MaxValue:
                    _ = confirmed switch
                    {
                        bool flag => flag,
                        0L => false,
                        1L => true,
                        _ => throw new InvalidDataException("Invalid clone jump confirmation."),
                    };
                    double quote = price switch
                    {
                        double value => value,
                        long value => value,
                        _ => throw new InvalidDataException("Invalid clone jump quote."),
                    };
                    if (quote != (double)JumpCloneRules.FeeCents / MarketDefinitions.CentsPerIsk)
                    {
                        throw new InvalidDataException("Request the current jump clone fee before jumping.");
                    }
                    var transfer = clones.Jump(pilot, context.Pilot.ShipId, (int)station, id, JumpCloneRules.FeeCents);
                    refits.Clear();
                    context.ChangeShip(transfer.Pilot);
                    foreach (var change in transfer.Changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    context.Notifications.Add(new ServerBrainUpdated(brain.Update(pilot, transfer.Pilot.ShipId)));
                    Changed(context, transfer.SourceStationId);
                    Balance(context, transfer.BalanceCents);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid jump clone request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static void Balance(PilotCallContext context, long cents) =>
        context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, (double)cents / MarketDefinitions.CentsPerIsk));

    private static void Changed(PilotCallContext context, int station)
    {
        context.Notifications.Add(new JumpCloneCacheInvalidated());
        context.Notifications.Add(new StationJumpCloneCacheInvalidated(station, context.Pilot.PilotId));
    }
}

internal sealed record JumpCloneState(JumpCloneSnapshot Snapshot) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["clones"] = new CompatibilityRowset(
                    ["jumpCloneID", "locationID", "cloneName"],
                    Snapshot.Clones.Select(clone => new JumpCloneRow(clone)).ToArray()
                ),
                // Implant installation is not implemented; all currently creatable bodies are empty.
                ["implants"] = new CompatibilityRowset(["jumpCloneID", "typeID"], []),
                ["timeLastJump"] = Snapshot.LastJump,
            }
        );
}

internal sealed record JumpCloneRow(JumpClone Body) : BlueModel
{
    public override object ToBlue() => new object?[] { Body.Id, Body.StationId, new BlueUnicode(Body.Name) };
}

internal sealed record JumpCloneInstallValidation(int? Limit, int Count) : BlueModel
{
    public override object ToBlue() =>
        Limit switch
        {
            null => new List<object?>(),
            0 => new List<object?> { "UI/Messages/2182/JumpCharStoringMaxClonesNoneBody" },
            _ => new List<object?>
            {
                new object?[]
                {
                    "UI/Messages/1016/JumpCharStoringMaxClonesBody",
                    new Dictionary<string, object?> { ["have"] = Count, ["max"] = Limit.Value },
                },
            },
        };
}

internal sealed record JumpCloneCacheInvalidated() : PilotNotification("OnJumpCloneCacheInvalidated")
{
    public override object?[] Arguments => [];
}

internal sealed record StationJumpCloneCacheInvalidated(int StationId, long PilotId)
    : PilotNotification("OnStationJumpCloneCacheInvalidated")
{
    public override object?[] Arguments => [StationId, PilotId];
}
