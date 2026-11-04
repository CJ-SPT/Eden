using Eden.Gameplay.Characters;
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

internal sealed record PublicCharacterInfo(DockedPilot Pilot) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["characterID"] = Pilot.PilotId,
                ["characterName"] = Pilot.Name,
                ["corporationID"] = Pilot.CorpId,
                ["raceID"] = Pilot.Profile.RaceId,
                ["bloodlineID"] = Pilot.Profile.BloodlineId,
                ["gender"] = Pilot.Profile.GenderId,
                ["schoolID"] = Pilot.Profile.SchoolId,
                ["ancestryID"] = Pilot.Profile.AncestryId,
                ["createDateTime"] = Pilot.Profile.CreatedAt,
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
