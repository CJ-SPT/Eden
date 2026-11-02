using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Skills;

[Injectable]
internal sealed class SkillHandler(IPilotSkillReader skills) : ISelectedPilotCallHandler
{
    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        return (service, method) switch
        {
            ("skillMgr2", "GetMySkillHandler") => context.Reference("@skills"),
            ("@skills", "GetSkills" or "GetAllSkills") when args.Length == 0 =>
                skills.ReadSkills(context.Pilot.PilotId).ToDictionary(s => (long)s.TypeId, s => (object?)new BlueConstructor(
                    "characterskills.common.character_skill_entry.CharacterSkillEntry", [s.TypeId, s.Level, s.Points, s.Rank, null])),
            ("expertSystemMgr", "GetMyExpertSystems") => new Dictionary<long, object?>(),
            ("@skills", "GetAttributes") => PilotProfile.TrainingAttributes(),
            ("@skills", "GetFreeSkillPoints") => 0,
            ("@skills", "GetSkillHistory") => new List<object?>(),
            ("@skills", "GetSkillQueueAndFreePoints") => new object?[] { new List<object?>(), 0 },
            ("@skills", "GetBoosters" or "GetImplants") => new Dictionary<long, object?>(),
            ("@skills", "CheckAndSendNotifications") => null,
            _ => PilotCallRouter.Unsupported,
        };
    }
}
