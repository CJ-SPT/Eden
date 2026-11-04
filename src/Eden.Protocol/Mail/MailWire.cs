using System.Globalization;
using System.IO.Compression;
using System.Text;
using Eden.Gameplay.Mail;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Mail;

internal sealed record MailHeaderReply(MailHeader Header) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["messageID"] = Header.Id,
                ["senderID"] = Header.SenderId,
                ["sentDate"] = Header.SentAt,
                ["toCharacterIDs"] = string.Join(',', Header.Recipients.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                ["toListID"] = null,
                ["toCorpOrAllianceID"] = null,
                ["title"] = new BlueUnicode(Header.Subject),
            }
        );
}

internal sealed record MailStateReply(MailEntryState State) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["messageID"] = State.Id,
                ["statusMask"] = (int)State.Status,
                ["labelMask"] = State.Labels,
            }
        );
}

internal sealed record MailSyncReply(MailSnapshot Snapshot) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["newMail"] = new BlueList<MailHeaderReply>(Snapshot.New.Select(h => new MailHeaderReply(h)).ToArray()),
                ["oldMail"] = new BlueList<MailHeaderReply>(Snapshot.Old.Select(h => new MailHeaderReply(h)).ToArray()),
                ["mailStatus"] = new BlueList<MailStateReply>(Snapshot.Status.Select(s => new MailStateReply(s)).ToArray()),
            }
        );
}

internal sealed record MailLabelReply(MailLabel Label) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["labelID"] = Label.Id,
                ["name"] = new BlueUnicode(Label.Name),
                ["color"] = Label.Color,
            }
        );
}

internal sealed record MailBodyReply(string Body) : BlueModel
{
    public override object ToBlue()
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(Encoding.UTF8.GetBytes(Body));
        }
        return output.ToArray();
    }
}

internal sealed record MailSent(MailHeader Header, MailEntryState State) : PilotNotification("OnMailSent")
{
    public override object?[] Arguments =>
        [
            Header.Id,
            Header.SenderId,
            Header.SentAt,
            new BlueList<long>(Header.Recipients),
            null,
            null,
            new BlueUnicode(Header.Subject),
            (int)State.Status,
            State.Labels,
        ];
}

internal sealed record MailStateChanged(MailEntryState State) : PilotNotification("OnMailUpdatedByExternal")
{
    public override object?[] Arguments => [new BlueList<long>([State.Id]), State.Status.HasFlag(MailStatus.Read), State.Labels];
}

internal sealed record MailTrashed(long Id) : PilotNotification("OnMailTrashed")
{
    public override object?[] Arguments => [new BlueList<long>([Id])];
}

internal sealed record MailRestored(long Id) : PilotNotification("OnMailRestored")
{
    public override object?[] Arguments => [new BlueList<long>([Id])];
}

internal sealed record MailDeleted(long Id) : PilotNotification("OnMailDeleted")
{
    public override object?[] Arguments => [new BlueList<long>([Id])];
}

internal sealed record MailLabelChanged(MailLabel Label) : PilotNotification("OnLabelsCreatedByExternal")
{
    public override object?[] Arguments => [new MailLabelReply(Label)];
}
