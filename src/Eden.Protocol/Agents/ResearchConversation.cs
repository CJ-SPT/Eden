using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

// Build 3662168 appConst.agentDialogueButton{StartResearch,CancelResearch,BuyDatacores}.
internal enum ResearchAction
{
    Start = 12,
    Cancel = 13,
    Buy = 14,
}

internal sealed record ResearchConversation(ResearchProject? Project, string Message) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new object?[]
            {
                new object?[] { Message, null },
                Project is null
                    ? new List<object?> { new object?[] { (int)ResearchAction.Start, (int)ResearchAction.Start } }
                    : new List<object?>
                    {
                        new object?[] { (int)ResearchAction.Buy, (int)ResearchAction.Buy },
                        new object?[] { (int)ResearchAction.Cancel, (int)ResearchAction.Cancel },
                    },
            },
            new Dictionary<string, object?>
            {
                ["missionCompleted"] = false,
                ["missionDeclined"] = false,
                ["missionQuit"] = false,
                ["missionCantReplay"] = false,
                ["loyaltyPoints"] = 0,
            },
        };
}

internal sealed record ResearchDialog(
    string CallMethod,
    object?[] Arguments,
    Dictionary<string, object?> Keywords,
    Func<object?, PilotCallResult> Resolve
) : PilotClientDialog
{
    internal override string Method => CallMethod;

    public override object ToBlue() => new object?[] { 1, Method, Arguments, Keywords };

    internal override PilotCallResult Complete(object? answer)
    {
        try
        {
            return Resolve(answer);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("Datacore quantity exceeds the supported limit.");
        }
    }

    internal static int? Choice(object? answer, int count)
    {
        if (answer is not object?[] { Length: 2 } values || values[0] is not true || values[1] is not (string or byte[]))
        {
            return null;
        }
        string selection = BlueMarshal.Text(values[1]);
        const string Prefix = "radioboxOption";
        const string Suffix = "Selected";
        return
            selection.StartsWith(Prefix, StringComparison.Ordinal)
            && selection.EndsWith(Suffix, StringComparison.Ordinal)
            && int.TryParse(
                selection.AsSpan(Prefix.Length, selection.Length - Prefix.Length - Suffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int index
            )
            && index > 0
            && index <= count
            ? index - 1
            : null;
    }
}

internal sealed record ResearchJournalRow(ResearchProject Project) : BlueModel
{
    // Pinned browserResearch.BrowserResearch._MakeResearchEntry unpacks seven fields.
    public override object ToBlue() =>
        new object?[]
        {
            Project.Agent.Id,
            Project.SkillId,
            Project.PointsPerDay,
            Project.Points,
            Project.Agent.Level,
            AgentDirectoryRow.RetiredAgentQuality,
            Project.Agent.StationId,
        };
}

internal sealed record ResearchChanged(string Action) : PilotNotification("OnAgentMissionChange")
{
    // Pinned journal._UpdateMissionDataPartial removes mission rows only, then
    // appends research rows. A full invalidation avoids duplicate/stale research.
    public override object?[] Arguments => [Action, null];
}
