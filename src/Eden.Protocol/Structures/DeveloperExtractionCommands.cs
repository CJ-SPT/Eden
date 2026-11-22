using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class DeveloperExtractionCommands(IMoonExtractionGameMaster extractions)
{
    internal PilotCallResult Execute(PilotCallContext context, string[] command)
    {
        if (command is not [_, var action, var value] || !action.Equals("advance", StringComparison.OrdinalIgnoreCase))
        {
            throw Usage();
        }
        var phase = value.ToLowerInvariant() switch
        {
            "mid" => MoonExtractionPhase.Midway,
            "stop" => MoonExtractionPhase.Stopped,
            "ready" => MoonExtractionPhase.Ready,
            "decay" => MoonExtractionPhase.Decay,
            _ => throw Usage(),
        };
        long structureId =
            context.ControlledStructureId
            ?? (context.Pilot.InStructure ? context.Pilot.DockedLocationId : throw new InvalidDataException("Dock at an active refinery."));
        var extraction = extractions.AdvanceExtraction(context.Pilot.PilotId, structureId, phase);
        context.Notifications.Add(new MoonExtractionChanged(extraction));
        return PilotCallResult.None;
    }

    private static InvalidDataException Usage() => new("Use /extraction advance mid, stop, ready, or decay.");
}
