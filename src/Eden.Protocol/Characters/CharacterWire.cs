using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.Characters;

internal sealed record CharacterBiographyReply(string Biography) : BlueModel
{
    public override object ToBlue() => new BlueUnicode(Biography);
}

// Appearance schemas are client-defined, validated inert data stored as Blue bytes.
internal sealed record CharacterAppearanceReply(byte[] Data) : BlueModel
{
    public override object? ToBlue() => BlueMarshal.Decode(Data);
}

internal sealed record CharacterPortraitReply(byte[]? Data) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Data is null
                ? new List<object?>()
                : new List<object?> { BlueMarshal.Decode(Data) },
            null,
        };
}

internal sealed record PublicCharacterInfo(PublicCharacterProfile Pilot) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Pilot.CharacterId,
                ["characterName"] = new BlueUnicode(Pilot.Name),
                ["corporationID"] = Pilot.CorporationId,
                ["raceID"] = Pilot.RaceId,
                ["bloodlineID"] = Pilot.BloodlineId,
                ["gender"] = Pilot.GenderId,
                ["schoolID"] = Pilot.SchoolId,
                ["ancestryID"] = Pilot.AncestryId,
                ["createDateTime"] = Pilot.CreatedAt,
            }
        );
}

internal sealed record PublicCharacterInfo3(PublicCharacterProfile Profile) : BlueModel
{
    // Build 3662168 CharacterInfoWindow indexes row zero before reading these fields.
    public override object ToBlue() =>
        new CompatibilityRowset(
            [
                "characterID",
                "characterName",
                "corporationID",
                "raceID",
                "bloodlineID",
                "gender",
                "schoolID",
                "ancestryID",
                "startDateTime",
                "militiaFactionID",
                "description",
            ],
            [new PublicCharacterInfoRow(Profile)]
        ).ToBlue();
}

internal sealed record PublicCharacterInfoRow(PublicCharacterProfile Profile) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Profile.CharacterId,
            new BlueUnicode(Profile.Name),
            Profile.CorporationId,
            Profile.RaceId,
            Profile.BloodlineId,
            Profile.GenderId,
            Profile.SchoolId,
            Profile.AncestryId,
            Profile.CreatedAt,
            // CharacterInfoWindow reads this on the public row before constructing any tabs.
            null,
            new BlueUnicode(Profile.Biography),
        };
}

internal sealed record PublicCharacterMedals : BlueModel
{
    // Build 3662168 medals.GetMedalsReceived unpacks info and graphics rowsets even when empty.
    public override object ToBlue() =>
        new object?[] { new CompatibilityRowset(["medalID", "status"], []), new CompatibilityRowset(["medalID"], []) };
}

internal sealed record PublicCharacterEmployment(PublicCharacterProfile Profile, IReadOnlyList<CorporationEmployment>? Employment = null)
    : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new BlueList<CharacterEmploymentRow>(
                Employment is not null ? Employment.Select(e => new CharacterEmploymentRow(e.CorporationId, e.StartedAt)).ToArray()
                : Profile.CreatedAt is long born ? [new(Profile.CorporationId, born)]
                : []
            ),
            new List<object?>(),
        };
}

internal sealed record CharacterEmploymentRow(int CorporationId, long StartedAt) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["corporationID"] = CorporationId,
                ["startDate"] = StartedAt,
                ["deleted"] = false,
            }
        );
}

internal sealed record CharacterCorporationInfo(int CorporationId) : BlueModel
{
    // NPC starter corporations have no alliance, militia membership or player titles.
    private const int CorporationTitleCount = 16;

    public override object ToBlue()
    {
        Dictionary<string, object?> fields = new()
        {
            ["corpID"] = CorporationId,
            ["allianceID"] = null,
            ["militiaFactionID"] = null,
            ["title"] = new BlueUnicode(""),
        };
        for (int index = 1; index <= CorporationTitleCount; index++)
        {
            fields[$"title{index}"] = null;
        }
        return new BlueInstance("utillib.KeyVal", fields);
    }
}

internal sealed record PublicNpcInfo(NpcCharacter Npc) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Npc.Identity.Id,
                ["characterName"] = new BlueUnicode(Npc.Identity.Name),
                ["corporationID"] = Npc.CorporationId,
                ["raceID"] = Npc.RaceId,
                ["bloodlineID"] = Npc.BloodlineId,
                ["gender"] = Npc.Identity.Gender,
                ["schoolID"] = Npc.SchoolId,
                ["ancestryID"] = Npc.AncestryId,
                ["createDateTime"] = Npc.CreatedAt,
            }
        );
}

internal sealed record CharacterSelectionReply(AccountProfile Account, IReadOnlyList<CharacterSelectionCard> Pilots) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            new List<object?>
            {
                new BlueInstance(
                    "utillib.KeyVal",
                    new Dictionary<string, object?>
                    {
                        ["userName"] = Account.UserName,
                        ["characterSlots"] = ICharacterCreation.Slots,
                        ["maxCharacterSlots"] = ICharacterCreation.Slots,
                        ["creationDate"] = Account.CreatedAt,
                        ["subscriptionEndTime"] = null,
                    }
                ),
            },
            new object?[] { null, null },
            new BlueList<CharacterSelectionCard>(Pilots),
            new List<object?>(),
        };
}

internal sealed record CharacterSelectionCard(DockedPilot Pilot, long SkillPoints, TrainingEntry? Active, long? QueueEnd, long BalanceCents)
    : BlueModel
{
    // Existing selection-card placeholder; this is not a persisted logoff timestamp.
    private const long PlaceholderLogoffDate = 134000000000000000L;

    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Pilot.PilotId,
                ["characterName"] = Pilot.Name,
                ["name"] = Pilot.Name,
                ["stationID"] = Pilot.Position is null ? Pilot.StationId : null,
                ["solarSystemID"] = Pilot.SystemId,
                ["shipTypeID"] = Pilot.ShipTypeId,
                ["corporationID"] = Pilot.CorpId,
                ["allianceID"] = null,
                ["factionID"] = null,
                ["balance"] = (double)BalanceCents / MarketDefinitions.CentsPerIsk,
                ["balanceChange"] = 0,
                ["skillPoints"] = SkillPoints,
                ["finishedSkills"] = 0,
                ["unreadMailCount"] = 0,
                ["paperdollState"] = 0,
                ["lockTypeID"] = null,
                ["deletePrepareDateTime"] = null,
                ["logoffDate"] = PlaceholderLogoffDate,
                ["skillTypeID"] = Active?.Target.TypeId,
                ["toLevel"] = Active?.Target.Level,
                ["trainingStartTime"] = Active?.StartTime,
                ["trainingEndTime"] = Active?.EndTime,
                ["queueEndTime"] = QueueEnd,
                ["finishSP"] = Active?.EndPoints,
                ["trainedSP"] = Active?.StartPoints,
                ["gender"] = Pilot.Profile.GenderId,
                ["bloodlineID"] = Pilot.Profile.BloodlineId,
                ["raceID"] = Pilot.Profile.RaceId,
            }
        );
}
