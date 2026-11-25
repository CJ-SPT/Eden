using Eden.Gameplay.Corporations;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

internal sealed record CorporationWarReply(CorporationWar War) : BlueModel
{
    // evewar.const war-start reasons in build 3662168.
    private const int PlayerDeclared = 1;
    private const int Inherited = 3;

    public override object ToBlue() => new BlueInstance("utillib.KeyVal", Fields(War));

    internal static Dictionary<string, object?> Fields(CorporationWar war) =>
        new()
        {
            ["warID"] = war.Id,
            ["declaredByID"] = war.AttackerId,
            ["againstID"] = war.DefenderId,
            ["warHQ"] = war.HeadquartersId,
            ["warHQID"] = war.HeadquartersId,
            ["timeDeclared"] = war.DeclaredAt,
            ["timeStarted"] = war.StartsAt,
            ["timeFinished"] = war.EndsAt,
            ["retracted"] = war.RetractedAt,
            ["retractedBy"] = war.RetractedBy,
            ["billID"] = war.BillId,
            ["mutual"] = war.Mutual,
            ["openForAllies"] = false,
            ["canBeRetracted"] = war.CanBeRetracted,
            ["reasonEnded"] = (int?)war.ReasonEnded,
            ["reasonStarted"] = war.InheritedFromWarId.HasValue ? Inherited : PlayerDeclared,
            ["noOfAllies"] = 0,
            ["createdFromWarID"] = war.InheritedFromWarId,
            ["allies"] = BlueEmpty.IndexedDictionary,
        };
}

internal sealed record CorporationWarHistoryReply(IReadOnlyList<CorporationWar> Wars) : BlueModel
{
    private static readonly (string Name, BlueColumnType Type)[] s_columns =
    [
        ("warID", BlueColumnType.Int32),
        ("declaredByID", BlueColumnType.Int32),
        ("againstID", BlueColumnType.Int32),
        ("timeDeclared", BlueColumnType.FileTime),
        ("timeFinished", BlueColumnType.FileTime),
        ("retracted", BlueColumnType.FileTime),
        ("retractedBy", BlueColumnType.Int32),
        ("timeStarted", BlueColumnType.FileTime),
        ("billID", BlueColumnType.Int32),
        ("mutual", BlueColumnType.Boolean),
        ("createdFromWarID", BlueColumnType.Int32),
        ("openForAllies", BlueColumnType.Boolean),
        ("canBeRetracted", BlueColumnType.Boolean),
        ("reasonEnded", BlueColumnType.UInt8),
        ("warHQ", BlueColumnType.Int64),
        ("noOfAllies", BlueColumnType.Int32),
        ("reasonStarted", BlueColumnType.UInt8),
    ];

    public override object ToBlue()
    {
        byte[] payload = BlueMarshal.Encode(Rows());
        return new BlueInstance(
            "carbon.common.script.net.objectCaching.CachedMethodCallResult",
            new object?[]
            {
                new Dictionary<string, object?> { ["versionCheck"] = "15 minutes" },
                payload,
                new object?[] { DateTime.UnixEpoch.ToFileTimeUtc(), CachedMethodPayload.Checksum(payload) },
            }
        );
    }

    internal BlueConstructor Rows()
    {
        var header = BlueRows.Descriptor(s_columns);
        var rows = Wars.Select(war =>
            {
                var fields = CorporationWarReply.Fields(war);
                return (object?)new BlueConstructor("blue.DBRow", [header, s_columns.Select(column => fields[column.Name]).ToArray()]);
            })
            .ToList();
        return new("carbon.common.script.sys.crowset.CRowset", [header, rows]);
    }
}

internal sealed record CorporationWarHeadquartersReply(CorporationWarHeadquarters Headquarters) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["typeID"] = Headquarters.TypeId,
                ["structureID"] = Headquarters.StructureId,
                ["upkeepState"] = (int)Headquarters.UpkeepState,
                ["wars"] = new BlueList<long>(Headquarters.Wars),
                ["ownerID"] = Headquarters.OwnerId,
                ["solarSystemID"] = Headquarters.SolarSystemId,
                ["itemName"] = new BlueUnicode(Headquarters.Name),
                ["inSpace"] = true,
            }
        );
}

internal sealed record CorporationWarInvitationReply(CorporationWarInvitation Invitation) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["fromOwnerID"] = Invitation.FromOwnerId,
                ["toOwnerID"] = Invitation.ToOwnerId,
                ["sentDate"] = Invitation.SentAt,
            }
        );
}

internal sealed record CorporationWarOwnerReply(int Id, string Name, bool Alliance, bool Eligible) : BlueModel
{
    // Group identities consumed by the pinned owner lookup.
    private const int CorporationType = 2;
    private const int AllianceType = 16159;

    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["ownerID"] = Id,
                ["ownerName"] = new BlueUnicode(Name),
                ["typeID"] = Alliance ? AllianceType : CorporationType,
                ["warPermit"] = Eligible,
            }
        );
}

internal sealed record CorporationWarPeaceTreatiesReply(IReadOnlyList<CorporationWarPeaceTreaty> Treaties) : BlueModel
{
    // The first implemented peace-treaty reason is PEACE_REASON_HQ_REMOVED in evewar.const.
    private const int HeadquartersRemoved = 1;

    public override object ToBlue()
    {
        var header = BlueRows.Descriptor([
            ("otherOwnerID", BlueColumnType.Int32),
            ("expiryDate", BlueColumnType.FileTime),
            ("peaceReason", BlueColumnType.UInt8),
            ("warID", BlueColumnType.Int32),
            ("reasonEnded", BlueColumnType.UInt8),
            ("warHQ", BlueColumnType.Int64),
        ]);
        List<object?> outgoing = [];
        List<object?> incoming = [];
        foreach (var treaty in Treaties)
        {
            var row = new BlueConstructor(
                "blue.DBRow",
                [
                    header,
                    new object?[]
                    {
                        treaty.OtherOwnerId,
                        treaty.ExpiresAt,
                        HeadquartersRemoved,
                        treaty.WarId,
                        (int)CorporationWarEndReason.HeadquartersLost,
                        treaty.HeadquartersId,
                    },
                ]
            );
            (treaty.Outgoing ? outgoing : incoming).Add(row);
        }
        return new object?[]
        {
            new BlueConstructor("carbon.common.script.sys.crowset.CRowset", [header, outgoing]),
            new BlueConstructor("carbon.common.script.sys.crowset.CRowset", [header, incoming]),
        };
    }
}
