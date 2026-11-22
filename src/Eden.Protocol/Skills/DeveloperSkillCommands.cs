using System.Globalization;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class DeveloperSkillCommands(IPilotSkillGrants grants, IPilotDeveloperSkills developer, FreeSkillPointHandler points)
{
    internal PilotCallResult Grant(PilotCallContext context, string[] command)
    {
        long caller = context.Pilot.PilotId;
        points.Enter(context);
        switch (command)
        {
            case ["/giveskill", var subject, var type, var level]
                when int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out int targetLevel)
                    && targetLevel is >= 0 and <= 5:
                long pilot =
                    subject.Equals("me", StringComparison.OrdinalIgnoreCase) ? caller
                    : long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id
                    : 0;
                if (pilot > 0 && int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out int typeId) && typeId > 0)
                {
                    var result = grants.Grant(pilot, new(typeId, targetLevel));
                    if (pilot == caller && (result.Allocations.Count > 0 || result.Training.ChangedSkills.Count > 0))
                    {
                        points.Notify(context, result.Remaining, result.Revision, result.Training);
                    }
                }
                else
                {
                    Execute(subject, new DeveloperSkillAction.Give(type, targetLevel));
                }
                break;
            case ["/removeskill", var subject, var type]:
                Execute(subject, new DeveloperSkillAction.Remove(type));
                break;
            case ["/givestarterskills", var subject]:
                Execute(subject, new DeveloperSkillAction.Starting());
                break;
            case ["/skillscopy", var source, var target]:
                Execute(target, new DeveloperSkillAction.CopyFrom(source));
                break;
            default:
                throw new InvalidDataException("Supply a pilot, skill name or ID, and level as required by the skill command.");
        }
        return PilotCallResult.None;

        void Execute(string target, DeveloperSkillAction action)
        {
            var result = developer.Execute(caller, target, action);
            if (result.PilotId == caller)
            {
                points.Notify(context, result.Balance, result.Revision, result.Training, result.Removed);
            }
        }
    }
}
