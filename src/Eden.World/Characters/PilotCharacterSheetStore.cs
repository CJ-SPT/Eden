using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotCharacterSheetStore(WorldDatabase database, PilotTrainingStore training, TimeProvider clock)
    : IPilotCharacterSheet
{
    public PublicCharacterProfile? ReadPublicProfile(long pilotId) =>
        WorldActionScope.Inspect<PublicCharacterProfile?>(
            database,
            world =>
                world.Pilots.Find(pilotId) is { } p
                    ? new(
                        p.Id,
                        p.Name,
                        p.CorporationId,
                        p.RaceId,
                        p.BloodlineId,
                        p.GenderId,
                        p.SchoolId,
                        p.AncestryId,
                        p.CreatedAt,
                        world.Set<SavedCharacterSheet>().Find(pilotId)?.Biography ?? ""
                    )
                    : null,
            consistentFileRead: true
        );

    public NeuralRemapState ReadAttributes(long pilotId) => WorldActionScope.Inspect(database, world => ReadAttributes(world, pilotId));

    public CharacterAttributes ReadEffectiveAttributes(long pilotId) =>
        WorldActionScope.Inspect(
            database,
            world => PilotBoosterStore.Read(world, pilotId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()).Attributes,
            consistentFileRead: true
        );

    internal static NeuralRemapState ReadAttributes(WorldAction world, long pilotId)
    {
        CheckPilot(world, pilotId);
        return world.Set<SavedCharacterSheet>().Find(pilotId)?.Snapshot() ?? NeuralRemapState.Default;
    }

    private static SavedCharacterSheet GetOrCreate(WorldAction world, long pilotId)
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

    public string ReadBiography(long pilotId) =>
        WorldActionScope.Inspect(
            database,
            world =>
            {
                CheckPilot(world, pilotId);
                return world.Set<SavedCharacterSheet>().Find(pilotId)?.Biography ?? "";
            }
        );

    public void SaveBiography(long pilotId, string biography)
    {
        CharacterSheetRules.CheckBiography(biography);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        GetOrCreate(transaction.World, pilotId).Biography = biography;
        transaction.Commit();
    }

    private static void CheckPilot(WorldAction world, long pilotId)
    {
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Unknown character.");
        }
    }
}
