using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.Fittings;
using Eden.Protocol.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Protocol.Structures;

namespace Eden.Protocol.Development;

[Injectable]
internal sealed class DeveloperCommandHandler(
    DeveloperTravelCommands travel,
    DeveloperItemCommands items,
    DeveloperSkillCommands skills,
    DeveloperStructureCommands structures,
    DeveloperExtractionCommands extractions,
    DeveloperFittingCommands fittings,
    DeveloperRepairCommands repairs
) : ISelectedPilotCallHandler
{
    private const int MaximumCommandLength = 128;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "slash" || method != "SlashCmd")
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (args is not [string or byte[]] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Supply one developer command without additional arguments.");
            }
            string text = BlueRead.Text(args[0]);
            if (text.Length > MaximumCommandLength || text.Contains('\0'))
            {
                throw new InvalidDataException("The developer command is too long or contains a null character.");
            }
            string[] command = DeveloperCommands.Read(text);
            if (command is ["/"])
            {
                return new PilotCallResult.NamedFailure("SlashError", DeveloperCommandList.For(context.Roles));
            }
            if (command.Length == 0 || !DeveloperCommands.Roles.TryGetValue(command[0], out var allowed))
            {
                throw new InvalidDataException("Unknown developer command. Use / to discover your available commands.");
            }
            if ((context.Roles & (long)allowed) == 0)
            {
                throw new InvalidDataException($"Your account cannot use {command[0]}.");
            }
            if (context.Flight.Translocating)
            {
                throw new InvalidDataException("Wait for the current translocation to finish.");
            }
            command[0] = command[0].ToLowerInvariant();
            switch (command[0])
            {
                case "/getpos":
                case "/reportdesync":
                case "/stop":
                case "/tr":
                case "/warptopoint":
                    return travel.Execute(context, command);
                case "/giveskill":
                case "/givestarterskills":
                case "/removeskill":
                case "/skillscopy":
                    return skills.Grant(context, command);
                case "/copyship":
                case "/fit":
                case "/getshipsetup":
                case "/online":
                case "/unload":
                    return fittings.Execute(context, command);
                case "/heal":
                case "/repairmodules":
                    return repairs.Execute(context, command);
                case "/structure":
                    return structures.Execute(context, command);
                case "/skyhook":
                    return structures.ExecuteSkyhook(context, command);
                case "/extraction":
                    return extractions.Execute(context, command);
                case "/create":
                case "/createitem":
                case "/load":
                    return items.Create(context, command);
            }
            return new PilotCallResult.Failure("The developer command has no handler.");
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (Eden.Gameplay.Skills.InvalidTrainingQueue error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The developer command exceeds the supported numeric range.");
        }
    }
}
