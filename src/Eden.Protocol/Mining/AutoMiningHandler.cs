using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Mining;

[Injectable(InjectionType.Scoped)]
internal sealed partial class AutoMiningHandler(
    TimeProvider clock,
    IPilotAutoMining store,
    IPilotFitting fitting,
    TargetingHandler targeting,
    MiningHandler mining,
    IAutoMiningAvailability? availability = null,
    IPilotAutoMiningHauling? hauling = null
) : ISelectedPilotCallHandler
{
    internal const string Service = "edenAutoMining";
    private static readonly TimeSpan s_retryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_blockedInterval = TimeSpan.FromSeconds(3);
    private long _pilotId;
    private AutoMiningFilter? _filter;
    private FlightSession? _flight;
    private long _ship;
    private int _system;
    private long _nextDecision;
    private string _status = "Stopped";

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != Service || method is not ("Start" or "Stop" or "GetStatus" or "SaveFilter" or "GetStations" or "SaveHauling"))
        {
            return PilotCallResult.Unsupported;
        }
        if (availability?.AutoMiningEnabled != true)
        {
            return new PilotCallResult.Failure("Activate the AutoMining mod for this world first.");
        }
        try
        {
            bool validArguments = method switch
            {
                "SaveFilter" => args is [string or byte[]],
                "SaveHauling" => args is [long, long and >= int.MinValue and <= int.MaxValue],
                _ => args.Length == 0,
            };
            if (keywords is { Count: > 0 } || !validArguments)
            {
                return new PilotCallResult.Failure("Invalid AutoMining arguments.");
            }
            CheckFlight(context);
            if (_pilotId != context.Pilot.PilotId)
            {
                _pilotId = context.Pilot.PilotId;
                _filter = null;
                _haulSettings = null;
                _status = "Stopped";
            }
            _filter ??= store.ReadFilter(_pilotId);
            _haulSettings ??= hauling?.ReadHauling(_pilotId) ?? AutoMiningHaulSettings.Disabled;
            switch (method)
            {
                case "GetStations":
                    return new AutoMiningStations(hauling?.Stations(context.Pilot.SystemId) ?? []);
                case "SaveHauling":
                    if (hauling is null)
                    {
                        return new PilotCallResult.Failure("AutoMining hauling is unavailable.");
                    }
                    var settings = new AutoMiningHaulSettings((long)args[0]!, checked((int)(long)args[1]!));
                    hauling.SaveHauling(_pilotId, context.Pilot.SystemId, settings);
                    _haulSettings = settings;
                    mining.AutoRepeatThreshold = _flight is not null && settings.Enabled ? settings.Threshold : null;
                    break;
                case "Start":
                    if (_trip is not null)
                    {
                        break;
                    }
                    context.Flight.Checkpoint();
                    CheckFlight(context);
                    if (!CanRun(context.Flight))
                    {
                        return new PilotCallResult.Failure("Start AutoMining in space outside warp, with targeting available.");
                    }
                    if (store.Modules(_pilotId, context.Pilot.ShipId).Count == 0)
                    {
                        return new PilotCallResult.Failure("Fit and online an ore mining laser or strip miner first.");
                    }
                    _flight = context.Flight;
                    _ship = context.Pilot.ShipId;
                    _system = context.Pilot.SystemId;
                    _navigationRevision = _flight.PilotNavigationRevision;
                    _miningOrigin = null;
                    mining.AutoRepeatThreshold = _haulSettings.Enabled ? _haulSettings.Threshold : null;
                    mining.ResetManualStops();
                    targeting.ResetAutoMiningExclusions();
                    _nextDecision = clock.GetTimestamp();
                    targeting.Advance(context);
                    if (_haulSettings.Enabled)
                    {
                        UpdateStatus(context, "Starting AutoMining");
                    }
                    Advance(context);
                    break;
                case "Stop":
                    if (_flight is not null)
                    {
                        context.Flight.Checkpoint();
                        CheckFlight(context);
                        targeting.Advance(context);
                    }
                    if (_trip is not null && context.Pilot.Position is not null)
                    {
                        _ = context.Flight.Execute(new FlightCommand.Stop(), automatic: true);
                    }
                    Cancel(context, "Stopped", settleDue: true);
                    break;
                case "SaveFilter":
                    var filter = AutoMiningFilter.Parse(BlueMarshal.Text(args[0]));
                    store.SaveFilter(_pilotId, filter);
                    bool changed = filter.Text != _filter.Text;
                    _filter = filter;
                    if (_flight is not null && _trip is null && changed)
                    {
                        context.Flight.Checkpoint();
                        CheckFlight(context);
                        if (_flight is not null)
                        {
                            targeting.Advance(context);
                            mining.StopAutomatic(context, settleDue: true);
                            targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
                            _nextDecision = clock.GetTimestamp();
                            Advance(context);
                        }
                    }
                    break;
            }
            return new AutoMiningStatus(_flight is not null, _status, _filter.Text, _haulSettings);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    public void CheckFlight(PilotCallContext context)
    {
        if (_flight is not { } flight)
        {
            return;
        }
        if (!ReferenceEquals(context.ActiveFlight, flight) || context.Pilot.ShipId != _ship || context.Pilot.SystemId != _system)
        {
            Cancel(context, "Stopped after changing ship or system");
        }
        else if (_trip is not null && _navigationRevision != flight.PilotNavigationRevision)
        {
            Cancel(context, "Stopped for manual navigation");
        }
        else if (_trip is null && !CanRun(flight))
        {
            Cancel(context, "Stopped after leaving the grid or losing targeting");
        }
        else if (_trip is not null && (flight.Translocating || flight.Cloaked || flight.MicroJumpSpooling))
        {
            Cancel(context, "Stopped after a manual ship maneuver");
        }
        else if (_navigationRevision != flight.PilotNavigationRevision)
        {
            _navigationRevision = flight.PilotNavigationRevision;
            _miningOrigin = null;
        }
    }

    private static bool CanRun(FlightSession flight) => flight.CanAcquireTargets && !flight.PreparingWarp && !flight.MicroJumpSpooling;

    public void Cancel(PilotCallContext context, string status = "Stopped", bool settleDue = false)
    {
        if (_flight is not null)
        {
            mining.StopAutomatic(context, settleDue);
            if (context.Selected)
            {
                targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
            }
            mining.ResetManualStops();
            targeting.ResetAutoMiningExclusions();
            _flight = null;
            _trip = null;
            _miningOrigin = null;
            mining.AutoRepeatThreshold = null;
        }
        UpdateStatus(context, status);
    }

    public void Advance(PilotCallContext context, bool sessionTick = false)
    {
        CheckFlight(context);
        if (_trip is not null)
        {
            if (sessionTick)
            {
                AdvanceHaul(context);
            }
            return;
        }
        if (_flight is null || clock.GetTimestamp() < _nextDecision)
        {
            return;
        }
        if (_haulSettings is { Enabled: true })
        {
            if (!sessionTick)
            {
                return;
            }
            if (TryBeginHaul(context))
            {
                return;
            }
        }
        else if (sessionTick)
        {
            // The RPC and tick coordinators already advance ordinary grid mining.
            return;
        }

        var modules = store.Modules(_pilotId, _ship);
        var idle = modules.Where(module => !mining.IsActive(module.Id) && !mining.WasManuallyStopped(module.Id)).ToArray();
        if (idle.Length == 0)
        {
            SetStatus(
                context,
                modules.Any(module => mining.IsAutomatic(module.Id)) ? "Mining"
                    : modules.Count == 0 ? "Waiting for an online ore mining module"
                    : "Waiting for a free mining module",
                s_retryInterval
            );
            return;
        }

        double lockRange = fitting.ShipAttributes(_pilotId).GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
        var candidates = AutoMiningCandidate.Nearest(
            _flight.VisibleAsteroids,
            _filter!,
            context.Pilot.Position!,
            context.Pilot.Flight.ShipRadius,
            Math.Min(lockRange, idle.Max(module => module.Range))
        );
        // Release idle automatic locks before asking for another. Keep a lock
        // when a manual cycle or a pilot's own lock request has taken it over.
        targeting.ReleaseAutoMiningLocks(
            context,
            id =>
                mining.IsMiningTarget(id)
                || candidates.Any(candidate => candidate.Rock.Id == id && idle.Any(module => Eligible(module, candidate)))
        );

        bool pending = false;
        bool holdFull = false;
        string? blocked = null;
        foreach (var module in idle)
        {
            foreach (var candidate in candidates)
            {
                if (!Eligible(module, candidate))
                {
                    continue;
                }
                long target = candidate.Rock.Id;
                if (!targeting.IsLocked(target))
                {
                    if (targeting.LockForAutoMining(context, target))
                    {
                        pending = true;
                        break;
                    }
                    blocked ??= "Waiting for a target lock";
                    continue;
                }
                try
                {
                    mining.StartAutomatic(context, module.Id, target);
                    break;
                }
                catch (CargoCapacityException)
                {
                    holdFull = true;
                }
                catch (InvalidDataException error)
                {
                    blocked = error.Message;
                    // A previous cycle on the same frame can have emptied this
                    // rock. Try other eligible targets before the next scene refresh.
                }
            }
        }
        if (modules.Any(module => mining.IsAutomatic(module.Id)))
        {
            SetStatus(context, "Mining", s_retryInterval);
        }
        else if (pending)
        {
            SetStatus(context, "Locking asteroid", s_retryInterval);
        }
        else if (holdFull)
        {
            if (!sessionTick || !TryBeginHaul(context, unitDoesNotFit: true))
            {
                SetStatus(context, "Ore hold has no room for another unit", s_blockedInterval);
            }
        }
        else
        {
            SetStatus(context, blocked ?? "Waiting for matching ore in range", blocked is null ? s_retryInterval : s_blockedInterval);
        }
    }

    private bool Eligible(AutoMiningModule module, AutoMiningCandidate candidate) =>
        candidate.Distance <= module.Range && !targeting.IsExcludedFromAutoMining(candidate.Rock.Id) && module.Matches(candidate.Rock);

    private void SetStatus(PilotCallContext context, string status, TimeSpan retry)
    {
        UpdateStatus(context, status);
        _nextDecision = clock.GetTimestamp() + (long)(clock.TimestampFrequency * retry.TotalSeconds);
    }

    private void UpdateStatus(PilotCallContext context, string status)
    {
        if (status != _status)
        {
            _status = status;
            context.Notifications.Add(new AutoMiningStatusChanged(status));
        }
    }
}

internal sealed record AutoMiningStatusChanged(string Status) : PilotNotification("OnEdenAutoMiningStatus")
{
    public override object?[] Arguments => [new BlueUnicode(Status)];
}

internal sealed record AutoMiningStations(IReadOnlyList<AutoMiningStation> Stations) : BlueModel
{
    public override object ToBlue() =>
        new BlueList<AutoMiningStationRow>(Stations.Select(station => new AutoMiningStationRow(station)).ToArray());
}

internal sealed record AutoMiningStationRow(AutoMiningStation Station) : BlueModel
{
    public override object ToBlue() => new object[] { Station.Id, new BlueUnicode(Station.Name) };
}

internal sealed record AutoMiningStatus(bool Running, string Status, string OreFilter, AutoMiningHaulSettings Hauling) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["running"] = Running,
            ["status"] = new BlueUnicode(Status),
            ["oreFilter"] = new BlueUnicode(OreFilter),
            ["unloadStation"] = Hauling.StationId,
            ["unloadThreshold"] = Hauling.Threshold,
        };
}
