using Eden.Data.World.Characters;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotNotesStore(WorldDatabase database, INpcDirectory npcs) : IPilotNotes
{
    public string Read(long pilotId, long targetId)
    {
        PilotNoteRules.CheckTarget(targetId);
        using WorldDbContext world = new(database.Path);
        CheckCharacters(world, pilotId, targetId);
        return world
                .Set<SavedPilotNote>()
                .AsNoTracking()
                .Where(note => note.PilotId == pilotId && note.TargetId == targetId)
                .Select(note => note.Text)
                .SingleOrDefault()
            ?? "";
    }

    public void Save(long pilotId, PilotNote note)
    {
        PilotNoteRules.Check(note);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
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
        world.SaveChanges();
        transaction.Commit();
    }

    private void CheckCharacters(WorldDbContext world, long pilotId, long targetId)
    {
        if (!world.Pilots.Any(pilot => pilot.Id == pilotId))
        {
            throw new InvalidDataException("Unknown note owner.");
        }
        if (!world.Pilots.Any(pilot => pilot.Id == targetId) && npcs.Character(targetId) is null)
        {
            throw new InvalidDataException("Unknown note target.");
        }
    }
}
