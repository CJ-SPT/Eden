using System.Globalization;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Structures;

[Injectable]
internal sealed class DeveloperStructureCommands(IStructureGameMaster structures, IStructureContacts contacts)
{
    internal PilotCallResult Execute(PilotCallContext context, string[] tokens)
    {
        // Build 3662168 structuremenu.GetStructureMenu checks HasGMHRole before exposing these actions.
        if ((context.Roles & (long)PilotRole.GameMasterHigh) == 0)
        {
            throw new InvalidDataException("Structure GM commands require the high game-master role.");
        }
        if (
            tokens.Length is < 3 or > 4
            || !long.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out long id)
            || id <= 0
        )
        {
            throw Usage();
        }

        string? value = tokens.Length == 4 ? tokens[3].ToLowerInvariant() : null;
        StructureGameMasterCommand command = (tokens[1].ToLowerInvariant(), value) switch
        {
            ("state", not null) => new StructureGameMasterCommand.SetState(State(value)),
            ("timer", not null) => new StructureGameMasterCommand.SetTimer(Remaining(value)),
            ("deploytimer", not null) => new StructureGameMasterCommand.SetDeployTimer(Remaining(value)),
            ("unanchor", null) => new StructureGameMasterCommand.StartUnanchoring(),
            ("unanchor", "cancel") => new StructureGameMasterCommand.CancelUnanchoring(),
            ("unanchor", not null) => new StructureGameMasterCommand.SetUnanchoringTimer(Remaining(value)),
            ("abandontimer", not null) => new StructureGameMasterCommand.SetAbandonTimer(Remaining(value)),
            _ => throw Usage(),
        };
        structures.Execute(id, command);
        if (context.Flight.InBallpark)
        {
            context.Flight.RefreshStructures(contacts.Contacts(context.Pilot.SystemId));
        }
        return PilotCallResult.None;
    }

    private static StructureState State(string value)
    {
        var state = value switch
        {
            "unknown" => StructureState.Unknown,
            "unanchored" => StructureState.Unanchored,
            "anchoring" => StructureState.Anchoring,
            "online_deprecated" => StructureState.OnlineDeprecated,
            "fitting_invulnerable" => StructureState.Fitting,
            "onlining_vulnerable" => StructureState.Onlining,
            "shield_vulnerable" => StructureState.ShieldVulnerable,
            "armor_reinforce" => StructureState.ArmorReinforced,
            "armor_vulnerable" => StructureState.ArmorVulnerable,
            "hull_reinforce" => StructureState.HullReinforced,
            "hull_vulnerable" => StructureState.HullVulnerable,
            "anchor_vulnerable" => StructureState.AnchorVulnerable,
            "deploy_vulnerable" => StructureState.DeployVulnerable,
            "fob_invulnerable" => StructureState.FobInvulnerable,
            _ when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) => (StructureState)id,
            _ => throw new InvalidDataException("Unknown structure state."),
        };
        if (!Enum.IsDefined(state) || state == StructureState.Destroyed)
        {
            throw new InvalidDataException("Unknown structure state.");
        }
        return state;
    }

    private static TimeSpan Remaining(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
            ? TimeSpan.FromTicks(checked(seconds * TimeSpan.TicksPerSecond))
            : throw new InvalidDataException("Supply a non-negative whole number of seconds.");

    private static InvalidDataException Usage() =>
        new("Use /structure state, timer, deploytimer, unanchor, or abandontimer, followed by the structure ID and any value.");
}
