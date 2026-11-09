namespace Eden.Server.Administration;

internal static class SkillPublicationState
{
    internal static string Describe(long generation, SkillPublicationStatus? publication, bool dirty)
    {
        if (dirty)
        {
            return "Unsaved changes. Save this draft before publishing.";
        }
        if (publication is null)
        {
            return "Publication status is unavailable. Reload before publishing.";
        }
        if (publication.Active?.Generation == generation)
        {
            return "Published. These saved changes are active on the server.";
        }
        if (publication.Pending?.Generation == generation)
        {
            return "Published — restart required. The launcher will install the matching package before login.";
        }
        if (generation == 0 && publication.Active is null && publication.Pending is null)
        {
            return "Original skills are active. Save and publish changes to update the game.";
        }
        return "Saved draft — ready to publish. Build a package, then restart the server to apply it.";
    }
}
