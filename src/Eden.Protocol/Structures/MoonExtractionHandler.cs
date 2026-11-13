using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class MoonExtractionHandler(IMoonExtractions extractions) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service is not ("moonExtractions" or "corpMiningLedger"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected moon extraction arguments.");
            }
            long pilot = context.Pilot.PilotId;
            if (service == "corpMiningLedger")
            {
                return (method, args) switch
                {
                    ("GetObserversWithMiningEvents", []) => new BlueList<MoonMiningObserverReply>(
                        extractions.Observers(pilot).Select(o => new MoonMiningObserverReply(o)).ToArray()
                    ),
                    ("GetObserverLedger", [long id]) => new BlueList<PersonalMiningLogReply>(
                        extractions.Ledger(pilot, id).Select(l => new PersonalMiningLogReply(l)).ToArray()
                    ),
                    _ => PilotCallResult.Unsupported,
                };
            }
            if (args.Length == 0)
            {
                args =
                    method == "GetExtractionsForCorp" ? [(long)context.Pilot.CorpId]
                    : context.Pilot.InStructure ? [context.Pilot.DockedLocationId]
                    : [];
            }
            switch (method, args)
            {
                case ("GetExtractionAndEventIDFromStructureID", [long structure]):
                    return new MoonExtractionWindow(extractions.Current(pilot, structure));
                case ("GetExtractionsForCorp", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return new BlueList<MoonExtractionReply>(
                        extractions.CorporationExtractions(pilot, (int)corporation).Select(e => new MoonExtractionReply(e)).ToArray()
                    );
                case ("GetMoonResourcesForStructure", [long structure]):
                    return new BlueMap<long, double>(
                        extractions.Resources(pilot, structure).ToDictionary(share => (long)share.TypeId, share => share.Percentage / 100d)
                    );
                case ("StartNewExtraction", [long structure, long duration, bool]):
                    var extraction = extractions.Start(pilot, structure, duration);
                    context.Notifications.Add(new MoonExtractionChanged(extraction));
                    return new MoonExtractionReply(extraction);
                case ("CancelExtraction", [long structure]):
                    extractions.Cancel(pilot, structure);
                    break;
                case ("FractureChunkWithStructure", [long structure]):
                    extractions.Fracture(pilot, structure);
                    break;
                default:
                    return PilotCallResult.Unsupported;
            }
            context.Notifications.Add(new MoonExtractionChanged(null));
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record MoonExtractionReply(MoonExtraction Extraction) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["extractionID"] = Extraction.Id,
                ["structureID"] = Extraction.StructureId,
                ["moonID"] = (long)Extraction.MoonId,
                ["solarSystemID"] = (long)Extraction.SystemId,
                ["ownerID"] = (long)Extraction.CorporationId,
                ["startMoveTime"] = Extraction.StartedAt,
                ["chunkAvailableTime"] = Extraction.ReadyAt,
                ["naturalDecayTime"] = Extraction.AutoFractureAt,
                ["durationSeconds"] = (Extraction.ReadyAt - Extraction.StartedAt) / TimeSpan.TicksPerSecond,
                ["yieldMultiplier"] = 1d,
            }
        );
}

internal sealed record MoonExtractionWindow(MoonExtraction? Extraction) : BlueModel
{
    public override object ToBlue() =>
        Extraction is null ? new object?[] { null, null } : new object?[] { new MoonExtractionReply(Extraction), 0L };
}

internal sealed record MoonExtractionChanged(MoonExtraction? Extraction) : PilotNotification("OnMoonExtractionUpdate")
{
    public override object?[] Arguments => [Extraction is null ? null : new MoonExtractionReply(Extraction)];
}

internal sealed record MoonMiningObserverReply(MoonMiningObserver Observer) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Observer.Id,
                ["itemName"] = Observer.Name,
                ["solarSystemID"] = (long)Observer.SystemId,
            }
        );
}
