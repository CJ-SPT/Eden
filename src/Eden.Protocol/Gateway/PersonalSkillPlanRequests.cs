using System.Net;
using Eden.Gameplay.Skills;
using Eden.Protocol.Gateway.Plans;
using Google.Protobuf;
using MilestoneRequests = Eden.Protocol.Gateway.Plans.Milestones;
using MilestoneTypes = Eden.Protocol.Gateway.Plans.Milestones.Shared;
using PlanTypes = Eden.Protocol.Gateway.Plans.Shared;

namespace Eden.Protocol.Gateway;

internal static class PersonalSkillPlanRequests
{
    private const string Api = "eve_public.character.skill.plan.";
    private const string MilestoneApi = Api + "milestone.";

    internal static GatewayReply? Read(
        string type,
        ByteString payload,
        AuthoritativeContext? context,
        long? account,
        IPersonalSkillPlans plans
    )
    {
        if (!type.StartsWith(Api, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            long pilotId = context?.Identity?.Character?.Sequential ?? 0;
            if (account is not long accountId || accountId <= 0 || pilotId <= 0)
            {
                return new(HttpStatusCode.Unauthorized);
            }

            var result = Dispatch(type, payload, new(accountId, pilotId), plans);
            return result is null ? null : new(HttpStatusCode.OK, result);
        }
        catch (UnauthorizedAccessException)
        {
            return new(HttpStatusCode.Unauthorized);
        }
        catch (KeyNotFoundException)
        {
            return new(HttpStatusCode.NotFound);
        }
        catch (Exception error) when (error is InvalidDataException or InvalidProtocolBufferException or OverflowException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static IMessage? Dispatch(string type, ByteString payload, SkillPlanOwner owner, IPersonalSkillPlans plans)
    {
        switch (type)
        {
            case Api + "GetAllRequest":
                _ = GetAllRequest.Parser.ParseFrom(payload);
                var list = new GetAllResponse();
                list.SkillPlans.Add(plans.List(owner).Select(WritePlanId));
                return list;
            case Api + "GetRequest":
                var get = GetRequest.Parser.ParseFrom(payload);
                return new GetResponse { SkillPlan = WriteDetails(plans.Read(owner, ReadId(get.SkillPlan?.Uuid))) };
            case Api + "CreateRequest":
                var create = CreateRequest.Parser.ParseFrom(payload);
                return new CreateResponse { SkillPlan = WritePlanId(plans.Create(owner, ReadDetails(create.SkillPlan))) };
            case Api + "SetNameRequest":
                var rename = SetNameRequest.Parser.ParseFrom(payload);
                plans.Rename(owner, ReadId(rename.SkillPlan?.Uuid), rename.Name);
                return new SetNameResponse();
            case Api + "SetDescriptionRequest":
                var describe = SetDescriptionRequest.Parser.ParseFrom(payload);
                plans.Describe(owner, ReadId(describe.SkillPlan?.Uuid), describe.Description);
                return new SetDescriptionResponse();
            case Api + "SetSkillRequirementsRequest":
                var setSkills = SetSkillRequirementsRequest.Parser.ParseFrom(payload);
                plans.SetSkills(owner, ReadId(setSkills.SkillPlan?.Uuid), setSkills.Requirements.Select(ReadSkill).ToArray());
                return new SetSkillRequirementsResponse();
            case Api + "DeleteRequest":
                var delete = DeleteRequest.Parser.ParseFrom(payload);
                plans.Delete(owner, ReadId(delete.SkillPlan?.Uuid));
                return new DeleteResponse();
            case Api + "GetActiveRequest":
                _ = GetActiveRequest.Parser.ParseFrom(payload);
                var tracked = plans.ReadTracked(owner);
                return new GetActiveResponse
                {
                    SkillPlan = WritePlanId(tracked.Id),
                    SkillPlanInfo = tracked.Details is { } details ? WriteDetails(details) : null,
                };
            case Api + "SetActiveRequest":
                var track = SetActiveRequest.Parser.ParseFrom(payload);
                plans.Track(owner, ReadId(track.SkillPlan?.Uuid, allowZero: true));
                return new SetActiveResponse();
            case MilestoneApi + "GetAllRequest":
                var getMilestones = MilestoneRequests.GetAllRequest.Parser.ParseFrom(payload);
                var milestones = new MilestoneRequests.GetAllResponse();
                milestones.Milestones.Add(plans.Milestones(owner, ReadId(getMilestones.SkillPlan?.Uuid)).Select(WriteMilestone));
                return milestones;
            case MilestoneApi + "CreateRequest":
                var createMilestone = MilestoneRequests.CreateRequest.Parser.ParseFrom(payload);
                var milestone = createMilestone.Milestone ?? throw new InvalidDataException("Choose a milestone.");
                var milestoneId = plans.AddMilestone(
                    owner,
                    ReadId(milestone.SkillPlan?.Uuid),
                    ReadTarget(milestone),
                    milestone.Description
                );
                return new MilestoneRequests.CreateResponse { Milestone = WriteMilestoneId(milestoneId) };
            case MilestoneApi + "SetDescriptionRequest":
                var describeMilestone = MilestoneRequests.SetDescriptionRequest.Parser.ParseFrom(payload);
                plans.DescribeMilestone(owner, ReadId(describeMilestone.Identifier?.Uuid), describeMilestone.Description);
                return new MilestoneRequests.SetDescriptionResponse();
            case MilestoneApi + "DeleteRequest":
                var deleteMilestone = MilestoneRequests.DeleteRequest.Parser.ParseFrom(payload);
                plans.DeleteMilestone(owner, ReadId(deleteMilestone.Milestone?.Uuid));
                return new MilestoneRequests.DeleteResponse();
            default:
                return null;
        }
    }

    private static Guid ReadId(ByteString? bytes, bool allowZero = false)
    {
        if (bytes is not { Length: 16 })
        {
            throw new InvalidDataException("Choose a valid skill plan or milestone ID.");
        }

        // The pinned client uses the network order uuid.UUID.bytes representation.
        var id = new Guid(bytes.Span, bigEndian: true);
        if (!allowZero && id == Guid.Empty)
        {
            throw new InvalidDataException("Choose a valid skill plan or milestone ID.");
        }

        return id;
    }

    private static PlanTypes.Identifier WritePlanId(Guid id) => new() { Uuid = ByteString.CopyFrom(id.ToByteArray(bigEndian: true)) };

    private static MilestoneTypes.Identifier WriteMilestoneId(Guid id) =>
        new() { Uuid = ByteString.CopyFrom(id.ToByteArray(bigEndian: true)) };

    private static SkillPlanDetails ReadDetails(PlanTypes.Attributes? attributes) =>
        attributes is null
            ? throw new InvalidDataException("Choose the skill plan details.")
            : new(attributes.Name, attributes.Description, attributes.SkillRequirements.Select(ReadSkill).ToArray());

    private static PlanTypes.Attributes WriteDetails(SkillPlanDetails details)
    {
        var attributes = new PlanTypes.Attributes { Name = details.Name, Description = details.Description };
        attributes.SkillRequirements.Add(details.Skills.Select(WriteSkill));
        return attributes;
    }

    private static int ReadTypeId(ulong? id) =>
        id is > 0 and <= int.MaxValue ? (int)id.Value : throw new InvalidDataException("Choose a valid type ID.");

    private static SkillPlanSkill ReadSkill(PlanTypes.SkillRequirement skill) =>
        new(ReadTypeId(skill.SkillType?.Sequential), checked((int)skill.Level));

    private static PlanTypes.SkillRequirement WriteSkill(SkillPlanSkill skill) =>
        new()
        {
            SkillType = new() { Sequential = checked((ulong)skill.TypeId) },
            Level = checked((uint)skill.Level),
        };

    private static SkillPlanMilestoneTarget ReadTarget(MilestoneTypes.Attributes milestone)
    {
        switch (milestone.MilestoneCase)
        {
            case MilestoneTypes.Attributes.MilestoneOneofCase.TrainToType:
                return new(ReadTypeId(milestone.TrainToType.Sequential), null);
            case MilestoneTypes.Attributes.MilestoneOneofCase.Skill:
                var skill = ReadSkill(milestone.Skill);
                return new(skill.TypeId, skill.Level);
            default:
                throw new InvalidDataException("Choose a milestone item or skill.");
        }
    }

    private static MilestoneRequests.GetAllResponse.Types.Milestone WriteMilestone(PersonalSkillMilestone milestone)
    {
        var attributes = new MilestoneTypes.Attributes { SkillPlan = WritePlanId(milestone.PlanId), Description = milestone.Description };
        if (milestone.Target.Level is int level)
        {
            attributes.Skill = WriteSkill(new(milestone.Target.TypeId, level));
        }
        else
        {
            attributes.TrainToType = new() { Sequential = checked((ulong)milestone.Target.TypeId) };
        }

        return new() { Identifier = WriteMilestoneId(milestone.Id), Data = attributes };
    }
}
