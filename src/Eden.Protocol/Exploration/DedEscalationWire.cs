using Eden.Gameplay.Exploration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

internal sealed record DedEscalationJournal(IReadOnlyList<DedEscalation> Escalations) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["instanceID", "dungeon", "destDungeon", "srcDungeon", "expiryTime", "journalEntryTemplateID"],
            Escalations.Select(e => new DedEscalationJournalRow(e)).ToArray()
        ).ToBlue();
}

internal sealed record DedEscalationJournalRow(DedEscalation Escalation) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Escalation.InstanceId,
            new BlueInstance(
                "utillib.KeyVal",
                new Dictionary<string, object?>
                {
                    ["instanceID"] = Escalation.InstanceId,
                    ["destDungeonID"] = Escalation.DungeonId,
                    ["srcDungeonID"] = Escalation.SourceDungeonId,
                    ["solarSystemID"] = Escalation.SystemId,
                }
            ),
            new BlueInstance(
                "utillib.KeyVal",
                new Dictionary<string, object?>
                {
                    ["dungeonID"] = Escalation.DungeonId,
                    ["dungeonNameID"] = Escalation.NameId,
                    ["descriptionID"] = Escalation.DescriptionId,
                    ["difficulty"] = Escalation.Difficulty,
                    ["factionID"] = Escalation.FactionId,
                }
            ),
            null,
            Escalation.ExpiresAt,
            Escalation.DescriptionId,
        };
}

internal sealed record EscalatingPathMessage(long InstanceId) : PilotNotification("OnEscalatingPathMessage")
{
    public override object?[] Arguments => [InstanceId];
}
