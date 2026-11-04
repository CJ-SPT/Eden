using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Industry;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

internal enum BrainOperation
{
    PreMultiply = 0,
    Add = 2,
    PostMultiply = 4,
    PostPercent = 6,
}

[Injectable]
internal sealed class PilotBrain(
    IPilotSkillReader skills,
    TimeProvider clock,
    IPilotIndustrySkills industry,
    IPilotJumpCloneSkills clones,
    IPilotNavigationSkills navigation,
    IPilotFitting? fitting = null
)
{
    private const double Percent = 100;

    // The native client applies these earned modifiers to its local dogma graph.
    public PilotBrainState Read(long pilotId, long shipId)
    {
        var levels = skills.ReadSkills(pilotId).ToDictionary(skill => skill.TypeId, skill => skill.Level);
        var effects = CombatDefinitions
            .SkillModifiers.Where(modifier => levels.GetValueOrDefault((int)modifier.Skill) > 0)
            .Select(modifier => new BrainEffect(
                shipId,
                modifier.GroupId is null ? BrainModifier.LocationRequiredSkill : BrainModifier.LocationGroup,
                modifier.Attribute,
                BrainOperation.PostPercent,
                [modifier.GroupId ?? (int)modifier.RequiredSkill],
                [(int)modifier.Skill],
                Percent * modifier.PerLevel * levels[(int)modifier.Skill]
            ))
            .ToList();
        effects.AddRange(
            MiningDefinitions
                .SkillModifiers.Where(modifier => levels.GetValueOrDefault((int)modifier.Skill) > 0)
                .Select(modifier => new BrainEffect(
                    shipId,
                    BrainModifier.LocationRequiredSkill,
                    modifier.Attribute,
                    BrainOperation.PostPercent,
                    [(int)MiningSkill.Mining],
                    [(int)modifier.Skill],
                    Percent * modifier.PerLevel * levels[(int)modifier.Skill]
                ))
        );
        effects.AddRange(
            navigation
                .NavigationBonuses(pilotId)
                .Select(bonus => new BrainEffect(
                    shipId,
                    bonus.RequiredSkill.HasValue ? BrainModifier.LocationRequiredSkill
                        : bonus.Group.HasValue ? BrainModifier.LocationGroup
                        : BrainModifier.Item,
                    bonus.Attribute,
                    BrainOperation.PostPercent,
                    bonus.RequiredSkill is int skill ? [skill]
                        : bonus.Group is int group ? [group]
                        : [],
                    [bonus.SkillId],
                    bonus.Percent
                ))
        );
        var fittingBonuses = fitting?.SkillBonuses(pilotId) ?? [];
        BrainEffect FittingEffect(FittingSkillBonus bonus) =>
            new(
                bonus.Modifier.Selector == ModifierSelector.OwnerRequiredSkill ? pilotId : shipId,
                bonus.Modifier.Selector switch
                {
                    ModifierSelector.Group => BrainModifier.LocationGroup,
                    ModifierSelector.RequiredSkill => BrainModifier.LocationRequiredSkill,
                    ModifierSelector.OwnerRequiredSkill => BrainModifier.OwnerRequiredSkill,
                    _ => BrainModifier.Item,
                },
                bonus.Modifier.Attribute,
                (BrainOperation)bonus.Modifier.Operation,
                bonus.Modifier.SelectionId is int selected ? [selected] : [],
                [bonus.SkillId],
                bonus.Value
            );
        effects.AddRange(fittingBonuses.Where(b => b.Modifier.Selector != ModifierSelector.OwnerRequiredSkill).Select(FittingEffect));
        var ownerEffects = fittingBonuses.Where(b => b.Modifier.Selector == ModifierSelector.OwnerRequiredSkill).Select(FittingEffect);
        var character = industry
            .CharacterBonuses(pilotId)
            .Select(bonus => new BrainEffect(
                pilotId,
                BrainModifier.Item,
                bonus.Attribute,
                bonus.Additive ? BrainOperation.Add : BrainOperation.PostPercent,
                [],
                [bonus.SkillId],
                bonus.Value
            ))
            .Concat(ownerEffects)
            .ToArray();
        var cloneSkills = clones.ReadCloneSkills(pilotId);
        var cloneEffects = new[]
        {
            new BrainEffect(
                pilotId,
                BrainModifier.Item,
                DogmaAttributeId.MaxJumpClones,
                BrainOperation.Add,
                [],
                [(int)InfomorphSkill.Psychology],
                cloneSkills.Psychology
            ),
            new BrainEffect(
                pilotId,
                BrainModifier.Item,
                DogmaAttributeId.MaxJumpClones,
                BrainOperation.Add,
                [],
                [(int)InfomorphSkill.AdvancedPsychology],
                cloneSkills.AdvancedPsychology
            ),
            new BrainEffect(
                pilotId,
                BrainModifier.Item,
                DogmaAttributeId.MaxJumpClones,
                BrainOperation.Add,
                [],
                [(int)InfomorphSkill.ElitePsychology],
                cloneSkills.ElitePsychology
            ),
            new BrainEffect(
                pilotId,
                BrainModifier.Item,
                DogmaAttributeId.CloneJumpCooldown,
                BrainOperation.Add,
                [],
                [(int)InfomorphSkill.Synchronizing],
                -cloneSkills.Synchronizing
            ),
        };
        character = character.Concat(cloneEffects.Where(effect => effect.Value != 0)).ToArray();
        return new(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), character, effects, []);
    }

    public BrainUpdate Update(long pilotId, long shipId) => Read(pilotId, shipId).Update();
}
