using Eden.Protocol.Combat;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

internal enum BrainOperation
{
    PostPercent = 6,
}

[Injectable]
internal sealed class PilotBrain(IPilotSkillReader skills, TimeProvider clock)
{
    private const string LocationRequiredSkillModifier = "LRS";
    private const string LocationGroupModifier = "LG";
    private const double Percent = 100;

    // Build 3662168 calculates docked module tooltips in clientDogmaLocation,
    // separately from the trained values in Godma. Brain effects attach the
    // earned skill bonuses to that local graph without changing base attributes.
    public object?[] Read(long pilotId, long shipId)
    {
        var levels = skills.ReadSkills(pilotId).ToDictionary(s => s.TypeId, s => s.Level);
        var effects = CombatDefinitions.SkillModifiers.Where(m => levels.GetValueOrDefault((int)m.Skill) > 0)
            .Select(m => (object?)new BlueNewObject(BlueNewObject.BrainEffectClass, new Dictionary<string, object?>
            {
                ["fromAttrib"] = null,
                ["toItemID"] = shipId,
                ["modifierType"] = m.GroupId is null ? LocationRequiredSkillModifier : LocationGroupModifier,
                ["toAttribID"] = (int)m.Attribute,
                ["operation"] = (int)BrainOperation.PostPercent,
                ["extras"] = new object?[] { m.GroupId ?? (int)m.RequiredSkill },
                ["skills"] = new List<object?> { (int)m.Skill },
                ["value"] = Percent * m.PerLevel * levels[(int)m.Skill],
            })).ToList();
        return [clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), new List<object?>(), effects, new List<object?>()];
    }

    public object?[] Update(long pilotId, long shipId)
    {
        var brain = Read(pilotId, shipId);
        // OnServerBrainUpdated takes one (version, marshaled gray matter) pair.
        return [brain[0], BlueMarshal.Encode(new object?[] { brain[1], brain[2], brain[3] })];
    }
}
