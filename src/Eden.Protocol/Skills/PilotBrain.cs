using Eden.Protocol.Combat;
using Eden.Protocol.Industry;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

internal enum BrainOperation
{
    Add = 2,
    PostPercent = 6,
}

[Injectable]
internal sealed class PilotBrain(IPilotSkillReader skills, TimeProvider clock, IPilotIndustrySkills industry, IPilotNavigationSkills navigation)
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
        effects.AddRange(navigation.NavigationBonuses(pilotId).Select(b => (object?)new BlueNewObject(BlueNewObject.BrainEffectClass,
            new Dictionary<string, object?>
            {
                ["fromAttrib"] = null,
                ["toItemID"] = shipId,
                ["modifierType"] = b.RequiredSkill.HasValue ? LocationRequiredSkillModifier : b.Group.HasValue ? LocationGroupModifier : "M",
                ["toAttribID"] = (int)b.Attribute,
                ["operation"] = (int)BrainOperation.PostPercent,
                ["extras"] = b.RequiredSkill is int skill ? new object?[] { skill } : b.Group is int group ? new object?[] { group } : Array.Empty<object?>(),
                ["skills"] = new List<object?> { b.SkillId },
                ["value"] = b.Percent,
            })));
        var characterEffects = industry.CharacterBonuses(pilotId).Select(b => (object?)new BlueNewObject(BlueNewObject.BrainEffectClass,
            new Dictionary<string, object?>
            {
                ["fromAttrib"] = null,
                ["toItemID"] = pilotId,
                ["modifierType"] = "M",
                ["toAttribID"] = (int)b.Attribute,
                ["operation"] = (int)(b.Additive ? BrainOperation.Add : BrainOperation.PostPercent),
                ["extras"] = Array.Empty<object?>(),
                ["skills"] = new List<object?> { b.SkillId },
                ["value"] = b.Value,
            })).ToList();
        return [clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), characterEffects, effects, new List<object?>()];
    }

    public object?[] Update(long pilotId, long shipId)
    {
        var brain = Read(pilotId, shipId);
        // OnServerBrainUpdated takes one (version, marshaled gray matter) pair.
        return [brain[0], BlueMarshal.Encode(new object?[] { brain[1], brain[2], brain[3] })];
    }
}
