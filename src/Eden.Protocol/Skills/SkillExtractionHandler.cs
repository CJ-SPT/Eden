using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable(InjectionType.Scoped)]
internal sealed class SkillExtractionHandler(IPilotSkillExtraction extraction, SkillHandler skills, TimeProvider clock)
    : ISelectedPilotCallHandler
{
    private long? _pilot;
    private long _revision;
    private long _nextPoll;
    public bool IsMonitoring => _pilot.HasValue;

    public void Enter(PilotCallContext context)
    {
        if (_pilot != context.Pilot.PilotId)
        {
            _pilot = context.Pilot.PilotId;
            _revision = extraction.Revision(_pilot.Value);
            _nextPoll = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        }
    }

    public void Reset() => _pilot = null;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Enter(context);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _nextPoll)
        {
            return;
        }
        if (extraction.Revision(context.Pilot.PilotId) != _revision)
        {
            var state = extraction.Snapshot(context.Pilot.PilotId);
            skills.Apply(context, state.Training, true, removed: true);
            _revision = state.Revision;
        }
        _nextPoll = checked(now + TimeSpan.TicksPerSecond);
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "@skills" || method != "ExtractSkills")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (
                keywords is { Count: > 0 }
                || args is not [Dictionary<long, object?> selected, long item, var token]
                || item <= 0
                || token is not (string or byte[])
            )
            {
                throw new InvalidDataException("Invalid skill extraction request.");
            }
            var requests = selected.Select(pair => new SkillExtractionRequest(Type(pair.Key), Points(pair.Value))).ToArray();
            Enter(context);
            // The third argument is the stock client's opaque SSO token. Eden
            // authorizes this operation through its authenticated selected session;
            // the token is neither executed, retained nor sent to an external service.
            var result = extraction.Extract(context.Pilot.PilotId, item, requests);
            foreach (var change in result.Changes)
            {
                InventoryChangesWire.NotifyItemChange(context, change);
            }
            skills.Apply(context, result.Training, true, removed: true);
            _revision = result.Revision;
            _nextPoll = checked(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond);
            return PilotCallResult.None;
        }
        catch (InvalidSkillExtraction error)
        {
            return error.Message is "SkillExtractionNotEnoughSP" or "SkillExtractorNotDockedInStation"
                ? new PilotCallResult.NamedFailure(
                    error.Message,
                    new SkillExtractionErrorValues(error.Message == "SkillExtractionNotEnoughSP")
                )
                : new PilotCallResult.Failure(error.Message);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("This skill extraction amount is too large.");
        }
    }

    private static int Type(long value) =>
        value is > 0 and <= int.MaxValue ? (int)value : throw new InvalidDataException("Invalid extraction skill.");

    private static long Points(object? value) =>
        value switch
        {
            long points when points is > 0 and <= SkillExtractionRules.Points => points,
            double points
                when double.IsFinite(points) && points > 0 && points <= SkillExtractionRules.Points && points == Math.Truncate(points) =>
                (long)points,
            _ => throw new InvalidDataException("Select a whole number of skill points to extract."),
        };
}

internal sealed record SkillExtractionErrorValues(bool Minimum) : BlueModel
{
    public override object ToBlue()
    {
        Dictionary<string, object?> values = new() { ["extractor"] = SkillExtractionRules.ExtractorType };
        if (Minimum)
        {
            values["limit"] = SkillExtractionRules.RemainingMinimum + SkillExtractionRules.Points;
        }
        return values;
    }
}
