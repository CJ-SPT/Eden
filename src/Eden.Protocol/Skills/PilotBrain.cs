using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;

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
    IPilotFitting? fitting = null,
    IWorldTuning? tuning = null
)
{
    private const double Percent = 100;

    // The native client applies these earned modifiers to its local dogma graph.
    public PilotBrainState Read(long pilotId, long shipId)
    {
        var levels = skills.ReadSkills(pilotId).ToDictionary(skill => skill.TypeId, skill => skill.Level);
        // Docked OnlineModule activates the local graph before calling the server.
        // Absolute Godma output values alone do not supply its earned fitting budget.
        var resourceEffects = FittingRules
            .ResourceOutputSkills.Where(bonus => levels.GetValueOrDefault((int)bonus.Skill) > 0)
            .Select(bonus => new BrainEffect(
                shipId,
                BrainModifier.Item,
                bonus.Attribute,
                BrainOperation.PostPercent,
                [],
                [(int)bonus.Skill],
                Percent * InventoryDefinitions.OutputBonusPerSkillLevel * levels[(int)bonus.Skill]
            ));
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
        effects.AddRange(resourceEffects);
        AddGatheringEffects(effects, shipId, tuning?.Settings.Resources ?? new());
        if (tuning is not null)
        {
            // Native inventory reads the local fitting graph. The client applies
            // item modifiers only to attributes the hull already provides.
            foreach (var (attribute, multiplier) in WorldTuningRules.MiningHoldMultipliers(tuning.Settings.MiningHolds))
            {
                effects.Add(new(shipId, BrainModifier.Item, attribute, BrainOperation.PostMultiply, [], [], multiplier));
            }
        }
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
        // Drone control is already sent as absolute pilot attributes by primes and live updates.
        var fittingBonuses = (fitting?.SkillBonuses(pilotId) ?? []).Where(bonus =>
            bonus.Modifier.Attribute is not (DogmaAttributeId.MaxActiveDrones or DogmaAttributeId.DroneControlDistance)
        );
        BrainEffect FittingEffect(FittingSkillBonus bonus) =>
            new(
                bonus.Modifier.Domain == ModifierDomain.Character ? pilotId : shipId,
                bonus.Modifier.Selector switch
                {
                    ModifierSelector.Group => BrainModifier.LocationGroup,
                    ModifierSelector.RequiredSkill => BrainModifier.LocationRequiredSkill,
                    ModifierSelector.OwnerRequiredSkill => BrainModifier.OwnerRequiredSkill,
                    ModifierSelector.Location => BrainModifier.Location,
                    _ => BrainModifier.Item,
                },
                bonus.Modifier.Attribute,
                (BrainOperation)bonus.Modifier.Operation,
                bonus.Modifier.SelectionId is int selected ? [selected] : [],
                [bonus.SkillId],
                bonus.Value
            );
        effects.AddRange(fittingBonuses.Where(b => b.Modifier.Domain != ModifierDomain.Character).Select(FittingEffect));
        var ownerEffects = fittingBonuses.Where(b => b.Modifier.Domain == ModifierDomain.Character).Select(FittingEffect);
        var character = industry
            .CharacterBonuses(pilotId)
            .Select(bonus => new BrainEffect(
                pilotId,
                BrainModifier.Item,
                bonus.Attribute,
                bonus.Additive ? BrainOperation.Add : BrainOperation.PostPercent,
                [],
                bonus.SkillId is int skill ? [skill] : [],
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

    private static void AddGatheringEffects(List<BrainEffect> effects, long shipId, ResourcePolicy policy)
    {
        void Add(BrainModifier modifier, int selector, decimal multiplier, DogmaAttributeId attribute = DogmaAttributeId.MiningAmount)
        {
            if (multiplier != 1)
            {
                effects.Add(new(shipId, modifier, attribute, BrainOperation.PostMultiply, [selector], [], (double)multiplier));
            }
        }
        // Every admitted ore/ice tool in build 3662168 directly requires exactly
        // one of these skills. Separate selectors keep the resource factors independent.
        Add(BrainModifier.LocationRequiredSkill, (int)MiningSkill.Mining, policy.OreYieldMultiplier);
        Add(
            BrainModifier.LocationRequiredSkill,
            (int)MiningSkill.Mining,
            policy.OreYieldMultiplier,
            DogmaAttributeId.MiningSpecialtyAmount
        );
        Add(BrainModifier.LocationRequiredSkill, (int)MiningSkill.IceHarvesting, policy.IceYieldMultiplier);
        Add(BrainModifier.LocationGroup, InventoryDefinitions.GasScoopGroup, policy.GasYieldMultiplier);
        Add(BrainModifier.LocationGroup, InventoryDefinitions.GasHarvesterGroup, policy.GasYieldMultiplier);
    }
}
