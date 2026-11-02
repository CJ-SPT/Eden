using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class CharacterSelectionHandler(IPilotLookup pilots, ICharacterCreation creation, IPilotSkillReader skills, IPilotTraining training, TimeProvider clock) : IPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords)
    {
        if (service == "charUnboundMgr" && method == "SelectCharacterID")
        {
            var id = arguments is object?[] { Length: > 0 } args ? args[0] : keywords?.GetValueOrDefault("charID");
            if (context.Selected || context.Roster.FirstOrDefault(p => Equals(p.PilotId, id)) is not { } selected)
            {
                return new PilotCallRouter.Error("Unknown pilot for this account");
            }
            context.Select(selected);
            return null;
        }
        if (service == "charUnboundMgr" && method == "CreateCharacterWithDoll" && !context.Selected)
        {
            if (arguments is not object?[] { Length: 10 } fields || fields[0] is not (byte[] or string) ||
                !IsAvatarData(fields[5]) || !IsAvatarData(fields[6]) || fields[8] is not null || fields[9] is not null ||
                new[] { 1, 2, 3, 4, 7 }.Any(i => fields[i] is not long number || number < 0 || number > int.MaxValue))
            {
                return new PilotCallRouter.Error("Invalid character creation arguments");
            }
            int Number(int index) => (int)(long)fields[index]!;
            try
            {
                long id = creation.Create(context.AccountId, new(BlueMarshal.Text(fields[0]), Number(1), Number(2),
                    Number(3), Number(4), BlueMarshal.Encode(fields[5]), BlueMarshal.Encode(fields[6]), Number(7)));
                context.Roster = pilots.ListPilots(context.AccountId);
                return id;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallRouter.Error(error.Message);
            }
        }
        return (service, method) switch
        {
            ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(context),
            ("charUnboundMgr", "ValidateNameEx") when arguments is object?[] { Length: > 0 } name && name[0] is byte[] or string
                => creation.ValidateName(BlueMarshal.Text(name[0])),
            ("charUnboundMgr", "GetCohortsForUser") => new List<object?>(),
            ("charUnboundMgr", "GetValidRandomName") => $"Eden {Guid.NewGuid().ToString("N")[..8]}",
            ("charUnboundMgr", "GetCharOmegaDowngradeStatus") => new Dictionary<string, object?>(),
            ("charUnboundMgr", "GetCharacterLockType") => null,
            ("populationCap", "MachoResolveObject") => PilotLogin.NodeId,
            ("populationCap", "MachoBindObject") when context.Roster.Any(p => IsPilotLoadSlot(arguments, p.PilotId)) => new object?[] { new BlueObjectReference($"N={PilotLogin.NodeId}:1", 1), new Dictionary<string, object?>() },
            _ => PilotCallRouter.Unsupported,
        };
    }

    private static bool IsAvatarData(object? value) => value is BlueInstance { Name: "utillib.KeyVal", State: Dictionary<string, object?> fields }
        && fields.Values.All(IsAvatarField);

    private static bool IsAvatarField(object? value) => value switch
    {
        null or bool or long or double or string or byte[] or PaperDollRow => true,
        object?[] items => items.All(IsAvatarField),
        List<object?> items => items.All(IsAvatarField),
        Dictionary<string, object?> fields => fields.Values.All(IsAvatarField),
        BlueInstance => IsAvatarData(value),
        _ => false,
    };

    private static bool IsPilotLoadSlot(object? value, long pilotId) =>
        value is object?[] { Length: 2 } args && args[0] is object?[] { Length: 2 } bind && Equals(bind[0], pilotId) &&
        args[1] is object?[] { Length: > 1 } call && BlueMarshal.Text(call[0]) == "GetCharacterLoadSlot" &&
        call[1] is object?[] { Length: 1 } ids && Equals(ids[0], pilotId);

    private object?[] CharacterSelection(PilotCallContext context)
    {
        BlueInstance account = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["userName"] = context.Account.UserName,
            ["characterSlots"] = ICharacterCreation.Slots,
            ["maxCharacterSlots"] = ICharacterCreation.Slots,
            ["creationDate"] = context.Account.CreatedAt,
            ["subscriptionEndTime"] = null,
        });
        return [new List<object?> { account }, new object?[] { null, null }, context.Roster.Select(Card).ToList(), new List<object?>()];
    }

    private object? Card(DockedPilot docked)
    {
        var queue = training.Advance(docked.PilotId).Queue;
        var head = queue.FirstOrDefault();
        var active = head?.StartTime is not null ? head : null;
        BlueInstance pilot = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["characterID"] = docked.PilotId,
            ["characterName"] = docked.Name,
            ["name"] = docked.Name,
            ["stationID"] = docked.Position is null ? docked.StationId : null,
            ["solarSystemID"] = docked.SystemId,
            ["shipTypeID"] = docked.ShipTypeId,
            ["corporationID"] = docked.CorpId,
            ["allianceID"] = null,
            ["factionID"] = null,
            ["balance"] = 0,
            ["balanceChange"] = 0,
            ["skillPoints"] = skills.ReadSkills(docked.PilotId).Sum(s => s.Points)
                + (active is null ? 0 : active.PointsAt(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) - active.StartPoints),
            ["finishedSkills"] = 0,
            ["unreadMailCount"] = 0,
            ["paperdollState"] = 0,
            ["lockTypeID"] = null,
            ["deletePrepareDateTime"] = null,
            ["logoffDate"] = 134000000000000000L,
            ["skillTypeID"] = active?.Target.TypeId,
            ["toLevel"] = active?.Target.Level,
            ["trainingStartTime"] = active?.StartTime,
            ["trainingEndTime"] = active?.EndTime,
            ["queueEndTime"] = queue.LastOrDefault()?.EndTime,
            ["finishSP"] = active?.EndPoints,
            ["trainedSP"] = active?.StartPoints,
            ["gender"] = docked.Profile.GenderId,
            ["bloodlineID"] = docked.Profile.BloodlineId,
            ["raceID"] = docked.Profile.RaceId,
        });
        return pilot;
    }
}
