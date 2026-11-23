using Eden.Gameplay.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record EpicArcJournalChanged() : PilotNotification("OnEpicJournalChange")
{
    public override object?[] Arguments => [];
}

internal sealed record EpicArcStatusReply(IReadOnlyList<EpicArcProgress> Status) : BlueModel
{
    public override object ToBlue() =>
        Status
            .GroupBy(stage => stage.ArcId)
            .ToDictionary(
                arc => (long)arc.Key,
                arc => (object?)arc.ToDictionary(stage => (long)stage.MissionId, stage => (object?)new EpicArcStageStatus(stage))
            );
}

internal sealed record EpicArcStageStatus(EpicArcProgress Stage) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["acceptedDate"] = Stage.AcceptedAt,
                ["completedDate"] = Stage.CompletedAt,
                ["quitDate"] = Stage.QuitAt,
                ["nameID"] = Stage.NameId > 0 ? Stage.NameId : null,
            }
        );
}

internal sealed record EpicArcMessagesReply(IReadOnlyList<EpicArcMessage> Messages) : BlueModel
{
    public override object ToBlue() =>
        Messages
            .GroupBy(message => message.Kind)
            .ToDictionary(
                kind => kind.Key,
                kind => (object?)kind.ToDictionary(message => (long)message.MissionId, message => (object?)message.MessageId)
            );
}

internal sealed record CachedEpicArcMessages(IReadOnlyList<EpicArcMessage> Messages) : BlueModel
{
    public override object ToBlue()
    {
        // The native journal looks up each node's messages independently during reconstruction.
        byte[] payload = BlueMarshal.Encode(new EpicArcMessagesReply(Messages));
        return new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "never" },
                payload,
                new object?[] { DateTime.UnixEpoch.ToFileTimeUtc(), CachedMethodPayload.Checksum(payload) },
            }
        );
    }
}

internal readonly record struct EpicArcChoiceAction(long OfferId, int MissionId)
{
    // Build 3662168 appConst.agentDialogueButtonAcceptChoice reserves this action number.
    private const int AcceptChoice = 4;
    private const long MissionIdStride = (long)int.MaxValue + 1;

    internal long Token => checked((OfferId * MissionIdStride + MissionId) * MissionRules.ActionStride + AcceptChoice);

    internal static EpicArcChoiceAction? Read(long token)
    {
        if (token <= 0 || token % MissionRules.ActionStride != AcceptChoice)
        {
            return null;
        }
        long parts = token / MissionRules.ActionStride;
        long offer = parts / MissionIdStride;
        int mission = (int)(parts % MissionIdStride);
        return offer > 0 && mission > 0 ? new(offer, mission) : null;
    }
}

internal sealed record EpicArcChoiceLink(MissionOffer Offer, EpicArcChoice Choice) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new EpicArcChoiceAction(Choice.OfferId, Choice.MissionId).Token,
            // A dictionary in the second slot is rendered as a special-interaction link.
            new Dictionary<string, object?>
            {
                ["ContentID"] = Choice.MissionId,
                ["Mission Title ID"] = Choice.NameId > 0 ? Choice.NameId : Choice.Name,
                ["Mission Briefing ID"] = Choice.BriefingId > 0 ? Choice.BriefingId : null,
                ["Mission Keywords"] = new MissionKeywords(Offer),
            },
        };
}

internal sealed record EpicArcAgentLocation(long AgentId, long LocationId, int TypeId, int SystemId) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["agentID"] = AgentId,
            ["locationID"] = LocationId,
            ["typeID"] = TypeId,
            ["solarsystemID"] = SystemId,
            ["locationType"] = "objective.destination",
            ["locationNumber"] = 0,
        };
}

internal sealed record EpicArcHandoffBookmark(MissionOffer Offer, string LocationType) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentID"] = Offer.Agent.Id,
                ["bookmarkID"] = MissionRules.FirstSiteId + Offer.Id,
                ["locationID"] = Offer.EpicArcTargetSystem,
                ["itemID"] = Offer.EpicArcTargetStation is int station ? station : Offer.EpicArcTargetAgentId,
                ["typeID"] = Offer.EpicArcTargetTypeId,
                ["locationType"] = LocationType,
                ["locationNumber"] = 0,
                ["solarsystemID"] = Offer.EpicArcTargetSystem,
                ["hint"] = Offer.Name,
                ["memo"] = Offer.Name,
                ["x"] = Offer.EpicArcTargetPosition?.X ?? 0,
                ["y"] = Offer.EpicArcTargetPosition?.Y ?? 0,
                ["z"] = Offer.EpicArcTargetPosition?.Z ?? 0,
            }
        );
}
