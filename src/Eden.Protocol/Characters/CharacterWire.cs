using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal sealed record CharacterBiographyReply(string Biography) : BlueModel
{
    public override object ToBlue() => new BlueUnicode(Biography);
}

internal sealed record CharacterPortraitReply(byte[]? Data) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Data is null
                ? new List<object?>()
                : new List<object?> { ReadRendererSettings(Data) },
            null,
        };

    private static object? ReadRendererSettings(byte[] data)
    {
        var saved = BlueMarshal.Decode(data);
        if (saved is not BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields })
        {
            return saved;
        }

        // The creator submits vectors and a nested pose map. The scene renderer
        // reads the flat camera and pose fields used by native facial portrait data.
        var settings = new Dictionary<string, object?>(fields);
        AddVector(settings, fields.GetValueOrDefault("cameraPosition"), "camera");
        AddVector(settings, fields.GetValueOrDefault("cameraPoi"), "cameraPoi");
        if (fields.GetValueOrDefault("poseData") is Dictionary<string, object?> pose)
        {
            AddVector(settings, pose.GetValueOrDefault("HeadLookTarget"), "headLookTarget");
            foreach (var (key, value) in pose)
            {
                if (key.Length > 0 && key != "HeadLookTarget")
                {
                    settings.TryAdd(char.ToLowerInvariant(key[0]) + key[1..], value);
                }
            }
        }
        return new BlueInstance("utillib.KeyVal", settings);
    }

    private static void AddVector(Dictionary<string, object?> settings, object? value, string prefix)
    {
        if (value is object?[] { Length: 3 } coordinates)
        {
            settings.TryAdd(prefix + "X", coordinates[0]);
            settings.TryAdd(prefix + "Y", coordinates[1]);
            settings.TryAdd(prefix + "Z", coordinates[2]);
        }
    }
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

internal sealed record CharacterCorporationInfo(int CorporationId, string Title = "", IReadOnlyList<CorporationTitle>? Titles = null)
    : BlueModel
{
    // Standalone corporations have no alliance or militia membership.
    private const int CorporationTitleCount = 16;

    public override object ToBlue()
    {
        Dictionary<string, object?> fields = new()
        {
            ["corpID"] = CorporationId,
            ["allianceID"] = null,
            ["militiaFactionID"] = null,
            ["title"] = new BlueUnicode(Title),
        };
        for (int index = 1; index <= CorporationTitleCount; index++)
        {
            string? name = Titles?.SingleOrDefault(t => t.Id == 1L << (index - 1))?.Name;
            fields[$"title{index}"] = name is null ? null : new BlueUnicode(name);
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

internal sealed record CharacterSelectionCard(
    DockedPilot Pilot,
    long SkillPoints,
    TrainingEntry? Active,
    long? QueueEnd,
    long BalanceCents,
    long? DeleteReadyAt = null
) : BlueModel
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
                ["deletePrepareDateTime"] = DeleteReadyAt,
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
