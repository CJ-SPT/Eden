using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Propulsion;

internal static class TackleWire
{
    internal static string Ewar(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.StasisWebifier => "webify",
            DogmaEffect.WarpDisruptor => "warpScrambler",
            DogmaEffect.WarpScrambler => "warpScramblerMWD",
            _ => throw new InvalidDataException("Unsupported incoming tackle effect."),
        };

    internal static DogmaEffect? Effect(string? name) =>
        name switch
        {
            "remoteWebifierFalloff" => DogmaEffect.StasisWebifier,
            "warpDisrupt" => DogmaEffect.WarpDisruptor,
            "warpScrambleBlockMWDWithNPCEffect" => DogmaEffect.WarpScrambler,
            "warpCoreStabilizerActive" => DogmaEffect.WarpCoreStabilizer,
            _ => null,
        };

    internal static string Graphic(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.StasisWebifier => "effects.ModifyTargetSpeed",
            DogmaEffect.WarpDisruptor => "effects.WarpDisrupt",
            DogmaEffect.WarpScrambler => "effects.WarpScramble",
            DogmaEffect.WarpCoreStabilizer => "effects.Invulnerability",
            _ => throw new InvalidDataException("Unsupported tackle graphic."),
        };
}

// Pinned tactical.OnEwarStart/End take source, module, target and the
// buffBarConst slot name, rather than the effect's name or graphic GUID.
internal sealed record TackleEwarChanged(TackleEffect Effect, bool Active) : PilotNotification(Active ? "OnEwarStart" : "OnEwarEnd")
{
    public override object?[] Arguments =>
        [Effect.SourceShipId, Effect.Module.ItemId, Effect.Target.Id, TackleWire.Ewar(Effect.Module.Effect)];
}
