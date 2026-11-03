using Eden.Gameplay.Docking;
using Eden.Protocol.Characters;
using Eden.Protocol.Docking;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

namespace Eden.Protocol.PilotSessions;

[Injectable]
internal sealed class CharacterSelectionHandler(
    IPilotLookup pilots,
    ICharacterCreation creation,
    IPilotSkillReader skills,
    IPilotTraining training,
    TimeProvider clock
) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        if (service == "charUnboundMgr" && method == "SelectCharacterID")
        {
            var id = arguments is object?[] { Length: > 0 } args ? args[0] : keywords?.GetValueOrDefault("charID");
            if (context.Selected || context.Roster.FirstOrDefault(p => Equals(p.PilotId, id)) is not { } selected)
            {
                return new PilotCallResult.Failure("Unknown pilot for this account");
            }
            context.Select(selected);
            return PilotCallResult.None;
        }
        if (service == "charUnboundMgr" && method == "CreateCharacterWithDoll" && !context.Selected)
        {
            if (
                arguments is not object?[] { Length: 10 } fields
                || fields[0] is not (byte[] or string)
                || fields[8] is not null
                || fields[9] is not null
                || new[] { 1, 2, 3, 4, 7 }.Any(i => fields[i] is not long number || number < 0 || number > int.MaxValue)
            )
            {
                return new PilotCallResult.Failure("Invalid character creation arguments");
            }
            int Number(int index) => (int)(long)fields[index]!;
            try
            {
                long id = creation.Create(
                    context.AccountId,
                    new(
                        BlueMarshal.Text(fields[0]),
                        Number(1),
                        Number(2),
                        Number(3),
                        Number(4),
                        AvatarWire.Encode(fields[5]),
                        AvatarWire.Encode(fields[6]),
                        Number(7)
                    )
                );
                context.Roster = pilots.ListPilots(context.AccountId);
                return id;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        return (service, method) switch
        {
            ("charUnboundMgr", "GetCharacterSelectionData") => CharacterSelection(context),
            ("charUnboundMgr", "ValidateNameEx") when arguments is object?[] { Length: > 0 } name && name[0] is byte[] or string =>
                creation.ValidateName(BlueMarshal.Text(name[0])),
            ("charUnboundMgr", "GetCohortsForUser") => BlueEmpty.List,
            ("charUnboundMgr", "GetValidRandomName") => $"Eden {Guid.NewGuid().ToString("N")[..8]}",
            ("charUnboundMgr", "GetCharOmegaDowngradeStatus") => BlueEmpty.Dictionary,
            ("charUnboundMgr", "GetCharacterLockType") => PilotCallResult.None,
            ("populationCap", "MachoResolveObject") => PilotLogin.NodeId,
            ("populationCap", "MachoBindObject") when context.Roster.Any(p => IsPilotLoadSlot(arguments, p.PilotId)) =>
                new PilotLoadSlotReply(new BlueObjectReference($"N={PilotLogin.NodeId}:1", 1)),
            _ => PilotCallResult.Unsupported,
        };
    }

    private static bool IsPilotLoadSlot(object? value, long pilotId) =>
        value is object?[] { Length: 2 } args
        && args[0] is object?[] { Length: 2 } bind
        && Equals(bind[0], pilotId)
        && args[1] is object?[] { Length: > 1 } call
        && BlueMarshal.Text(call[0]) == "GetCharacterLoadSlot"
        && call[1] is object?[] { Length: 1 } ids
        && Equals(ids[0], pilotId);

    private CharacterSelectionReply CharacterSelection(PilotCallContext context) =>
        new(context.Account, context.Roster.Select(Card).ToArray());

    private CharacterSelectionCard Card(DockedPilot pilot)
    {
        var queue = training.Advance(pilot.PilotId).Queue;
        var head = queue.FirstOrDefault();
        var active = head?.StartTime is not null ? head : null;
        long points =
            skills.ReadSkills(pilot.PilotId).Sum(skill => skill.Points)
            + (active is null ? 0 : active.PointsAt(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) - active.StartPoints);
        return new(pilot, points, active, queue.LastOrDefault()?.EndTime);
    }
}
