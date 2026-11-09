using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Repair;

public sealed record AssistanceInputs(
    double Security,
    ShipSafetyLevel Safety,
    bool Alpha,
    CrimewatchFlag TargetFlag,
    bool TargetOutlaw,
    bool TargetEngaged,
    bool TargetInCombat = false
);

public sealed record AssistanceDecision(CrimewatchFlag Flag, bool NewOffense);

public static class AssistanceRules
{
    public static AssistanceDecision Decide(AssistanceInputs input)
    {
        if (!double.IsFinite(input.Security) || !Enum.IsDefined(input.Safety) || !Enum.IsDefined(input.TargetFlag))
        {
            throw new InvalidDataException("Invalid remote assistance inputs.");
        }
        if (input.Security <= 0)
        {
            return new(CrimewatchFlag.None, false);
        }
        bool flagged =
            input.TargetFlag != CrimewatchFlag.None
            || input.TargetEngaged
            || input.TargetOutlaw && (input.TargetInCombat || input.Security >= CrimewatchRules.HighSecurity);
        var flag =
            !flagged ? CrimewatchFlag.None
            : input.TargetFlag == CrimewatchFlag.Criminal || input.Security >= CrimewatchRules.HighSecurity ? CrimewatchFlag.Criminal
            : CrimewatchFlag.Suspect;
        var safety = input.Alpha && input.Security >= CrimewatchRules.HighSecurity ? ShipSafetyLevel.Full : input.Safety;
        if (
            flag != CrimewatchFlag.None
            && (safety == ShipSafetyLevel.Full || safety == ShipSafetyLevel.Partial && flag == CrimewatchFlag.Criminal)
        )
        {
            throw new InvalidDataException("Your ship safety setting prevents this remote assistance.");
        }
        return new(flag, input.TargetEngaged || input.TargetOutlaw && input.TargetInCombat);
    }

    public static CrimewatchTimers Apply(CrimewatchTimers source, CrimewatchTimers target, AssistanceDecision decision, long now, long end)
    {
        var result = source with
        {
            Weapons = Inherit(source.Weapons, target.Weapons),
            Pvp = Inherit(source.Pvp, target.Pvp),
            Npc = Inherit(source.Npc, target.Npc),
            Suspect = Inherit(source.Suspect, target.Suspect),
            Criminal = Inherit(source.Criminal, target.Criminal),
        };
        if (!decision.NewOffense)
        {
            return result;
        }
        if (decision.Flag == CrimewatchFlag.Criminal || decision.Flag == CrimewatchFlag.Suspect && result.Criminal.Running(now))
        {
            return result with { Criminal = CrimewatchRules.Extend(result.Criminal, end, CrimewatchRules.FlagDuration) };
        }
        return decision.Flag == CrimewatchFlag.Suspect
            ? result with
            {
                Suspect = CrimewatchRules.Extend(result.Suspect, end, CrimewatchRules.FlagDuration),
            }
            : result;
    }

    private static CombatTimer Inherit(CombatTimer source, CombatTimer target) =>
        new(Math.Max(source.ActiveUntil, target.ActiveUntil), Math.Max(source.ExpiresAt, target.ExpiresAt));
}
