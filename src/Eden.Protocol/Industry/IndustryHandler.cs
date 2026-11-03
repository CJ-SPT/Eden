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
    private int? _station;
    private IReadOnlyDictionary<int, long> _available = new Dictionary<int, long>();
    public bool IsMonitoring => _station.HasValue;

    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);

    private static Dictionary<long, object?> Quantities(IReadOnlyDictionary<int, long> values) =>
        values.ToDictionary(v => (long)v.Key, v => (object?)v.Value);

    public void Advance(PilotCallContext context)
    {
        if (_station is not int station || !context.Selected)
        {
            return;
        }
        if (context.Pilot.Position is not null || context.Pilot.StationId != station)
        {
            _station = null;
            return;
        }
        var current = industry.Materials(context.Pilot.PilotId, station);
        if (current.Count != _available.Count || current.Any(v => _available.GetValueOrDefault(v.Key) != v.Value))
        {
            _available = current;
            context.Notifications.Add(("OnIndustryMaterials", [_monitor, Quantities(current)]));
        }
    }

    private object Facility(IndustryFacility facility) =>
        new Dictionary<string, object?>
        {
            ["facilityID"] = facility.Id,
            ["typeID"] = facility.TypeId,
            ["ownerID"] = facility.OwnerId,
            ["solarSystemID"] = facility.SystemId,
            ["online"] = true,
            // Adjusted industry prices are absent, matching the pinned client's
            // zero-price fallback used by reprocessing. No hidden installation fee.
            ["tax"] = 0d,
            ["sccTaxModifier"] = 1d,
            ["serviceAccess"] = new Dictionary<long, object?>(),
            ["rigModifiers"] = new Dictionary<long, object?>(),
            ["globalModifiers"] = new Dictionary<string, object?>
            {
                ["faction"] = 1d,
                ["system"] = new Dictionary<long, object?> { [(long)IndustryActivity.Manufacturing] = 1d },
            },
            ["activities"] = new Dictionary<long, object?>
            {
                [(long)IndustryActivity.Manufacturing] = new object?[]
                {
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    new List<object?>(),
                    industry.ProductTypes().Cast<object?>().ToList(),
                },
            },
        };

    private static BlueInstance Blueprint(OwnedBlueprint blueprint)
    {
        var item = blueprint.Item;
        return KeyVal(
            new()
            {
                ["itemID"] = item.Id,
                ["typeID"] = item.TypeId,
                ["ownerID"] = item.OwnerId,
                ["quantity"] = item.Singleton ? InventoryDefinitions.AssembledQuantity : item.Quantity,
                ["timeEfficiency"] = 0,
                ["materialEfficiency"] = 0,
                ["runs"] = -1,
                ["locationID"] = item.LocationId,
                ["locationTypeID"] = blueprint.Facility.TypeId,
                ["locationFlagID"] = item.FlagId,
                ["flagID"] = item.FlagId,
                ["facilityID"] = blueprint.Facility.Id,
                ["solarSystemID"] = blueprint.Facility.SystemId,
                ["jobID"] = blueprint.JobId,
                ["isImpounded"] = false,
            }
        );
    }

    private static BlueInstance Job(ManufacturingJob job) =>
        KeyVal(
            new()
            {
                ["jobID"] = job.Id,
                ["ownerID"] = job.PilotId,
                ["installerID"] = job.PilotId,
                ["blueprintID"] = job.BlueprintId,
                ["blueprintTypeID"] = job.BlueprintTypeId,
                ["blueprintCopy"] = false,
                ["blueprintLocationID"] = job.Facility.Id,
                ["blueprintLocationFlagID"] = (int)InventoryFlag.Hangar,
                ["facilityID"] = job.Facility.Id,
                ["stationID"] = job.Facility.Id,
                ["solarSystemID"] = job.Facility.SystemId,
                ["activityID"] = (int)IndustryActivity.Manufacturing,
                ["status"] = (int)job.Status,
                ["completedCharacterID"] = job.CompletedAt.HasValue ? job.PilotId : null,
                ["startDate"] = job.StartTime,
                ["endDate"] = job.EndTime,
                ["pauseDate"] = null,
                ["runs"] = job.Runs,
                ["licensedRuns"] = 0,
                ["successfulRuns"] = job.Status == IndustryJobStatus.Delivered ? job.Runs : null,
                ["cost"] = 0d,
                ["timeInSeconds"] = job.DurationSeconds,
                ["probability"] = 1d,
                ["productTypeID"] = job.ProductTypeId,
                ["optionalTypeID"] = null,
                ["optionalTypeID2"] = null,
                ["outputLocationID"] = job.Facility.Id,
                ["outputFlagID"] = (int)InventoryFlag.Hangar,
            }
        );

    private static object Location(IndustryFacility facility, long pilotId) =>
        new BlueNewObject(
            BlueNewObject.IndustryLocationClass,
            new Dictionary<string, object?>
            {
                ["itemID"] = facility.Id,
                ["typeID"] = facility.TypeId,
                ["ownerID"] = pilotId,
                ["flagID"] = (int)InventoryFlag.Hangar,
                ["canView"] = true,
                ["canTake"] = true,
                ["solarSystemID"] = facility.SystemId,
            }
        );

    private static Dictionary<string, object?> Values(object? value) =>
        value switch
        {
            Dictionary<string, object?> values => values,
            BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> values } => values,
            BlueNewObject { Name: BlueNewObject.IndustryLocationClass, State: Dictionary<string, object?> values } => values,
            _ => throw new InvalidDataException("Invalid manufacturing request data."),
        };

    private static long Number(Dictionary<string, object?> values, string key) =>
        values.GetValueOrDefault(key) is long number ? number : throw new InvalidDataException($"Missing manufacturing {key}.");

    private static int Integer(Dictionary<string, object?> values, string key) => checked((int)Number(values, key));

    private static bool IsTrue(object? value) => value is true or 1L;

    private int CheckLocation(PilotCallContext context, Dictionary<string, object?> request, string key)
    {
        var location = Values(request.GetValueOrDefault(key));
        int station = Integer(location, "itemID");
        if (
            Number(location, "ownerID") != context.Pilot.PilotId
            || Number(location, "flagID") != (int)InventoryFlag.Hangar
            || station != Integer(request, "facilityID")
        )
        {
            throw new InvalidDataException("Choose your current station's personal Item Hangar for manufacturing.");
        }
        industry.CheckStation(context.Pilot.PilotId, station);
        return station;
    }

    private object Connect(PilotCallContext context, object? request)
    {
        var values = Values(request);
        int station = CheckLocation(context, values, "inputLocation");
        _monitor = checked(++_nextMonitor);
        _station = station;
        _available = industry.Materials(context.Pilot.PilotId, station);
        return new object?[] { _monitor, Quantities(_available) };
    }

    private object Install(PilotCallContext context, object? request)
    {
        var values = Values(request);
        int station = CheckLocation(context, values, "inputLocation");
        _ = CheckLocation(context, values, "outputLocation");
        if (
            Number(values, "characterID") != context.Pilot.PilotId
            || Number(values, "activityID") != (int)IndustryActivity.Manufacturing
            || values.GetValueOrDefault("account") is not object?[] { Length: 2 } account
            || !Equals(account[0], context.Pilot.PilotId)
            || !Equals(account[1], (long)WalletAccountKey.Cash)
            || values.GetValueOrDefault("cost") is not (0L or 0d)
            || values.GetValueOrDefault("tax") is not (0L or 0d)
        )
        {
            throw new InvalidDataException("Use personal manufacturing with the current installation quote.");
        }
        var materials = values.GetValueOrDefault("materials") switch
        {
            Dictionary<long, object?> items when items.Values.All(v => v is long) => items.ToDictionary(
                i => checked((int)i.Key),
                i => checked((int)(long)i.Value!)
            ),
            _ => throw new InvalidDataException("Missing manufacturing material quote."),
        };
        double seconds = values.GetValueOrDefault("time") switch
        {
            long n => n,
            double n => n,
            _ => throw new InvalidDataException("Missing manufacturing time quote."),
        };
        // Job.dump leaves productTypeID as None for a single-output recipe.
        // The server resolves that product from the owned blueprint's catalog.
        int? product = values.TryGetValue("productTypeID", out var selectedProduct)
            ? selectedProduct switch
            {
                null => null,
                long type => checked((int)type),
                _ => throw new InvalidDataException("Invalid manufacturing product selection."),
            }
            : throw new InvalidDataException("Missing manufacturing product selection.");
        var result = industry.Install(
            context.Pilot.PilotId,
            new(
                Number(values, "blueprintID"),
                Integer(values, "blueprintTypeID"),
                station,
                Integer(values, "runs"),
                product,
                seconds,
                materials
            )
        );
        Notify(context, result);
        return result.Job.Id;
    }

    private static void Notify(PilotCallContext context, IndustryResult result)
    {
        foreach (var change in result.Changes)
        {
            InventoryHandler.NotifyItemChange(context, change);
        }
        var job = result.Job;
        context.Notifications.Add(
            (
                "OnIndustryJob",
                [
                    job.Id,
                    job.PilotId,
                    job.BlueprintId,
                    job.PilotId,
                    (int)job.Status,
                    job.Status == IndustryJobStatus.Delivered ? job.Runs : null,
                ]
            )
        );
        InventoryHandler.NotifyBlueprintsUpdated(context, job.PilotId);
    }

    public object? Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        // The native Industry button checks both factory and laboratory access
        // before opening its shared window. This does not enable research jobs.
        bool standing =
            service == "corpStationMgr"
            && method == "DoStandingCheckForStationService"
            && args is [(long)StationService.Manufacturing or (long)StationService.Laboratory];
        if (!standing && service is not ("industryManager" or "blueprintManager" or "facilityManager" or "industryMonitor"))
        {
            return PilotCallRouter.Unsupported;
        }
        try
        {
            long pilotId = context.Pilot.PilotId;
            if (standing)
            {
                industry.CheckAccess(pilotId, context.Pilot.StationId);
                return null;
            }
            switch (service, method)
            {
                case ("blueprintManager", "GetLimits") when args.Length == 0:
                    return new Dictionary<string, object?> { ["maxBlueprintResults"] = BlueprintResultLimit };
                case ("blueprintManager", "GetBlueprintData") when args is [long item]:
                    return Blueprint(industry.Blueprint(pilotId, item));
                case ("blueprintManager", "GetBlueprintDataByOwner") when args is [long owner, null or long] && owner == pilotId:
                    var blueprints = industry.Blueprints(pilotId, args[1] is long facility ? checked((int)facility) : null);
                    return new object?[]
                    {
                        blueprints.Take(BlueprintResultLimit).Select(b => (object?)Blueprint(b)).ToList(),
                        blueprints
                            .GroupBy(b => b.Facility.Id)
                            .ToDictionary(g => (long)g.Key, g => (object?)g.Sum(b => (long)b.Item.Quantity)),
                    };
                case ("facilityManager", "GetFacilities") when args.Length == 0:
                    return industry.Facilities(pilotId).Select(Facility).ToList();
                case ("facilityManager", "GetFacility") when args is [long station]:
                    return Facility(industry.Facility(pilotId, checked((int)station)));
                case ("facilityManager", "GetFacilityLocations") when args is [long station, long owner] && owner == pilotId:
                    industry.CheckStation(pilotId, checked((int)station));
                    return new List<object?> { Location(industry.Facility(pilotId, (int)station), pilotId) };
                case ("facilityManager", "GetMaxActivityModifiers") when args.Length == 0:
                    return new Dictionary<long, object?> { [(long)IndustryActivity.Manufacturing] = 1d };
                case ("industryManager", "GetJobsByOwner") when args is [long owner, var completed] && owner == pilotId:
                    return industry.Jobs(pilotId, IsTrue(completed)).Select(j => (object?)Job(j)).ToList();
                case ("industryManager", "GetJob") when args is [long job]:
                    return Job(industry.Job(pilotId, job));
                case ("industryManager", "GetJobCounts") when args is [long owner] && owner == pilotId:
                    return new Dictionary<long, object?> { [(long)IndustryActivity.Manufacturing] = industry.Jobs(pilotId, false).Count };
                case ("industryManager", "InstallJob") when args is [var request]:
                    return Install(context, request);
                case ("industryManager", "CompleteJob" or "CancelJob") when args is [long job, long system]:
                    var result =
                        method == "CompleteJob"
                            ? industry.Deliver(pilotId, job, checked((int)system))
                            : industry.Cancel(pilotId, job, checked((int)system));
                    Notify(context, result);
                    return null;
                case ("industryMonitor", "ConnectJob") when args is [var request]:
                    return Connect(context, request);
                case ("industryMonitor", "DisconnectJob") when args is [null] || args is [long monitor] && monitor == _monitor:
                    _station = null;
                    return null;
                default:
                    return PilotCallRouter.Unsupported;
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallRouter.Error("The manufacturing job exceeds the supported quantity or time limit.");
        }
    }
}
