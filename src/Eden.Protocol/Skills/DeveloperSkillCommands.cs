using System.Globalization;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class DeveloperSkillCommands(IPilotSkillGrants grants, FreeSkillPointHandler points)
{
    internal PilotCallResult Grant(PilotCallContext context, string[] command)
    {
        if ((context.Roles & (long)PilotRole.GameMasterHigh) == 0)
        {
            throw new InvalidDataException("Giving skills requires a high game-master role.");
        }
        if (
            command is not [_, var subject, var type, var level]
            || !int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out int typeId)
            || typeId <= 0
            || !int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out int targetLevel)
            || targetLevel is < 0 or > 5
        )
        {
            throw new InvalidDataException("Use /giveskill me|pilotID skillTypeID level (zero to five).");
        }
        long pilot = context.Pilot.PilotId;
        if (!subject.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(subject, NumberStyles.None, CultureInfo.InvariantCulture, out pilot) || pilot <= 0)
            {
                throw new InvalidDataException("Choose me or a saved pilot ID.");
            }
        }
        // Establish the caller's observed balance before committing a self grant.
        points.Enter(context);
        var result = grants.Grant(pilot, new(typeId, targetLevel));
        if (pilot == context.Pilot.PilotId && (result.Allocations.Count > 0 || result.Training.ChangedSkills.Count > 0))
        {
            points.Notify(context, result.Remaining, result.Revision, result.Training);
        }
        return PilotCallResult.None;
    }
}
