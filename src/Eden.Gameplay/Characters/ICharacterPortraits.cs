namespace Eden.Gameplay.Characters;

public sealed record CharacterDollModifier(long LocationId, long ResourceId, long Variation);

public sealed record CharacterDollSculpt(long LocationId, double UpDown, double LeftRight, double ForwardBack);

public sealed record CharacterDollColor(long ColorId, long NameA, long NameBC, double Weight, double Gloss);

public sealed record CharacterDoll(
    double HairDarkness,
    IReadOnlyList<CharacterDollModifier> Modifiers,
    IReadOnlyList<CharacterDollSculpt> Sculpts,
    IReadOnlyList<CharacterDollColor> Colors
);

public sealed record CharacterPortraitSource(long CharacterId, byte[]? Appearance, byte[]? Portrait, CharacterDoll? DefaultDoll);

public interface ICharacterPortraits
{
    // The native queue fetches five portraits at a time. Allow larger public reads,
    // but bound work before opening the world or decoding saved appearances.
    const int MaximumBatchSize = 100;
    IReadOnlyList<CharacterPortraitSource> Read(IReadOnlyList<long> characterIds);
}
