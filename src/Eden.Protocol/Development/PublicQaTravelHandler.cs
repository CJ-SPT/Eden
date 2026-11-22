using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Development;

[Injectable]
internal sealed class PublicQaTravelHandler(IPilotTranslocation travel, DeveloperTravelCommands commands) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "publicQaToolsServer" || method != "MoveMeTo")
        {
            return PilotCallResult.Unsupported;
        }
        if (
            (context.Roles & (long)DeveloperCommands.WorldTools) == 0
            || context.ControlledStructureId.HasValue
            || context.Flight.Translocating
        )
        {
            return new PilotCallResult.Failure("Developer travel is unavailable for this pilot.");
        }
        try
        {
            if (args is not [string or byte[]] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Supply one solar system name.");
            }
            string name = BlueRead.Text(args[0]);
            if (name.Length is < 1 or > 64 || name.Contains('\0'))
            {
                throw new InvalidDataException("Supply one solar system name.");
            }
            return commands.Move(context, travel.ResolveSystem(name));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
