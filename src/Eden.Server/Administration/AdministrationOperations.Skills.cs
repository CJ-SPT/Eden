using System.Security.Claims;
using Eden.Gameplay.Skills;
using Eden.World.Skills;

namespace Eden.Server.Administration;

internal sealed record AdminSkillSave(long Generation, SkillDefinition Definition);

internal sealed record AdminSkillRestore(long Generation, long? Revision = null);

internal sealed record AdminSkillDelete(long Generation);

internal sealed record AdminSkillPublish(long Generation, string ClientRoot);

internal sealed record AdminSkillOptions(
    IReadOnlyList<SkillEditorOption> Groups,
    IReadOnlyList<SkillEditorOption> Attributes,
    IReadOnlyList<SkillEditorOption> Effects
);

internal sealed partial class AdministrationOperations
{
    internal async Task<SkillEditorSnapshot> Skills(ClaimsPrincipal user)
    {
        await Authorize(user);
        return skillEditor.Read();
    }

    internal async Task<AdminSkillOptions> SkillOptions(ClaimsPrincipal user)
    {
        await Authorize(user);
        return new(skillEditor.Groups(), skillEditor.Attributes(), skillEditor.Effects());
    }

    internal async Task<SkillEditorEntry> SaveSkill(ClaimsPrincipal user, AdminSkillSave request, bool create = false)
    {
        await Authorize(user);
        return create
            ? skillEditor.Create(request.Generation, request.Definition, user.Identity!.Name!)
            : skillEditor.Save(request.Generation, request.Definition, user.Identity!.Name!);
    }

    internal async Task<SkillEditorEntry> RestoreSkill(ClaimsPrincipal user, int id, AdminSkillRestore request)
    {
        await Authorize(user);
        return skillEditor.Restore(request.Generation, id, request.Revision, user.Identity!.Name!);
    }

    internal async Task<SkillEditorEntry> DeleteSkill(ClaimsPrincipal user, int id, long generation)
    {
        await Authorize(user);
        return skillEditor.Delete(generation, id, user.Identity!.Name!);
    }

    internal async Task<IReadOnlyList<SkillEditorRevision>> SkillHistory(ClaimsPrincipal user, int id)
    {
        await Authorize(user);
        return skillEditor.History(id);
    }

    internal async Task<SkillPublicationStatus> SkillPublication(ClaimsPrincipal user)
    {
        await Authorize(user);
        return skillPublisher.Status();
    }

    internal async Task<SkillClientPackage> PublishSkills(ClaimsPrincipal user, AdminSkillPublish request)
    {
        await Authorize(user);
        return await skillPublisher.Publish(request.Generation, request.ClientRoot, authorize: () => Authorize(user));
    }
}
