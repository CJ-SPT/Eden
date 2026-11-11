using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Industry;

[Injectable(InjectionType.Scoped)]
internal sealed class IndustryHandler(IPilotIndustry industry) : ISelectedPilotCallHandler
{
    private const int BlueprintResultLimit = 10_000;
    private long _nextMonitor;
    private long _monitor;
    private long? _station;
    private IReadOnlyDictionary<int, long> _available = new Dictionary<int, long>();
    public bool IsMonitoring => _station.HasValue;

    public void Reset()
    {
        _station = null;
        _monitor = 0;
        _available = new Dictionary<int, long>();
    }

    private IndustryFacilityReply Facility(IndustryFacility facility) =>
        new(facility, industry.ProductTypes(facility.Id), industry.ScienceProductCategories());

    private static BlueprintReply Blueprint(OwnedBlueprint blueprint) => new(blueprint);

    private static IndustryJobReply Job(IndustryJobDetails job) => new(job);

    private static IndustryLocationReply Location(IndustryFacility facility, long pilotId) => new(facility, pilotId);

    public void Advance(PilotCallContext context)
    {
        if (_station is not long station || !context.Selected)
        {
            return;
        }
        if (context.Pilot.Position is not null || context.Pilot.DockedLocationId != station)
        {
            _station = null;
            return;
        }
        var current = industry.Materials(context.Pilot.PilotId, station);
        if (current.Count != _available.Count || current.Any(v => _available.GetValueOrDefault(v.Key) != v.Value))
        {
            _available = current;
            context.Notifications.Add(new IndustryMaterialsChanged(_monitor, current));
        }
    }

    private static Dictionary<string, object?> Values(object? value) =>
        value switch
        {
            Dictionary<string, object?> values => values,
            BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> values } => values,
            BlueNewObject { Name: BlueNewObject.IndustryLocationClass, State: Dictionary<string, object?> values } => values,
            _ => throw new InvalidDataException("Invalid industry request data."),
        };

    private static long Number(Dictionary<string, object?> values, string key) =>
        values.GetValueOrDefault(key) is long number ? number : throw new InvalidDataException($"Missing industry {key}.");

    private static int Integer(Dictionary<string, object?> values, string key) => checked((int)Number(values, key));

    private static bool IsTrue(object? value) => value is true or 1L;

    private long CheckLocation(PilotCallContext context, Dictionary<string, object?> request, string key)
    {
        var location = Values(request.GetValueOrDefault(key));
        long station = Number(location, "itemID");
        if (
            Number(location, "ownerID") != context.Pilot.PilotId
            || Number(location, "flagID") != (int)InventoryFlag.Hangar
            || station != Number(request, "facilityID")
        )
        {
            throw new InvalidDataException("Choose your current station's personal Item Hangar for industry.");
        }
        industry.CheckStation(context.Pilot.PilotId, station);
        return station;
    }

    private IndustryMonitorReply Connect(PilotCallContext context, object? request)
    {
        var values = Values(request);
        long station = CheckLocation(context, values, "inputLocation");
        _monitor = checked(++_nextMonitor);
        _station = station;
        _available = industry.Materials(context.Pilot.PilotId, station);
        return new IndustryMonitorReply(_monitor, _available);
    }

    private long Install(PilotCallContext context, object? request)
    {
        var values = Values(request);
        long station = CheckLocation(context, values, "inputLocation");
        _ = CheckLocation(context, values, "outputLocation");
        // Build 3662168 AttachSessionToJob includes the pilot's corporation even for personal jobs.
        // Personal ownership and payment are enforced by the hangar and wallet checks.
        var corporation = values.GetValueOrDefault("corporationID");
        var activity = (IndustryActivity)Integer(values, "activityID");
        if (
            Number(values, "characterID") != context.Pilot.PilotId
            || !IndustryDefinitions.Activities.Contains(activity)
            || corporation is not null && !Equals(corporation, (long)context.Pilot.CorpId)
            || values.GetValueOrDefault("account") is not object?[] { Length: 2 } account
            || !Equals(account[0], context.Pilot.PilotId)
            || !Equals(account[1], (long)WalletAccountKey.Cash)
        )
        {
            throw new InvalidDataException("Use personal industry with the current installation quote.");
        }
        var materials = values.GetValueOrDefault("materials") switch
        {
            Dictionary<string, object?> { Count: 0 } => new Dictionary<int, int>(),
            Dictionary<long, object?> items when items.Values.All(v => v is long) => items.ToDictionary(
                i => checked((int)i.Key),
                i => checked((int)(long)i.Value!)
            ),
            _ => throw new InvalidDataException("Missing industry material quote."),
        };
        double seconds = values.GetValueOrDefault("time") switch
        {
            long n => n,
            double n => n,
            _ => throw new InvalidDataException("Missing industry time quote."),
        };
        // Build 3662168 Job._get_time rounds very short previews to zero.
        // Normalize that wire quote to the server's one-second minimum;
        // authoritative rules still verify the actual calculated duration.
        if (seconds == 0)
        {
            seconds = IndustryDefinitions.MinimumJobSeconds;
        }
        // Job.dump leaves productTypeID as None for a single-output recipe.
        // The server resolves that product from the owned blueprint's catalog.
        int? product = values.TryGetValue("productTypeID", out var selectedProduct)
            ? selectedProduct switch
            {
                null => null,
                long type => checked((int)type),
                _ => throw new InvalidDataException("Invalid industry product selection."),
            }
            : throw new InvalidDataException("Missing industry product selection.");
        // Job.dump includes licensedRuns for every activity; its getter defaults to one.
        // Only copying uses this field. Other typed requests use zero for no copy output.
        int licensedRuns =
            activity == IndustryActivity.Copying && values.GetValueOrDefault("licensedRuns") is not null
                ? Integer(values, "licensedRuns")
                : 0;
        var result = industry.Install(
            context.Pilot.PilotId,
            new(
                Number(values, "blueprintID"),
                Integer(values, "blueprintTypeID"),
                station,
                Integer(values, "runs"),
                product,
                seconds,
                materials,
                new ManufacturingCharge(QuotedCents(values, "cost"), QuotedCents(values, "tax"), 0),
                activity,
                licensedRuns
            )
        );
        Notify(context, result);
        return result.Job.Id;
    }

    private static void Notify(PilotCallContext context, IndustryResult result)
    {
        if (result.BalanceCents is long balance)
        {
            context.Notifications.Add(new AccountBalanceChanged(context.Pilot.PilotId, balance / 100d));
        }
        foreach (var change in result.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        var job = result.Job;
        context.Notifications.Add(new IndustryJobChanged(job));
        InventoryChangesWire.NotifyBlueprintsUpdated(context, job.PilotId);
    }

    private static long QuotedCents(Dictionary<string, object?> values, string name)
    {
        double isk = values.GetValueOrDefault(name) switch
        {
            long number => number,
            double number => number,
            _ => throw new InvalidDataException("Missing industry price quote."),
        };
        if (!double.IsFinite(isk) || isk < 0 || isk != Math.Truncate(isk))
        {
            throw new InvalidDataException("Invalid industry price quote.");
        }
        return checked((long)((decimal)isk * 100));
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        // The native Industry button checks both factory and laboratory access
        // before opening its shared window; each install verifies its activity service.
        bool standing =
            service == "corpStationMgr"
            && method == "DoStandingCheckForStationService"
            && args is [(long)StationService.Manufacturing or (long)StationService.Laboratory];
        if (!standing && service is not ("industryManager" or "blueprintManager" or "facilityManager" or "industryMonitor"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long pilotId = context.Pilot.PilotId;
            if (standing)
            {
                industry.CheckAccess(pilotId, context.Pilot.DockedLocationId);
                return PilotCallResult.None;
            }
            switch (service, method)
            {
                case ("blueprintManager", "GetLimits") when args.Length == 0:
                    return new BlueprintLimits(BlueprintResultLimit);
                case ("blueprintManager", "GetBlueprintData") when args is [long item]:
                    return Blueprint(industry.Blueprint(pilotId, item));
                case ("blueprintManager", "GetBlueprintDataByOwner") when args is [long owner, null or long] && owner == pilotId:
                    var blueprints = industry.Blueprints(pilotId, args[1] is long facility ? facility : null);
                    return new BlueprintOwnerReply(blueprints, BlueprintResultLimit);
                case ("facilityManager", "GetFacilities") when args.Length == 0:
                    var categories = industry.ScienceProductCategories();
                    return new BlueList<IndustryFacilityReply>(
                        industry
                            .Facilities(pilotId)
                            .Select(f => new IndustryFacilityReply(f, industry.ProductTypes(f.Id), categories))
                            .ToArray()
                    );
                case ("facilityManager", "GetFacility") when args is [long station]:
                    return Facility(industry.Facility(pilotId, station));
                case ("facilityManager", "GetFacilityLocations") when args is [long station, long owner] && owner == pilotId:
                    industry.CheckStation(pilotId, station);
                    return new BlueList<IndustryLocationReply>([Location(industry.Facility(pilotId, station), pilotId)]);
                case ("facilityManager", "GetMaxActivityModifiers") when args.Length == 0:
                    return new BlueMap<IndustryActivity, double>(IndustryDefinitions.Activities.ToDictionary(a => a, _ => 1d));
                case ("industryManager", "GetJobsByOwner") when args is [long owner, var completed] && owner == pilotId:
                    return new BlueList<IndustryJobReply>(industry.Jobs(pilotId, IsTrue(completed)).Select(Job).ToArray());
                case ("industryManager", "GetJob") when args is [long job]:
                    return Job(industry.Job(pilotId, job));
                case ("industryManager", "GetJobCounts") when args is [long owner] && owner == pilotId:
                    var jobs = industry.Jobs(pilotId, false);
                    return new BlueMap<IndustryActivity, int>(
                        IndustryDefinitions.Activities.ToDictionary(a => a, a => jobs.Count(j => j.Activity == a))
                    );
                case ("industryManager", "InstallJob") when args is [var request]:
                    return Install(context, request);
                case ("industryManager", "CompleteJob" or "CancelJob") when args is [long job, long system]:
                    var result =
                        method == "CompleteJob"
                            ? industry.Deliver(pilotId, job, checked((int)system))
                            : industry.Cancel(pilotId, job, checked((int)system));
                    Notify(context, result);
                    return PilotCallResult.None;
                case ("industryMonitor", "ConnectJob") when args is [var request]:
                    return Connect(context, request);
                case ("industryMonitor", "DisconnectJob") when args is [null] || args is [long monitor] && monitor == _monitor:
                    _station = null;
                    return PilotCallResult.None;
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The industry job exceeds the supported quantity or time limit.");
        }
    }
}
