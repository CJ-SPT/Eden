using Eden.Gameplay.Characters;
using Eden.Protocol;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedCharacterSheet
{
    public long PilotId { get; set; }
    public string Biography { get; set; } = "";
    public int Intelligence { get; set; } = CharacterAttributes.Default.Intelligence;
    public int Memory { get; set; } = CharacterAttributes.Default.Memory;
    public int Charisma { get; set; } = CharacterAttributes.Default.Charisma;
    public int Perception { get; set; } = CharacterAttributes.Default.Perception;
    public int Willpower { get; set; } = CharacterAttributes.Default.Willpower;
    public int BonusRemaps { get; set; } = CharacterSheetRules.StartingBonusRemaps;
    public long? NextTimedRemap { get; set; }

    public NeuralRemapState Snapshot() => new(new(Intelligence, Memory, Charisma, Perception, Willpower), BonusRemaps, NextTimedRemap);

    public void Apply(NeuralRemapState state)
    {
        Intelligence = state.Attributes.Intelligence;
        Memory = state.Attributes.Memory;
        Charisma = state.Attributes.Charisma;
        Perception = state.Attributes.Perception;
        Willpower = state.Attributes.Willpower;
        BonusRemaps = state.BonusRemaps;
        NextTimedRemap = state.NextTimedRemap;
    }
}

[Injectable]
internal sealed class PilotCharacterSheetStore(WorldDatabase database, PilotTrainingStore training) : IPilotCharacterSheet
{
    public NeuralRemapState ReadAttributes(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return ReadAttributes(world, pilotId);
    }

    internal static NeuralRemapState ReadAttributes(WorldDbContext world, long pilotId)
    {
        CheckPilot(world, pilotId);
        return world.Set<SavedCharacterSheet>().Find(pilotId)?.Snapshot() ?? NeuralRemapState.Default;
    }

    internal static SavedCharacterSheet GetOrCreate(WorldDbContext world, long pilotId)
    {
        CheckPilot(world, pilotId);
        if (world.Set<SavedCharacterSheet>().Find(pilotId) is { } saved)
        {
            return saved;
        }
        var sheet = new SavedCharacterSheet { PilotId = pilotId };
        world.Add(sheet);
        return sheet;
    }

    public CharacterRemapResult Remap(long pilotId, CharacterAttributes attributes) => training.Remap(pilotId, attributes);

    public string ReadBiography(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        CheckPilot(world, pilotId);
        return world.Set<SavedCharacterSheet>().Find(pilotId)?.Biography ?? "";
    }

    public void SaveBiography(long pilotId, string biography)
    {
        CharacterSheetRules.CheckBiography(biography);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        GetOrCreate(world, pilotId).Biography = biography;
        world.SaveChanges();
        transaction.Commit();
    }

    private static void CheckPilot(WorldDbContext world, long pilotId)
    {
        if (!world.Pilots.Any(pilot => pilot.Id == pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }
}
