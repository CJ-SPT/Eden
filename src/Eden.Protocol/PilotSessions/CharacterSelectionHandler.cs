using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class CharacterSelectionHandler(IPilotSkillReader skills) : IPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object? arguments)
    {
        if (service == "charUnboundMgr" && method == "SelectCharacterID")
        {
            if (arguments is not object?[] { Length: > 0 } args || !Equals(args[0], context.Pilot.PilotId))
            {
                return new PilotCallRouter.Error("Unknown pilot for this account");
            }
            context.Selected = true;
            return null;
        }
        return (service, method) switch
        {
            ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(context.Pilot),
            ("charUnboundMgr", "GetCharOmegaDowngradeStatus") => new Dictionary<string, object?>(),
            ("charUnboundMgr", "GetCharacterLockType") => null,
            ("populationCap", "MachoResolveObject") => PilotLogin.NodeId,
            ("populationCap", "MachoBindObject") when IsPilotLoadSlot(arguments, context.Pilot.PilotId) => new object?[] { new BlueObjectReference($"N={PilotLogin.NodeId}:1", 1), new Dictionary<string, object?>() },
            _ => PilotCallRouter.Unsupported,
        };
    }

    private static bool IsPilotLoadSlot(object? value, long pilotId) =>
        value is object?[] { Length: 2 } args && args[0] is object?[] { Length: 2 } bind && Equals(bind[0], pilotId) &&
        args[1] is object?[] { Length: > 1 } call && BlueMarshal.Text(call[0]) == "GetCharacterLoadSlot" &&
        call[1] is object?[] { Length: 1 } ids && Equals(ids[0], pilotId);

    private object?[] CharacterSelection(DockedPilot docked)
    {
        BlueInstance account = new("utillib.KeyVal", new Dictionary<string, object?>
        {
            ["userName"] = docked.Account.UserName,
            ["characterSlots"] = 1,
            ["maxCharacterSlots"] = 1,
            ["creationDate"] = docked.Account.CreatedAt,
            ["subscriptionEndTime"] = null,
        });
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
            ["skillPoints"] = skills.ReadSkills(docked.PilotId).Sum(s => s.Points),
            ["finishedSkills"] = 0,
            ["unreadMailCount"] = 0,
            ["paperdollState"] = 0,
            ["lockTypeID"] = null,
            ["deletePrepareDateTime"] = null,
            ["logoffDate"] = 134000000000000000L,
            ["skillTypeID"] = null,
            ["gender"] = docked.Profile.GenderId,
            ["bloodlineID"] = docked.Profile.BloodlineId,
            ["raceID"] = docked.Profile.RaceId,
        });
        return [new List<object?> { account }, new object?[] { null, null }, new List<object?> { pilot }, new List<object?>()];
    }
}
