using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class CharacterPortraitHandler(ICharacterPortraits portraits, IPilotCharacterSheet sheet) : IPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    )
    {
        // ScenePortraitQueue asks for public identity while drawing account cards.
        // Permit only this account's roster before selection; other public services
        // retain the router's selected-pilot boundary.
        if (
            !context.Selected
            && service == "charMgr"
            && method == "GetPublicInfo"
            && arguments is object?[] { Length: 1 } fields
            && fields[0] is long owner
            && context.Roster.Any(p => p.PilotId == owner)
        )
        {
            if (keywords is { Count: > 0 } || sheet.ReadPublicProfile(owner) is not { } profile)
            {
                return new PilotCallResult.Failure("Invalid character portrait profile request.");
            }
            return new PublicCharacterInfo(profile);
        }
        if (service != "paperDollServer" || method is not ("GetPaperDollData" or "GetPaperDollDataBatch" or "GetPaperDollPortraitDataFor"))
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 } || arguments is not object?[] { Length: 1 } args)
        {
            return new PilotCallResult.Failure("Invalid character portrait request.");
        }
        long[] ids;
        if (method == "GetPaperDollDataBatch")
        {
            if (args[0] is not List<object?> batch || batch.Count > ICharacterPortraits.MaximumBatchSize || batch.Any(id => id is not long))
            {
                return new PilotCallResult.Failure("Invalid character portrait batch.");
            }
            ids = batch.Cast<long>().ToArray();
        }
        else if (args[0] is long id)
        {
            ids = [id];
        }
        else
        {
            return new PilotCallResult.Failure("Invalid character portrait request.");
        }
        try
        {
            var sources = portraits.Read(ids);
            if (method == "GetPaperDollDataBatch")
            {
                return new CharacterAppearanceBatchReply(sources);
            }
            var source = sources.SingleOrDefault();
            return method == "GetPaperDollPortraitDataFor" ? new CharacterPortraitReply(source?.Portrait)
                : source is null ? PilotCallResult.None
                : new CharacterDollReply(source);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CharacterAppearanceBatchReply(IReadOnlyList<CharacterPortraitSource> Sources) : BlueModel
{
    public override object ToBlue() => Sources.ToDictionary(s => s.CharacterId, s => new CharacterDollReply(s).ToBlue());
}

internal sealed record CharacterDollReply(CharacterPortraitSource Source) : BlueModel
{
    private const string RowNamespace = "eve.common.script.paperDoll.paperDollDefinitions.";

    public override object? ToBlue()
    {
        if (Source.Appearance is { } data)
        {
            return BlueMarshal.Decode(data);
        }
        if (Source.DefaultDoll is not { } doll)
        {
            return null;
        }
        return new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["appearance"] = new PaperDollRow(RowNamespace + "AppearanceRow", [doll.HairDarkness]),
                ["modifiers"] = doll
                    .Modifiers.Select(m =>
                        (object?)new PaperDollRow(RowNamespace + "ModifierRow", [m.LocationId, m.ResourceId, m.Variation])
                    )
                    .ToList(),
                ["sculpts"] = doll
                    .Sculpts.Select(s =>
                        (object?)new PaperDollRow(RowNamespace + "SculptingRow", [s.LocationId, s.UpDown, s.LeftRight, s.ForwardBack])
                    )
                    .ToList(),
                ["colors"] = doll
                    .Colors.Select(c =>
                        (object?)new PaperDollRow(RowNamespace + "ColorSelectionRow", [c.ColorId, c.NameA, c.NameBC, c.Weight, c.Gloss])
                    )
                    .ToList(),
            }
        );
    }
}
