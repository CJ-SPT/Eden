using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class HomeStationHandler(IPilotHomeStation homes, DeferredRefitPrimes refits, PilotBrain brain, ImplantHandler implants)
    : ISelectedPilotCallHandler
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
            (service != "charMgr" || method != "GetHomeStationRow")
            && (
                service != "home_station"
                || method
                    is not (
                        "get_home_station"
                        or "get_home_station_candidates"
                        or "get_next_remote_change_time"
                        or "set_home_station"
                        or "self_destruct_clone"
                    )
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected home station arguments.");
            }
            long pilot = context.Pilot.PilotId;
            switch (method, args)
            {
                case ("GetHomeStationRow", []):
                    return new HomeStationRowReply(homes.Read(pilot));
                case ("get_home_station", []):
                    var home = homes.Read(pilot);
                    return new HomeStationInfo(home.Id, home.TypeId, home.SystemId, home.IsFallback);
                case ("get_home_station_candidates", []):
                    return new BlueList<HomeStationCandidateReply>(
                        homes.Candidates(pilot).Select(candidate => new HomeStationCandidateReply(candidate)).ToArray()
                    );
                case ("get_next_remote_change_time", []):
                    return homes.NextRemoteChange(pilot) is long time ? new HomeStationRemoteDeadline(time) : PilotCallResult.None;
                case ("set_home_station", [long id, var remote]) when id is > 0 and <= int.MaxValue:
                    bool allow = remote switch
                    {
                        bool flag => flag,
                        0L => false,
                        1L => true,
                        _ => throw new InvalidDataException("Choose a valid remote change confirmation."),
                    };
                    var changed = homes.Set(pilot, (int)id, allow);
                    context.Notifications.Add(new HomeStationChanged(changed.Station.Id));
                    context.Notifications.Add(
                        new AccountBalanceChanged(pilot, (double)changed.BalanceCents / MarketDefinitions.CentsPerIsk)
                    );
                    return PilotCallResult.None;
                case ("self_destruct_clone", []):
                    var transfer = homes.SelfDestruct(pilot, context.Pilot.ShipId);
                    refits.Clear();
                    context.ChangeShip(transfer.Pilot);
                    implants.Refresh(context);
                    foreach (var change in transfer.Changes)
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    context.Notifications.Add(new ServerBrainUpdated(brain.Update(pilot, transfer.Pilot.ShipId)));
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid home station request.");
            }
        }
        catch (HomeStationRemoteConfirmationException)
        {
            return new PilotCallResult.NativeFailure(new HomeStationRemoteConfirmation());
        }
        catch (HomeStationRemoteValidationException error)
        {
            return new PilotCallResult.NativeFailure(new HomeStationRemoteValidation(error.Errors));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record HomeStationRemoteDeadline(long Time) : BlueModel
{
    public override object ToBlue()
    {
        // The pinned selection page subtracts utcnow() from a naive Python datetime.
        var time = DateTime.FromFileTimeUtc(Time);
        int microsecond = (int)(time.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond);
        byte[] state =
        [
            (byte)(time.Year >> 8),
            (byte)(time.Year & 0xff),
            (byte)time.Month,
            (byte)time.Day,
            (byte)time.Hour,
            (byte)time.Minute,
            (byte)time.Second,
            (byte)(microsecond >> 16),
            (byte)((microsecond >> 8) & 0xff),
            (byte)(microsecond & 0xff),
        ];
        return new BlueConstructor("datetime.datetime", [state]);
    }
}

internal sealed record HomeStationRemoteConfirmation : BlueModel
{
    public override object ToBlue() => new BlueConstructor("homestation.error.RemoteChangeNotExpectedError", []);
}

internal sealed record HomeStationRemoteValidation(IReadOnlyList<HomeStationError> Errors) : BlueModel
{
    internal static List<object?> ErrorCodes(IReadOnlyList<HomeStationError> errors) =>
        errors.Select(error => (object?)(long)error).ToList();

    public override object ToBlue() => new BlueConstructor("homestation.validation.ChangeHomeStationValidationError", [ErrorCodes(Errors)]);
}

internal sealed record HomeStationRowReply(HomeStation Station) : BlueModel
{
    // Build 3662168 charactersheet.GetHomeStationRow passes this attribute-bearing
    // reply to map/home-marker consumers, which use camel-case legacy field names.
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["stationID"] = Station.Id,
                ["stationTypeID"] = Station.TypeId,
                ["solarSystemID"] = Station.SystemId,
            }
        );
}

internal sealed record HomeStationCandidateReply(HomeStationCandidate Candidate) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["id"] = Candidate.Station.Id,
                ["type_id"] = Candidate.Station.TypeId,
                ["solar_system_id"] = Candidate.Station.SystemId,
                ["is_current_station"] = Candidate.Current,
                ["is_school_hq"] = Candidate.School,
                ["errors"] = HomeStationRemoteValidation.ErrorCodes(Candidate.Errors),
            }
        );
}

internal sealed record HomeStationChanged(int Id) : PilotNotification("OnHomeStationChanged")
{
    public override object?[] Arguments => [Id];
}

[Injectable]
internal sealed class NpcCorporationHandler(
    INpcCorporationDirectory corporations,
    ICharacterDirectory characters,
    IPlayerCorporations players,
    ICorporationWars wars,
    CorporationHandler monitor,
    CorporationWarMonitor warMonitor
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
            (service, method)
            is not (("corpRegistry", "GetCorporation") or ("corpmgr", "GetCorporations" or "GetCorporationIDForCharacter"))
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected corporation arguments.");
            }
            if (method == "GetCorporationIDForCharacter")
            {
                return args is [long character] && characters.Corporation(character) is { } corporation
                    ? corporation
                    : new PilotCallResult.Failure("Unknown character.");
            }
            int id = (service, args) switch
            {
                ("corpRegistry", []) => players.Identity(context.Pilot.PilotId).Id,
                ("corpmgr", [long corporation]) when corporation is > 0 and <= int.MaxValue => (int)corporation,
                _ => throw new InvalidDataException("Choose a valid NPC corporation."),
            };
            if (players.Read(id) is { } player)
            {
                monitor.ObserveCorporation(player);
                bool allowsWar = wars.CorporationAllowsWar(player.Id);
                if (service == "corpRegistry")
                {
                    warMonitor.WatchPermit(player.Id, allowsWar);
                }
                return new PlayerCorporationReply(player, allowsWar);
            }
            return new NpcCorporationReply(corporations.Read(id));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record NpcCorporationReply(NpcCorporationInfo Corporation) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor(
            "blue.DBRow",
            [
                PlayerCorporationReply.Descriptor(),
                new object?[]
                {
                    Corporation.Id,
                    new BlueUnicode(Corporation.Name),
                    Corporation.Ticker,
                    Corporation.StationId,
                    Corporation.SystemId,
                    Corporation.FactionId,
                    Corporation.CeoId,
                    Corporation.TaxRate,
                    Corporation.Shares,
                    Corporation.MemberLimit,
                    Corporation.Url,
                    Corporation.CeoId,
                    0,
                    new BlueUnicode(""),
                    0d,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                },
            ]
        );
}
