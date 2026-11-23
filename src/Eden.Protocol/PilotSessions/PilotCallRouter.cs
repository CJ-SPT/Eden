using Eden.Gameplay.Alliances;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal interface IPilotCallHandler
{
    PilotCallResult Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords);
}

internal interface ISelectedPilotCallHandler
{
    PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    );
}

internal sealed class PilotCallContext(
    long accountId,
    AccountProfile account,
    IReadOnlyList<DockedPilot> roster,
    Func<DockedPilot, FlightSession> startFlight,
    long sessionId,
    SignedSettings signedSettings
) : IDisposable
{
    public long AccountId { get; } = accountId;
    internal long SessionId { get; } = sessionId;
    public long Roles { get; init; } = (long)PilotRole.Player;
    public AccountProfile Account { get; } = account;
    public IReadOnlyList<DockedPilot> Roster { get; set; } = roster;
    public FlightSession? ActiveFlight { get; private set; }
    internal Eden.Gameplay.Structures.IStructureControlLease? StructureControl { get; private set; }
    internal long? ControlledStructureId => StructureControl?.StructureId;

    internal void TakeStructureControl(Eden.Gameplay.Structures.IStructureControlLease lease)
    {
        if (StructureControl != lease)
        {
            ReleaseStructureControl();
            StructureControl = lease;
        }
        Flight.SetStructureControl(lease.StructureId);
    }

    internal void ReleaseStructureControl()
    {
        StructureControl?.Dispose();
        StructureControl = null;
        ActiveFlight?.SetStructureControl(null);
    }

    public DockedPilot? DestroyedShip { get; private set; }
    public FlightSession Flight => ActiveFlight ?? throw new InvalidOperationException("Select a pilot before entering flight.");

    public void Select(DockedPilot pilot)
    {
        ReleaseStructureControl();
        ReportedSkillTypes.Clear();
        ObserveExpertSystems(null);
        DestroyedShip = null;
        ActiveFlight = startFlight(pilot);
    }

    public void ChangeShip(DockedPilot pilot)
    {
        ReleaseStructureControl();
        bool sameStation =
            ActiveFlight?.Pilot is { Position: null } previous
            && pilot.Position is null
            && previous.DockedLocationId == pilot.DockedLocationId;
        DestroyedShip = null;
        ActiveFlight?.Dispose();
        // The client binds the incoming ship's inventory before Board. Keep
        // station inventories usable through boarding, including that binding.
        if (!sameStation)
        {
            ReleaseLocationReferences();
        }
        ActiveFlight = startFlight(pilot);
        Roster = Roster.Select(p => p.PilotId == pilot.PilotId ? pilot : p).ToArray();
    }

    public void ReplaceDestroyedShip(DockedPilot pilot, IReadOnlyList<StaticSpaceObject>? destination = null)
    {
        ReleaseStructureControl();
        DestroyedShip = Pilot;
        Flight.ReplaceDestroyedShip(pilot, destination);
        Roster = Roster.Select(p => p.PilotId == pilot.PilotId ? pilot : p).ToArray();
    }

    public void Dispose()
    {
        ReleaseStructureControl();
        ActiveFlight?.Dispose();
    }

    public void LogOff()
    {
        ReleaseStructureControl();
        ActiveFlight?.Dispose();
        ActiveFlight = null;
        DestroyedShip = null;
        NextSkillCompletion = null;
        ReportedSkillTypes.Clear();
        ObserveExpertSystems(null);
        _bindings.ReleasePilotReferences();
        NotificationsBeforeFlight.Clear();
        Notifications.Clear();
    }

    public DockedPilot Pilot => Flight.Pilot;
    public SignedSettings SignedSettings { get; } = signedSettings;
    public bool LocalizationQaWrap { get; set; }
    public bool AssetHoldingItemValidationEnabled { get; set; } = true;
    public bool AssetHoldingItemFailureEnabled { get; set; }
    public bool Selected => ActiveFlight is not null;
    public long? NextSkillCompletion { get; set; }
    internal ExpertSystemState? ExpertSystems { get; private set; }
    internal long? NextExpertSystemExpiry { get; private set; }

    internal void ObserveExpertSystems(ExpertSystemState? state)
    {
        ExpertSystems = state;
        NextExpertSystemExpiry = state?.Systems.Min(system => (long?)system.ExpiresAt);
    }

    internal HashSet<int> ReportedSkillTypes { get; } = [];
    public List<PilotNotification> Notifications { get; } = [];

    // Combat text resolves the target's slim item, including on a fatal hit.
    // Deliver it before Destiny removes that ball from the client's park.
    public List<PilotNotification> NotificationsBeforeFlight { get; } = [];

    private readonly PilotBindings _bindings = new(sessionId);

    public void ReleaseFleetReferences(long? keepFleet = null) => _bindings.ReleaseFleetReferences(keepFleet);

    public void ReleaseCorporationReferences() => _bindings.ReleaseCorporationReferences();

    public void ReleaseLocationReferences()
    {
        DestroyedShip = null;
        _bindings.ReleaseLocationReferences();
    }

    public string? ResolveReference(object? objectId) => _bindings.ResolveReference(objectId);

    public void ReleaseReference(string service) => _bindings.ReleaseReference(service);

    public BlueObjectReference Reference(string service, PilotBindingLifetime lifetime = PilotBindingLifetime.Pilot) =>
        _bindings.Reference(service, lifetime);
}

[Injectable(InjectionType.Scoped)]
internal sealed class PilotCallRouter(
    IEnumerable<IPilotCallHandler> accountHandlers,
    IEnumerable<ISelectedPilotCallHandler> selectedHandlers,
    IAlliances? alliances = null
)
{
    // Build 3662168 eveMoniker.GetLocationBindParams uses inventory group IDs for its moniker tuple.
    private const int SolarSystemGroupId = 5;
    private const int StationGroupId = 15;

    public string ResolveService(PilotCallContext context, object?[] route, object? objectId)
    {
        return route switch
        {
            [1L, (long)PilotLogin.NodeId, null, _] when context.Selected => context.ResolveReference(objectId) ?? "unknown binding",
            [1L, _, null, _] => "unknown binding",
            [1L, _, var name, _] => NamedService(name),
            [8L, var name, _] => NamedService(name),
            _ => throw new InvalidDataException("Invalid service address."),
        };
    }

    private static string NamedService(object? value)
    {
        string name = BlueMarshal.Text(value);
        return name.StartsWith('@') ? "unknown binding" : name;
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Action<string> report,
        Dictionary<string, object?>? keywords = null
    )
    {
        keywords = ServiceKeywords(keywords);
        foreach (var handler in accountHandlers)
        {
            PilotCallResult result = handler.Call(context, service, method, arguments, keywords);
            if (result is not PilotCallResult.UnsupportedCall)
            {
                return result;
            }
        }
        return context.Selected && arguments is object?[] args
            ? CallSelected(context, service, method, args, report, keywords)
            : PilotCallResult.Unsupported;
    }

    private PilotCallResult CallSelected(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Action<string> report,
        Dictionary<string, object?>? keywords
    )
    {
        keywords = ServiceKeywords(keywords);
        if (
            context.ControlledStructureId.HasValue
            && (
                service == "ship" && method is "Undock" or "Board" or "LeaveShip"
                || service == "beyonce" && method.StartsWith("Cmd", StringComparison.Ordinal)
            )
        )
        {
            return new PilotCallResult.Failure("Release structure control before moving or boarding your ship.");
        }
        if (
            service
            is "jumpCloneSvc"
                or "invbroker"
                or "dogmaIM"
                or "corpRegistry"
                or "allianceRegistry"
                or "crimewatch"
                or "warRegistry"
                or "ship"
                or "insuranceSvc"
                or "beyonce"
                or "entity"
                or "corpStationMgr"
                or "officeManager"
                or "reprocessingSvc"
                or "repairSvc"
                or "charMgr"
                or "agentMgr"
                or "planetMgr"
                or "planetOrbitalRegistryBroker"
        )
        {
            if (
                service is "planetMgr" or "planetOrbitalRegistryBroker"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    keywords is { Count: > 0 }
                    || args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || args[0] is not long planetaryId
                    || planetaryId is <= 0 or > int.MaxValue
                )
            )
            {
                return new PilotCallResult.Failure("Bind a valid planet or planetary orbital system.");
            }
            if (
                service == "agentMgr"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    keywords is { Count: > 0 }
                    || args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || args[0] is not long agentId
                    || !Eden.Gameplay.Agents.NpcCharacterIds.Contains(agentId)
                )
            )
            {
                return new PilotCallResult.Failure("Bind a known NPC agent by its character ID.");
            }
            if (
                service == "charMgr"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    keywords is { Count: > 0 }
                    || args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || args[0] is not object?[] { Length: 2 } globalBind
                    || !Equals(globalBind[0], context.Pilot.PilotId)
                    || !Equals(globalBind[1], GlobalInventoryHandler.ContainerId)
                )
            )
            {
                return PilotCallResult.Unsupported;
            }
            if (
                service == "corpRegistry"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || !Equals(args[0], (long)context.Pilot.CorpId)
                    || keywords is { Count: > 0 }
                )
            )
            {
                return new PilotCallResult.Failure("Bind your current corporation registry.");
            }
            if (
                service == "allianceRegistry"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    keywords is { Count: > 0 }
                    || args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || args[0] is not long allianceId
                    || alliances?.Affiliation(context.Pilot.CorpId) != allianceId
                )
            )
            {
                return new PilotCallResult.Failure("Bind your current alliance registry.");
            }
            if (
                service == "officeManager"
                && method is "MachoResolveObject" or "MachoBindObject"
                && (
                    context.Pilot.Position is not null
                    || args.Length != (method == "MachoResolveObject" ? 1 : 2)
                    || !Equals(args[0], (long)context.Pilot.StationId)
                    || keywords is { Count: > 0 }
                )
            )
            {
                return new PilotCallResult.Failure("Bind the office service at your current station.");
            }
            if (method == "MachoResolveObject")
            {
                return PilotLogin.NodeId;
            }
            if (method == "MachoBindObject" && args.Length == 2)
            {
                // Upwell monikers bind in the solar system, including while the real ship remains docked.
                bool systemLocation = context.Pilot.Position is not null || context.Pilot.InStructure;
                long bindLocation = systemLocation ? context.Pilot.SystemId : context.Pilot.DockedLocationId;
                long locationGroup = systemLocation ? SolarSystemGroupId : StationGroupId;
                if (
                    service == "insuranceSvc"
                    && (
                        context.Pilot.Position is not null
                        || args[0] is not object?[] insuranceBind
                        || insuranceBind is not [long insuranceStation, long insuranceLocation, (long)InsuranceRules.StationGroup]
                        || insuranceStation != context.Pilot.DockedLocationId
                        || insuranceLocation != insuranceStation
                    )
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service == "repairSvc"
                    && (
                        context.Pilot.Position is not null
                        || args[0] is not object?[] repairBind
                        || repairBind is not [long dockable, long repairLocation, long repairGroup]
                        || dockable != context.Pilot.DockedLocationId
                        // Build 3662168 RepairSvc.GetRemoteRepairMgr binds Upwell repair to the solar system.
                        || repairLocation != (context.Pilot.InStructure ? context.Pilot.SystemId : dockable)
                        || repairGroup != (context.Pilot.InStructure ? SolarSystemGroupId : RepairDefinitions.StationGroup)
                    )
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service is "corpStationMgr" or "reprocessingSvc" or "officeManager"
                    && (context.Pilot.Position is not null || !Equals(args[0], context.Pilot.DockedLocationId))
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service is "invbroker" or "ship" or "jumpCloneSvc"
                    && (args[0] is not null || service == "jumpCloneSvc")
                    && (
                        args[0] is not object?[] bind
                        || bind is not [long location, long group]
                        || location != bindLocation
                        || group != locationGroup
                    )
                )
                {
                    return PilotCallResult.Unsupported;
                }
                if (
                    service is "beyonce" or "entity"
                    && (context.Pilot.Position is null && !context.Pilot.InStructure || !Equals(args[0], (long)context.Pilot.SystemId))
                )
                {
                    return PilotCallResult.Unsupported;
                }
                PilotCallResult result = PilotCallResult.None;
                string boundService =
                    service == "charMgr" ? GlobalInventoryHandler.BoundService
                    : service == "agentMgr" ? $"@agent:{args[0]}"
                    : service == "planetMgr" ? $"@planet:{args[0]}"
                    : service == "planetOrbitalRegistryBroker" ? $"@planetorbit:{args[0]}"
                    : service;
                if (args[1] is object?[] { Length: >= 2 } first && first[1] is object?[] innerArgs)
                {
                    string innerMethod = BlueMarshal.Text(first[0]);
                    report($"Bound call {service}.{innerMethod}.");
                    result = CallSelected(
                        context,
                        boundService,
                        innerMethod,
                        innerArgs,
                        report,
                        first.ElementAtOrDefault(2) as Dictionary<string, object?>
                    );
                }
                else if (args[1] is not null)
                {
                    return PilotCallResult.Unsupported;
                }
                // A failed first call is an RPC failure, not a value to embed in
                // the successful (bound reference, result) tuple.
                if (result is not (PilotCallResult.Success or PilotCallResult.ClientCall))
                {
                    return result;
                }
                var reference = context.Reference(
                    boundService,
                    // The corporation moniker is cached against corpid across docking and travel.
                    service
                        is "charMgr"
                            or "agentMgr"
                            or "planetMgr"
                            or "planetOrbitalRegistryBroker"
                            or "corpRegistry"
                            or "allianceRegistry"
                        ? PilotBindingLifetime.Pilot
                        : PilotBindingLifetime.Location
                );
                return result switch
                {
                    PilotCallResult.Success success => new PilotCallResult.Success(new BoundPilotReply(reference, success.Reply)),
                    PilotCallResult.ClientCall client => new PilotCallResult.ClientCall(
                        new BoundPilotClientDialog(client.Dialog, reference)
                    ),
                    _ => result,
                };
            }
        }
        foreach (var handler in selectedHandlers)
        {
            PilotCallResult result = handler.Call(context, service, method, args, keywords);
            if (result is not PilotCallResult.UnsupportedCall)
            {
                return result;
            }
        }
        return PilotCallResult.Unsupported;
    }

    private static Dictionary<string, object?>? ServiceKeywords(Dictionary<string, object?>? keywords)
    {
        // Build 3662168 ServiceCallGPCS supplies machoVersion even for uncached
        // methods. It belongs to MachoNet, not the service's argument contract.
        // Apply this to embedded MachoBindObject calls as well as direct calls.
        return keywords is not null && keywords.ContainsKey("machoVersion")
            ? keywords.Where(pair => pair.Key != "machoVersion").ToDictionary()
            : keywords;
    }
}
