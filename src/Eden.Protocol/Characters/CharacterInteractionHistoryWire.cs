using System.Globalization;
using System.Xml.Linq;
using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal sealed record ShipCombatHistoryReply(IReadOnlyList<ShipCombatReport> Reports) : BlueModel
{
    public override object ToBlue() => Reports.Select(r => (object?)new ShipCombatReportRow(r)).ToList();
}

internal sealed record ShipCombatReportRow(ShipCombatReport Report) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["killID"] = Report.Id,
                ["killTime"] = Report.Time,
                ["solarSystemID"] = Report.SystemId,
                ["moonID"] = null,
                ["victimCharacterID"] = Report.Victim.CharacterId,
                ["victimCorporationID"] = Report.Victim.CorporationId,
                ["victimAllianceID"] = null,
                ["victimFactionID"] = Report.Victim.FactionId,
                ["victimShipTypeID"] = Report.Victim.ShipTypeId,
                ["victimDamageTaken"] = Report.Victim.Damage,
                ["finalCharacterID"] = Report.FinalBlow.CharacterId,
                ["finalCorporationID"] = Report.FinalBlow.CorporationId,
                ["finalAllianceID"] = null,
                ["finalFactionID"] = Report.FinalBlow.FactionId,
                ["finalShipTypeID"] = Report.FinalBlow.ShipTypeId,
                ["finalWeaponTypeID"] = Report.FinalBlow.WeaponTypeId,
                ["finalSecurityStatus"] = Report.FinalBlow.SecurityStatus ?? 0,
                ["finalDamageDone"] = Report.FinalBlow.Damage,
                ["killBlob"] = KillBlob(),
                ["warID"] = null,
                ["iskLost"] = null,
                ["iskDestroyed"] = null,
                ["bountyClaimed"] = null,
                ["loyaltyPoints"] = null,
                ["killRightSupplied"] = null,
            }
        );

    private string KillBlob()
    {
        // The pinned eveCommonUtils parser wraps this fragment in <doc>.
        // It creates the final attacker from the fields above separately.
        var attackers = new XElement(
            "attackers",
            Report.OtherAttackers.Select(a => new XElement(
                "a",
                Attribute("c", a.CharacterId),
                Attribute("r", a.CorporationId),
                Attribute("f", a.FactionId),
                Attribute("s", a.ShipTypeId),
                Attribute("w", a.WeaponTypeId),
                Attribute("d", a.Damage),
                Attribute("t", a.SecurityStatus ?? 0)
            ))
        );
        var items = new XElement("items", Report.Items.Select(Item));
        return attackers.ToString(SaveOptions.DisableFormatting) + items.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement Item(CombatLostItem item) =>
        new(
            "i",
            Attribute("t", item.TypeId),
            Attribute("f", item.Flag),
            Attribute("s", item.Singleton ? 1 : 0),
            Attribute("d", item.Dropped),
            Attribute("x", item.Destroyed),
            item.Contents.Select(Item)
        );

    private static XAttribute? Attribute(string name, object? value) =>
        value is null ? null : new(name, Convert.ToString(value, CultureInfo.InvariantCulture)!);
}

internal sealed record StandingTransactionHistoryReply(IReadOnlyList<StandingTransaction> Transactions) : BlueModel
{
    public override object ToBlue() => Transactions.Select(t => (object?)new StandingTransactionRow(t)).ToList();
}

internal sealed record StandingTransactionRow(StandingTransaction Transaction) : BlueModel
{
    private const double NativeStandingScale = 10;

    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["eventID"] = Transaction.Id,
                ["eventDateTime"] = Transaction.Time,
                ["eventTypeID"] = (int)Transaction.Event,
                ["fromID"] = Transaction.SourceId,
                ["toID"] = Transaction.PilotId,
                // Standing.GetStandingTransactions multiplies by ten before the
                // graph subtracts changes from the current standing value.
                ["modification"] = (Transaction.After - Transaction.Before) / NativeStandingScale,
                ["int_1"] = Transaction.VictimCorporationId,
                ["int_2"] = Transaction.SystemId,
                ["int_3"] = Transaction.VictimTypeId,
                ["msg"] = null,
            }
        );
}

internal sealed record CharacterStandingsReply(long PilotId, IReadOnlyList<NpcStanding> Standings) : BlueModel
{
    public override object ToBlue() =>
        new BlueMap<long, CharacterStandingRow>(
            Standings.ToDictionary(s => s.SourceId, s => new CharacterStandingRow(PilotId, s))
        ).ToBlue();
}

internal sealed record CharacterStandingRow(long PilotId, NpcStanding Standing) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["fromID"] = Standing.SourceId,
                ["toID"] = PilotId,
                ["standing"] = Standing.Value,
            }
        );
}

internal sealed record CombatHistoryChanged() : PilotNotification("OnKillNotification")
{
    public override object?[] Arguments => [];
}

internal sealed record NpcStandingSet(long SourceId, long PilotId, double Value) : PilotNotification("OnStandingSet")
{
    public override object?[] Arguments => [SourceId, PilotId, Value];
}
