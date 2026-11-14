using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotNotesStore(WorldDatabase database, INpcDirectory npcs) : IPilotNotes
{
    public string Read(long pilotId, long targetId)
    {
        PilotNoteRules.CheckTarget(targetId);
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                CheckCharacters(world, pilotId, targetId);
                return world.Set<SavedPilotNote>().Find(pilotId, targetId)?.Text ?? "";
            }
        );
    }

    public void Save(long pilotId, PilotNote note)
    {
        PilotNoteRules.Check(note);
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        CheckCharacters(world, pilotId, note.TargetId);
        var saved = world.Set<SavedPilotNote>().Find(pilotId, note.TargetId);
        if (note.Text.Length == 0)
        {
            if (saved is not null)
            {
                world.Remove(saved);
            }
        }
        else if (saved is null)
        {
            world.Add(
                new SavedPilotNote
                {
                    PilotId = pilotId,
                    TargetId = note.TargetId,
                    Text = note.Text,
                }
            );
        }
        else
        {
            saved.Text = note.Text;
        }
        transaction.Commit();
    }

    private void CheckCharacters(WorldAction world, long pilotId, long targetId)
    {
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("Unknown note owner.");
        }
        if (!world.PilotExists(targetId) && npcs.Character(targetId) is null)
        {
            throw new InvalidDataException("Unknown note target.");
        }
    }
}
