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
    TargetingHandler targeting,
    MiningHandler mining,
    IAutoMiningAvailability? availability = null,
    IPilotAutoMiningHauling? hauling = null,
    IPilotAutoMiningRoutes? routes = null,
    IAutoMiningFleets? fleets = null,
    ISystemObjects? systems = null
) : ISelectedPilotCallHandler
{
    internal const string Service = "edenAutoMining";
    private static readonly TimeSpan s_retryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_blockedInterval = TimeSpan.FromSeconds(3);
    private long _pilotId;
    private AutoMiningFilter? _filter;
    private AutoMiningOptions? _options;
    private AutoMiningOptions Options => _options ?? AutoMiningOptions.Default;
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
        if (
            service != Service
            || method
                is not (
                    "Start"
                    or "Stop"
                    or "GetStatus"
                    or "SaveFilter"
                    or "GetStations"
                    or "SaveHauling"
                    or "SaveOptions"
                    or "SaveSettings"
                    or "SaveRoute"
                    or "SavePlan"
                    or "SearchSystems"
                    or "SearchStations"
                )
        )
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
                "SavePlan" => ValidPlanArguments(args),
                "SearchSystems" or "SearchStations" => args is [string or byte[]],
                "SaveRoute" => args is [string or byte[], long mode] && mode is >= 0 and <= (long)AutoMiningFleetMode.Follow,
                "SaveFilter" => args is [string or byte[]],
                "SaveOptions" => args is [long family, long order, bool, bool] && ValidOptions(family, order),
                "SaveHauling" => args is [long, long and >= int.MinValue and <= int.MaxValue],
                "SaveSettings" => args
                    is [
                        null
                        or string
                        or byte[]
                        or List<object?>,
                        long family,
                        long order,
                        bool,
                        bool,
                        long,
                        long
                        and >= int.MinValue
                        and <= int.MaxValue,
                    ]
                    && ValidOptions(family, order),
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
                _options = null;
                _haulSettings = null;
                _route = null;
                _jettisonReadyAt = 0;
                _status = "Stopped";
            }
            _filter ??= store.ReadFilter(_pilotId);
            _options ??= store.ReadOptions(_pilotId);
            _haulSettings ??= hauling?.ReadHauling(_pilotId) ?? AutoMiningHaulSettings.Disabled;
            _route ??= routes?.ReadRoute(_pilotId) ?? AutoMiningRoute.Empty;
            switch (method)
            {
                case "SavePlan":
                    SavePlan(context, args);
                    break;
                case "SearchSystems":
                    return new AutoMiningSystems(routes?.SearchSystems(BlueMarshal.Text(args[0])) ?? []);
                case "SearchStations":
                    return new AutoMiningStations(routes?.SearchStations(BlueMarshal.Text(args[0]), context.Pilot.SystemId) ?? []);
                case "SaveRoute":
                    if (_flight is not null)
                    {
                        throw new InvalidDataException("Stop AutoMining before changing the route or fleet mode.");
                    }
                    _route = (routes ?? throw new InvalidDataException("AutoMining routes are unavailable.")).SaveRoute(
                        _pilotId,
                        context.Pilot.SystemId,
                        BlueMarshal.Text(args[0]),
                        (AutoMiningFleetMode)(long)args[1]!
                    );
                    break;
                case "SaveSettings":
                    if (hauling is null)
                    {
                        return new PilotCallResult.Failure("AutoMining hauling is unavailable.");
                    }
                    var newFilter = args[0] is null ? _filter : ReadFilter(args[0]);
                    var newOptions = ReadOptions(args.AsSpan(1, 4));
                    var newHauling = new AutoMiningHaulSettings((long)args[5]!, checked((int)(long)args[6]!));
                    CheckOptionChange(newOptions);
                    store.SaveSettings(_pilotId, context.Pilot.SystemId, newFilter, newOptions, newHauling);
                    bool reselect = newOptions != Options || (newOptions.Family == MiningFamily.Ore && newFilter.Text != _filter.Text);
                    _filter = newFilter;
                    _options = newOptions;
                    _haulSettings = newHauling;
                    if (reselect)
                    {
                        RestartSelection(context);
                    }
                    UpdateRepeatThreshold();
                    break;
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
                    UpdateRepeatThreshold();
                    break;
                case "SaveOptions":
                    var options = ReadOptions(args);
                    CheckOptionChange(options);
                    store.SaveOptions(_pilotId, options);
                    if (options != Options)
                    {
                        _options = options;
                        RestartSelection(context);
                        UpdateRepeatThreshold();
                    }
                    break;
                case "Start":
                    if (_flight is not null)
                    {
                        if (_trip is null && _journey is null)
                        {
                            mining.ResetManualStops();
                            targeting.ResetAutoMiningExclusions();
                            _nextDecision = clock.GetTimestamp();
                            _routeExhausted = false;
                            Advance(context);
                        }
                        break;
                    }
                    context.Flight.Checkpoint();
                    CheckFlight(context);
                    if (!CanRun(context.Flight))
                    {
                        return new PilotCallResult.Failure("Start AutoMining in space outside warp, with targeting available.");
                    }
                    if (!store.Modules(_pilotId, context.Pilot.ShipId).Any(module => module.Family == Options.Family))
                    {
                        return new PilotCallResult.Failure("Fit and online a harvesting module for the selected resource first.");
                    }
                    PrepareRoute(context);
                    _flight = context.Flight;
                    _ship = context.Pilot.ShipId;
                    _system = context.Pilot.SystemId;
                    _navigationRevision = _flight.PilotNavigationRevision;
                    _miningOrigin = null;
                    UpdateRepeatThreshold();
                    mining.ResetManualStops();
                    targeting.ResetAutoMiningExclusions();
                    _nextDecision = clock.GetTimestamp();
                    StartRoute(context);
                    targeting.Advance(context);
                    if (UnloadEnabled)
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
                    if ((_trip is not null || _journey is not null) && context.Pilot.Position is not null)
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
                    if (changed && Options.Family == MiningFamily.Ore)
                    {
                        RestartSelection(context);
                    }
                    break;
            }
            return new AutoMiningStatus(_flight is not null, _status, _filter, _haulSettings, Options, store.OreTypes())
            {
                RouteText = routes?.Describe(_route) ?? "",
                FleetMode = _route.FleetMode,
                RouteStops = routes?.DescribeSystems(_route) ?? [],
                UnloadStationName = _haulSettings.Enabled ? routes?.Station(_haulSettings.StationId).Name ?? "Saved station" : "",
            };
        }
        catch (InvalidDataException error)
        {
            if (method == "Start" && _flight is not null)
            {
                Cancel(context, "Paused: " + error.Message);
            }
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static bool ValidOptions(long family, long order) =>
        family is >= (long)MiningFamily.Ore and <= (long)MiningFamily.Gas
        && order is >= (long)AutoMiningTargetOrder.Nearest and <= (long)AutoMiningTargetOrder.Smallest;

    private static AutoMiningOptions ReadOptions(ReadOnlySpan<object?> args) =>
        new((MiningFamily)(long)args[0]!, (AutoMiningTargetOrder)(long)args[1]!, (bool)args[2]!, (bool)args[3]!);

    private AutoMiningFilter ReadFilter(object? value)
    {
        if (value is not List<object?> ids)
        {
            return AutoMiningFilter.Parse(BlueMarshal.Text(value));
        }
        if (ids.Count > AutoMiningFilter.MaximumTypes || ids.Any(id => id is not (long and > 0 and <= int.MaxValue)))
        {
            throw new InvalidDataException("Choose ore types from the available list.");
        }
        return AutoMiningFilter.FromTypes(ids.Select(id => (int)(long)id!), store.OreTypes());
    }

    private void CheckOptionChange(AutoMiningOptions options)
    {
        if ((_trip is not null || _journey is not null) && options != Options)
        {
            throw new InvalidDataException("Finish the haul trip or stop AutoMining before changing mining options.");
        }
        if (
            _flight is not null
            && _route is { Systems.Length: > 0, FleetMode: not AutoMiningFleetMode.Follow }
            && options.Family != MiningFamily.Ore
        )
        {
            throw new InvalidDataException("Stop the asteroid-belt route before changing resource family.");
        }
    }

    public void CheckFlight(PilotCallContext context)
    {
        if (_flight is not { } flight)
        {
            return;
        }
        if (
            ReferenceEquals(context.ActiveFlight, flight)
            && context.Pilot.ShipId == _ship
            && _expectedSystem == context.Pilot.SystemId
            && _journey is not null
        )
        {
            _system = context.Pilot.SystemId;
            _expectedSystem = null;
            _journey = _journey with { Started = clock.GetTimestamp(), MovingTo = null };
        }
        if (!ReferenceEquals(context.ActiveFlight, flight) || context.Pilot.ShipId != _ship || context.Pilot.SystemId != _system)
        {
            Cancel(context, "Stopped after changing ship or system");
        }
        else if ((_trip is not null || Options.Approach || RouteActive) && _navigationRevision != flight.PilotNavigationRevision)
        {
            Cancel(context, "Stopped for manual navigation");
        }
        else if (_trip is null && _journey is null && !CanRun(flight))
        {
            Cancel(context, "Stopped after leaving the grid or losing targeting");
        }
        else if ((_trip is not null || _journey is not null) && (flight.Translocating || flight.Cloaked || flight.MicroJumpSpooling))
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
            if (
                (_journey is not null || _trip is not null)
                && ReferenceEquals(context.ActiveFlight, _flight)
                && _navigationRevision == _flight.PilotNavigationRevision
                && context.Pilot.Position is not null
            )
            {
                _ = _flight.Execute(new FlightCommand.Stop(), automatic: true);
            }
            StopApproach(context);
            mining.StopAutomatic(context, settleDue);
            if (context.Selected)
            {
                targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
            }
            mining.ResetManualStops();
            targeting.ResetAutoMiningExclusions();
            _flight = null;
            _trip = null;
            _journey = null;
            _destination = null;
            _expectedSystem = null;
            _routeExhausted = false;
            fleets?.Stop(_pilotId);
            _miningOrigin = null;
            mining.AutoRepeatThreshold = null;
        }
        UpdateStatus(context, status);
    }

    public void Advance(PilotCallContext context, bool sessionTick = false)
    {
        try
        {
            AdvanceSelection(context, sessionTick);
        }
        catch (InvalidDataException error)
        {
            StopApproach(context);
            SetStatus(context, error.Message, s_blockedInterval);
        }
    }

    private void AdvanceSelection(PilotCallContext context, bool sessionTick)
    {
        CheckFlight(context);
        if (_flight is not null && AvoidRats(context, sessionTick))
        {
            return;
        }
        if (_flight is not null && RouteActive)
        {
            if (!sessionTick)
            {
                return;
            }
            if (AdvanceRoute(context))
            {
                return;
            }
        }
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
        if (UnloadEnabled)
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
        else if (sessionTick && !RouteActive)
        {
            // The RPC and tick coordinators already advance ordinary grid mining.
            return;
        }

        var modules = store.Modules(_pilotId, _ship).Where(module => module.Family == Options.Family).ToArray();
        var idle = modules.Where(module => !mining.IsActive(module.Id) && !mining.WasManuallyStopped(module.Id)).ToArray();
        if (idle.Length == 0)
        {
            StopApproach(context);
            SetStatus(
                context,
                modules.Any(module => mining.IsAutomatic(module.Id)) ? "Mining"
                    : modules.Length == 0 ? "Waiting for an online harvesting module"
                    : "Waiting for a free mining module",
                s_retryInterval
            );
            return;
        }

        double lockRange = store.ShipAttributes(_pilotId).GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
        AutoMiningCandidate[] ranked;
        try
        {
            ranked = RankedTargets(context, idle, lockRange);
        }
        catch (InvalidDataException error)
        {
            StopApproach(context);
            targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
            SetStatus(context, error.Message, s_blockedInterval);
            return;
        }
        var candidates = ranked
            .Where(candidate => candidate.Distance <= lockRange && idle.Any(module => Eligible(module, candidate)))
            .ToArray();
        if (TryApproach(context, ranked, candidates, idle, lockRange))
        {
            targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
            return;
        }
        StopApproach(context);
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
                    mining.StartAutomatic(context, module, target);
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
            SetStatus(context, Options.Family == MiningFamily.Gas ? "Locking gas cloud" : "Locking asteroid", s_retryInterval);
        }
        else if (holdFull)
        {
            if (!sessionTick || !TryBeginHaul(context, unitDoesNotFit: true))
            {
                SetStatus(
                    context,
                    Options.Family == MiningFamily.Ore
                        ? "Ore hold has no room for another unit"
                        : "Harvesting hold has no room for another unit",
                    s_blockedInterval
                );
            }
        }
        else
        {
            _routeExhausted = RouteActive && candidates.Length == 0 && blocked is null && !mining.HasCycle;
            var waiting =
                Options.Family == MiningFamily.Ore ? "Waiting for matching ore in range" : "Waiting for matching resources in range";
            SetStatus(context, blocked ?? waiting, blocked is null ? s_retryInterval : s_blockedInterval);
        }
    }

    private bool Eligible(AutoMiningModule module, AutoMiningCandidate candidate) =>
        candidate.Distance <= module.Range && module.Matches(candidate.Rock);

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

internal sealed record AutoMiningOreRow(AutoMiningOre Ore) : BlueModel
{
    public override object ToBlue() => new object[] { Ore.TypeId, new BlueUnicode(Ore.Name) };
}

internal sealed record AutoMiningStatus(
    bool Running,
    string Status,
    AutoMiningFilter Filter,
    AutoMiningHaulSettings Hauling,
    AutoMiningOptions Options,
    IReadOnlyList<AutoMiningOre> OreTypes
) : BlueModel
{
    internal string RouteText { get; init; } = "";
    internal AutoMiningFleetMode FleetMode { get; init; }
    internal IReadOnlyList<AutoMiningSystem> RouteStops { get; init; } = [];
    internal string UnloadStationName { get; init; } = "";
    internal string OreFilter => Filter.Text;

    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["running"] = Running,
            ["status"] = new BlueUnicode(Status),
            ["oreFilter"] = new BlueUnicode(OreFilter),
            ["oreGroups"] = new BlueList<BlueUnicode>(
                AutoMiningFilter.LegacyGroupNames(OreTypes).Select(name => new BlueUnicode(name)).ToArray()
            ),
            ["oreTypes"] = new BlueList<AutoMiningOreRow>(OreTypes.Select(ore => new AutoMiningOreRow(ore)).ToArray()),
            ["selectedOreTypes"] = new BlueList<int>(
                OreTypes.Where(ore => Filter.Includes(ore.TypeId, ore.GroupId)).Select(ore => ore.TypeId).ToArray()
            ),
            ["unloadStation"] = Hauling.StationId,
            ["unloadThreshold"] = Hauling.Threshold,
            ["resourceFamily"] = (int)Options.Family,
            ["targetOrder"] = (int)Options.TargetOrder,
            ["approach"] = Options.Approach,
            ["jettison"] = Options.Jettison,
            ["routeSystems"] = new BlueUnicode(RouteText),
            ["fleetMode"] = (int)FleetMode,
            ["routeStops"] = new AutoMiningSystems(RouteStops),
            ["unloadStationName"] = new BlueUnicode(UnloadStationName),
        };
}
