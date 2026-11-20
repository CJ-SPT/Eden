using Eden.Gameplay.Characters;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

[Injectable]
internal sealed class MilitiaEnlistmentHandler(IMilitiaEnlistment militia, IPilotCharacterSheet characters, CorporationHandler corporations)
    : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            (service, method)
            is not (

                (
                    "fwCharacterEnlistmentMgr",
                    "GetMyEnlistment"
                        or "GetMyDirectEnlistmentCooldownTimestamp"
                        or "GetCorpAllowedEnlistmentFactions"
                        or "SetMyCorpAllowedEnlistmentFactions"
                        or "CreateMyDirectEnlistment"
                        or "RemoveMyDirectEnlistment"
                )
                or
                (
                    "facWarMgr",
                    "JoinFactionAsCharacter"
                        or "GetFactionMilitiaCorporation"
                        or "GetCharacterEnrolledFaction"
                        or "GetCorpFactionalWarStatus"
                )
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected militia enlistment arguments.");
        }

        long pilot = context.Pilot.PilotId;
        try
        {
            switch (method, args)
            {
                case ("GetMyEnlistment", []):
                    return new MilitiaEnlistmentReply(militia.Read(pilot));
                case ("GetMyDirectEnlistmentCooldownTimestamp", []):
                    return militia.Read(pilot).DirectCooldownUntil;
                case ("GetCorpAllowedEnlistmentFactions", [long corporation]) when corporation is > 0 and <= int.MaxValue:
                    return FactionList(militia.AllowedFactions((int)corporation));
                case ("SetMyCorpAllowedEnlistmentFactions", [List<object?> factions]) when factions.Count <= 4:
                    return FactionList(militia.SetAllowedFactions(pilot, factions.Select(Faction).ToArray()));
                case ("CreateMyDirectEnlistment", [var faction]):
                    militia.EnlistDirectly(pilot, Faction(faction));
                    break;
                case ("JoinFactionAsCharacter", [var faction]):
                    militia.JoinNpcMilitia(pilot, Faction(faction));
                    break;
                case ("RemoveMyDirectEnlistment", []):
                    militia.RetireDirectly(pilot);
                    break;
                case ("GetFactionMilitiaCorporation", [long faction]) when faction is > 0 and <= int.MaxValue:
                    return Enum.IsDefined((MilitiaFaction)faction)
                        ? MilitiaRules.Corporation((MilitiaFaction)faction)
                        : PilotCallResult.None;
                case ("GetCharacterEnrolledFaction", [long character]) when character > 0:
                    return FactionReply(characters.ReadPublicProfile(character)?.MilitiaFaction);
                case ("GetCorpFactionalWarStatus", []):
                    return new MilitiaCorporationStatusReply(MilitiaRules.CorporationFaction(context.Pilot.CorpId));
                default:
                    return new PilotCallResult.Failure("Invalid militia enlistment arguments.");
            }
            corporations.Advance(context);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static BlueList<int> FactionList(IReadOnlyList<MilitiaFaction> factions) =>
        new(factions.Select(faction => (int)faction).ToArray());

    private static PilotCallResult FactionReply(MilitiaFaction? faction) => faction is { } value ? (int)value : PilotCallResult.None;

    private static MilitiaFaction Faction(object? value) =>
        value is long id && id is > 0 and <= int.MaxValue && Enum.IsDefined((MilitiaFaction)id)
            ? (MilitiaFaction)id
            : throw new InvalidDataException("Choose an empire militia.");
}

internal sealed record MilitiaEnlistmentReply(MilitiaAffiliation Affiliation) : BlueModel
{
    // Build 3662168 uses the same order for the RPC and its update event.
    public override object ToBlue() => Arguments(Affiliation);

    internal static object?[] Arguments(MilitiaAffiliation affiliation) =>
        [(int?)affiliation.Effective, (int?)affiliation.Direct, (int?)affiliation.Corporation];
}

internal sealed record MilitiaEnlistmentUpdated(MilitiaAffiliation Affiliation) : PilotNotification("OnEnlistmentStateUpdated")
{
    public override object?[] Arguments => MilitiaEnlistmentReply.Arguments(Affiliation);
}

internal sealed record MilitiaCorporationStatusReply(MilitiaFaction? Faction) : BlueModel
{
    // Build 3662168 appConst.facwarCorporationActive.
    private const int ActiveStatus = 1;

    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["status"] = Faction is not null ? ActiveStatus : null,
                ["factionID"] = (int?)Faction,
                ["startDate"] = null,
            }
        );
}
