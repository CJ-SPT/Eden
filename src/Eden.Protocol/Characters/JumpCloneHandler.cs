using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class JumpCloneHandler(
    IPilotJumpClones clones,
    DeferredRefitPrimes refits,
    PilotBrain brain,
    ImplantHandler implants,
    IPilotShipClones shipClones,
    ShipCloneSignal signal
) : ISelectedPilotCallHandler
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
                    or "GetStationCloneState"
                    or "GetPriceForClone"
                    or "ValidateInstallJumpClone"
                    or "InstallCloneInStation"
                    or "SetJumpCloneName"
                    or "DestroyInstalledClone"
                    or "CloneJump"
                    or "ResetLastCloneJumpTime"
                    or "GetShipCloneState"
                    or "OfferShipCloneInstallation"
                    or "AcceptShipCloneInstallation"
                    or "CancelShipCloneInstallation"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (method == "ResetLastCloneJumpTime" && (context.Roles & (long)PilotRole.QualityAssurance) == 0)
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
                case ("GetStationCloneState", []):
                    return new JumpCloneRows(clones.ReadStationClones(pilot));
                case ("GetPriceForClone", []):
                    long cents = context.Pilot.Position is null ? clones.Price(pilot) : shipClones.OfferPrice(pilot);
                    return (double)cents / MarketDefinitions.CentsPerIsk;
                case ("ValidateInstallJumpClone", []):
                    return new JumpCloneInstallValidation(clones.InstallLimit(pilot), clones.Read(pilot).Clones.Count);
                case ("InstallCloneInStation", []):
                    var installed = clones.Install(pilot);
                    Changed(context, installed.StationId);
                    Balance(context, installed.BalanceCents);
                    return PilotCallResult.None;
                case ("SetJumpCloneName", [long id, string or byte[]]):
                    if (clones.Rename(pilot, id, BlueMarshal.Text(args[1])) is int station)
                    {
                        Changed(context, station);
                    }
                    else
                    {
                        context.Notifications.Add(new JumpCloneCacheInvalidated());
                    }
                    return PilotCallResult.None;
                case ("DestroyInstalledClone", [long id]):
                    var removed = clones.Destroy(pilot, id);
                    if (removed.VatHostId is long host)
                    {
                        signal.Send(removed.PilotId, new JumpCloneCacheInvalidated());
                        signal.Send(host, new ShipCloneCacheInvalidated(removed.LocationId, removed.PilotId));
                    }
                    else
                    {
                        Changed(context, (int)removed.LocationId);
                    }
                    return PilotCallResult.None;
                case ("ResetLastCloneJumpTime", []):
                    clones.ResetCooldown(pilot);
                    context.Notifications.Add(new JumpCloneCacheInvalidated());
                    return PilotCallResult.None;
                case ("GetShipCloneState", []):
                    return new CompatibilityRowset(
                        ["jumpCloneID", "ownerID", "locationID"],
                        shipClones.ShipClones(pilot, context.Pilot.ShipId).Select(clone => new ShipCloneRow(clone)).ToArray()
                    );
                case ("OfferShipCloneInstallation", [long target]):
                    var offer = shipClones.Offer(pilot, context.Pilot.ShipId, target);
                    signal.Send(target, new ShipCloneOfferChanged("OnShipJumpCloneInstallationOffered", offer));
                    return PilotCallResult.None;
                case ("AcceptShipCloneInstallation", []):
                    var installedInShip = shipClones.Accept(pilot, context.Pilot.ShipId);
                    NotifyOffer(installedInShip.Offer, "OnShipJumpCloneInstallationDone");
                    signal.Send(installedInShip.Offer.HostId, new ShipCloneCacheInvalidated(installedInShip.Offer.ShipId, pilot));
                    context.Notifications.Add(new JumpCloneCacheInvalidated());
                    Balance(context, installedInShip.BalanceCents);
                    return PilotCallResult.None;
                case ("CancelShipCloneInstallation", []):
                    if (shipClones.Cancel(pilot) is { } canceled)
                    {
                        NotifyOffer(canceled, "OnShipJumpCloneInstallationCanceled");
                    }
                    return PilotCallResult.None;
                case ("CloneJump", [long location, long id, var price, var confirmed]) when location > 0:
                    bool accepted = confirmed switch
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
                    bool mobile = clones.Read(pilot).Clones.Any(body => body.Id == id && body.ShipId == location);
                    if (mobile && !accepted)
                    {
                        return new PilotCallResult.NamedFailure("JumpCheckIntoShip", new ShipCloneConfirmation(location));
                    }
                    JumpCloneTransfer transfer;
                    if (mobile)
                    {
                        var arrival = shipClones.JumpToShip(pilot, context.Pilot.ShipId, location, id, JumpCloneRules.FeeCents);
                        transfer = arrival.Transfer;
                        signal.Send(arrival.HostId, new ShipCloneCacheInvalidated(location, pilot));
                    }
                    else
                    {
                        transfer = clones.Jump(pilot, context.Pilot.ShipId, checked((int)location), id, JumpCloneRules.FeeCents);
                    }
                    refits.Clear();
                    context.ChangeShip(transfer.Pilot);
                    if (mobile)
                    {
                        context.Flight.Snapshot();
                    }
                    implants.Refresh(context);
                    foreach (var change in transfer.Changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    context.Notifications.Add(new ServerBrainUpdated(brain.Update(pilot, transfer.Pilot.ShipId)));
                    Changed(context, transfer.SourceStationId);
                    if (!mobile && transfer.Pilot.StationId != transfer.SourceStationId)
                    {
                        context.Notifications.Add(new StationJumpCloneCacheInvalidated(transfer.Pilot.StationId, pilot));
                    }
                    Balance(context, transfer.BalanceCents);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid jump clone request.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private void NotifyOffer(ShipCloneOffer offer, string name)
    {
        var notice = new ShipCloneOfferChanged(name, offer);
        signal.Send(offer.HostId, notice);
        signal.Send(offer.PilotId, notice);
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
                ["clones"] = new JumpCloneRows(Snapshot.Clones),
                ["implants"] = new CompatibilityRowset(
                    ["jumpCloneID", "typeID"],
                    Snapshot.Implants.Select(i => new JumpCloneImplantRow(i)).ToArray()
                ),
                ["timeLastJump"] = Snapshot.LastJump,
            }
        );
}

internal sealed record JumpCloneRows(IReadOnlyList<JumpClone> Clones) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["jumpCloneID", "locationID", "cloneName"],
            Clones.Select(clone => new JumpCloneRow(clone)).ToArray()
        ).ToBlue();
}

internal sealed record JumpCloneImplantRow(InstalledImplant Implant) : BlueModel
{
    public override object ToBlue() => new object?[] { Implant.CloneId, Implant.Profile.TypeId };
}

internal sealed record JumpCloneRow(JumpClone Body) : BlueModel
{
    public override object ToBlue() =>
        new object?[] { Body.Id, Body.ShipId is long ship ? ship : (object)Body.StationId, new BlueUnicode(Body.Name) };
}

internal sealed record ShipCloneRow(ShipClone Body) : BlueModel
{
    public override object ToBlue() => new object?[] { Body.Id, Body.PilotId, Body.ShipId };
}

internal sealed record ShipCloneConfirmation(long ShipId) : BlueModel
{
    public override object ToBlue() => new Dictionary<string, object?> { ["shipID"] = ShipId };
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
