using Eden.Data.Catalog;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.Plans;
using Eden.Protocol.Gateway.Plans.Shared;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using MilestoneAttributes = Eden.Protocol.Gateway.Plans.Milestones.Shared.Attributes;
using MilestoneIdentifier = Eden.Protocol.Gateway.Plans.Milestones.Shared.Identifier;
using Milestones = Eden.Protocol.Gateway.Plans.Milestones;

namespace Eden.Protocol.Tests.Gateway;

public sealed class PersonalSkillPlanGatewayTests
{
    private const long OwnerAccount = 1000001;
    private const long OtherAccount = 1000002;
    private const uint OwnerPilot = 90000001;
    private const uint OtherPilot = 90000002;
    private const int SpaceshipCommand = 3327;
    private const int CaldariFrigate = 3330;
    private const int Navigation = 3449;
    private const int Merlin = 603;
    private static readonly ByteString s_untracked = ByteString.CopyFrom(new byte[16]);

    [Fact]
    public async Task PersonalPlanAndMilestonesRetainNativeOrderAndTrackingAcrossCheckpoint()
    {
        using TestWorld world = new(liveWorld: true);
        WorldDatabase database = LoadCatalog(world);
        LoadCertifiedPlan(database.CatalogPath);
        var gateway = world.Services.GetRequiredService<GatewayRequests>();
        Client own = new(gateway, OwnerAccount, OwnerPilot);
        Client other = new(gateway, OtherAccount, OtherPilot);
        var initial = new Attributes
        {
            Name = "Frigate training",
            Description = "Prepare to fly a Merlin",
            SkillRequirements = { Skill(SpaceshipCommand, 1), Skill(Navigation, 1), Skill(SpaceshipCommand, 2) },
        };
        var plan = own.Send<CreateResponse>(new CreateRequest { SkillPlan = initial }).SkillPlan;
        AssertUuid(plan.Uuid);
        Assert.Equal(plan, Assert.Single(own.Send<GetAllResponse>(new GetAllRequest()).SkillPlans));
        Assert.Equal(initial, own.Send<GetResponse>(new GetRequest { SkillPlan = plan }).SkillPlan);
        Assert.Empty(other.Send<GetAllResponse>(new GetAllRequest()).SkillPlans);
        other.Reject(new GetRequest { SkillPlan = plan }, 404);
        other.Reject(new SetNameRequest { SkillPlan = plan, Name = "Foreign edit" }, 404);
        other.Reject(new SetActiveRequest { SkillPlan = plan }, 404);

        var updated = new Attributes
        {
            Name = "Merlin and navigation",
            Description = "Train navigation between the frigate levels",
            SkillRequirements = { Skill(CaldariFrigate, 1), Skill(Navigation, 2), Skill(CaldariFrigate, 2), Skill(CaldariFrigate, 3) },
        };
        var setSkills = new SetSkillRequirementsRequest { SkillPlan = plan };
        setSkills.Requirements.Add(updated.SkillRequirements);
        await Task.WhenAll(
            Task.Run(() => own.Send<SetNameResponse>(new SetNameRequest { SkillPlan = plan, Name = updated.Name })),
            Task.Run(() =>
                own.Send<SetDescriptionResponse>(new SetDescriptionRequest { SkillPlan = plan, Description = updated.Description })
            ),
            Task.Run(() => own.Send<SetSkillRequirementsResponse>(setSkills))
        );
        SetActiveRequest track = new() { SkillPlan = plan };
        own.Send<SetActiveResponse>(track);

        MilestoneAttributes ship = new()
        {
            SkillPlan = plan,
            TrainToType = new() { Sequential = Merlin },
            Description = "Unlock the Merlin",
        };
        MilestoneAttributes training = new()
        {
            SkillPlan = plan,
            Skill = Skill(Navigation, 2),
            Description = "Navigation II",
        };
        var shipId = own.Send<Milestones.CreateResponse>(new Milestones.CreateRequest { Milestone = ship }).Milestone;
        var skillId = own.Send<Milestones.CreateResponse>(new Milestones.CreateRequest { Milestone = training }).Milestone;
        AssertUuid(shipId.Uuid);
        AssertUuid(skillId.Uuid);
        Assert.NotEqual(shipId, skillId);
        training.Description = "Complete Navigation II";
        own.Send<Milestones.SetDescriptionResponse>(
            new Milestones.SetDescriptionRequest { Identifier = skillId, Description = training.Description }
        );
        var listMilestones = new Milestones.GetAllRequest { SkillPlan = plan };
        other.Reject(listMilestones, 404);
        other.Reject(new Milestones.SetDescriptionRequest { Identifier = skillId, Description = "Foreign milestone edit" }, 404);
        Assert.Equal(s_untracked, other.Send<GetActiveResponse>(new GetActiveRequest()).SkillPlan.Uuid);
        // Build 3662168 encodes the certified plan's catalog key in the low bytes of the UUID.
        Identifier certified = new() { Uuid = ByteString.CopyFrom(Convert.FromHexString("00000000000000000000000000000021")) };
        Identifier unknownCertified = new() { Uuid = ByteString.CopyFrom(Convert.FromHexString("00000000000000000000000000000002")) };
        other.Send<SetActiveResponse>(new SetActiveRequest { SkillPlan = certified });
        other.Reject(new SetActiveRequest { SkillPlan = unknownCertified }, 404);

        await world.Services.GetRequiredService<LiveWorld>().FlushAsync();
        world.Services.Dispose();
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, liveWorld: true);
        var reopenedGateway = reopened.GetRequiredService<GatewayRequests>();
        Client certifiedPilot = new(reopenedGateway, OtherAccount, OtherPilot);
        var certifiedTracking = certifiedPilot.Send<GetActiveResponse>(new GetActiveRequest());
        Assert.Equal(certified, certifiedTracking.SkillPlan);
        Assert.Null(certifiedTracking.SkillPlanInfo);
        Assert.Empty(certifiedPilot.Send<GetAllResponse>(new GetAllRequest()).SkillPlans);

        Client restored = new(reopenedGateway, OwnerAccount, OwnerPilot);
        Assert.Equal(plan, Assert.Single(restored.Send<GetAllResponse>(new GetAllRequest()).SkillPlans));
        Assert.Equal(updated, restored.Send<GetResponse>(new GetRequest { SkillPlan = plan }).SkillPlan);
        var active = restored.Send<GetActiveResponse>(new GetActiveRequest());
        Assert.Equal(plan, active.SkillPlan);
        Assert.Equal(updated, active.SkillPlanInfo);
        var milestones = restored.Send<Milestones.GetAllResponse>(listMilestones).Milestones;
        Assert.Equal(2, milestones.Count);
        Assert.Equal(ship, Assert.Single(milestones, milestone => milestone.Identifier.Equals(shipId)).Data);
        Assert.Equal(training, Assert.Single(milestones, milestone => milestone.Identifier.Equals(skillId)).Data);

        restored.Send<Milestones.DeleteResponse>(new Milestones.DeleteRequest { Milestone = shipId });
        Assert.Equal(skillId, Assert.Single(restored.Send<Milestones.GetAllResponse>(listMilestones).Milestones).Identifier);
        restored.Send<SetActiveResponse>(new SetActiveRequest { SkillPlan = new() { Uuid = s_untracked } });
        AssertUntracked(restored);
        Assert.Equal(plan, Assert.Single(restored.Send<GetAllResponse>(new GetAllRequest()).SkillPlans));
        restored.Send<SetActiveResponse>(track);
        restored.Send<DeleteResponse>(new DeleteRequest { SkillPlan = plan });
        AssertUntracked(restored);
        Assert.Empty(restored.Send<GetAllResponse>(new GetAllRequest()).SkillPlans);
        restored.Reject(new GetRequest { SkillPlan = plan }, 404);
        restored.Reject(new Milestones.SetDescriptionRequest { Identifier = skillId, Description = "Deleted with plan" }, 404);
    }

    [Fact]
    public void InvalidChangesKeepPlanContentsAndEnforceNativeLimits()
    {
        using TestWorld world = new(liveWorld: true);
        LoadCatalog(world);
        Client own = new(world.Services.GetRequiredService<GatewayRequests>(), OwnerAccount, OwnerPilot);
        own.Reject(new CreateRequest { SkillPlan = new() { Name = new string('n', 51) } }, 400);
        own.Reject(new SetActiveRequest { SkillPlan = new() { Uuid = ByteString.CopyFrom(new byte[15]) } }, 400);

        var valid = new Attributes { Name = "Plan at capacity", SkillRequirements = { Skill(Navigation, 1) } };
        var plan = own.Send<CreateResponse>(new CreateRequest { SkillPlan = valid }).SkillPlan;
        own.Reject(new SetDescriptionRequest { SkillPlan = plan, Description = new string('d', 1001) }, 400);
        own.Reject(new SetSkillRequirementsRequest { SkillPlan = plan, Requirements = { Skill(Navigation, 6) } }, 400);
        own.Reject(new SetSkillRequirementsRequest { SkillPlan = plan, Requirements = { Skill(Merlin, 1) } }, 400);
        own.Reject(new Milestones.CreateRequest { Milestone = new() { SkillPlan = plan } }, 400);
        own.Reject(
            new Milestones.CreateRequest
            {
                Milestone = new()
                {
                    SkillPlan = plan,
                    TrainToType = new() { Sequential = int.MaxValue },
                },
            },
            400
        );
        Assert.Equal(valid, own.Send<GetResponse>(new GetRequest { SkillPlan = plan }).SkillPlan);

        var requirements = new SetSkillRequirementsRequest { SkillPlan = plan };
        requirements.Requirements.Add(Enumerable.Range(0, 150).Select(index => Skill(Navigation, (uint)(index % 5 + 1))));
        own.Send<SetSkillRequirementsResponse>(requirements);
        var expected = requirements.Requirements.ToArray();
        requirements.Requirements.Add(Skill(CaldariFrigate, 1));
        own.Reject(requirements, 400);
        Assert.Equal(expected, own.Send<GetResponse>(new GetRequest { SkillPlan = plan }).SkillPlan.SkillRequirements);

        MilestoneIdentifier[] milestones = Enumerable.Range(1, 5).Select(level => AddMilestone((uint)level)).ToArray();
        own.Reject(
            new Milestones.CreateRequest
            {
                Milestone = new() { SkillPlan = plan, Skill = Skill(CaldariFrigate, 1) },
            },
            400
        );
        var listed = own.Send<Milestones.GetAllResponse>(new Milestones.GetAllRequest { SkillPlan = plan }).Milestones;
        Assert.Equal(5, listed.Count);
        Assert.All(milestones, id => Assert.Contains(listed, row => row.Identifier.Equals(id)));

        for (int number = 2; number <= 10; number++)
        {
            own.Send<CreateResponse>(new CreateRequest { SkillPlan = new() { Name = $"Plan {number}" } });
        }
        own.Reject(new CreateRequest { SkillPlan = new() { Name = "Plan 11" } }, 400);
        Assert.Equal(10, own.Send<GetAllResponse>(new GetAllRequest()).SkillPlans.Count);

        MilestoneIdentifier AddMilestone(uint level) =>
            own.Send<Milestones.CreateResponse>(
                new Milestones.CreateRequest
                {
                    Milestone = new() { SkillPlan = plan, Skill = Skill(Navigation, level) },
                }
            ).Milestone;
    }

    private static WorldDatabase LoadCatalog(TestWorld world)
    {
        WorldDatabase database = new(world.Path);
        PassiveCatalogFixture.LoadSmallHulls(
            database.CatalogPath,
            new HashSet<int> { SpaceshipCommand, CaldariFrigate, Navigation, Merlin }
        );
        return database;
    }

    private static void LoadCertifiedPlan(string path)
    {
        using CatalogDbContext catalog = new(path, writable: true);
        catalog.Add(
            new SourceRecord
            {
                Dataset = CatalogData.Client.Skillplans.Name,
                Key = "33",
                Data = """
                {"descriptionID":585731,"skillRequirements":[{"typeID":3392,"level":1},{"typeID":3393,"level":1},
                {"typeID":3393,"level":2}],"milestones":[{"typeID":523},{"typeID":3393,"level":2}],"nameID":585730,
                "name":"AIR - Repair Systems"}
                """,
            }
        );
        catalog.SaveChanges();
    }

    private static void AssertUuid(ByteString uuid)
    {
        Assert.Equal(16, uuid.Length);
        Assert.NotEqual(s_untracked, uuid);
    }

    private static void AssertUntracked(Client client)
    {
        var tracked = client.Send<GetActiveResponse>(new GetActiveRequest());
        Assert.Equal(s_untracked, tracked.SkillPlan.Uuid);
        Assert.Null(tracked.SkillPlanInfo);
    }

    private static SkillRequirement Skill(uint type, uint level) =>
        new()
        {
            SkillType = new() { Sequential = type },
            Level = level,
        };

    private sealed class Client(GatewayRequests gateway, long accountId, uint pilotId)
    {
        public T Send<T>(IMessage payload)
            where T : class, IMessage<T>, new()
        {
            var response = Call(payload);
            Assert.Equal(200u, response.StatusCode);
            return response.Payload.Unpack<T>();
        }

        public void Reject(IMessage payload, uint status)
        {
            var response = Call(payload);
            Assert.Equal(status, response.StatusCode);
            Assert.Null(response.Payload);
        }

        private Response Call(IMessage payload)
        {
            Request request = new()
            {
                CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                AuthoritativeContext = new() { Identity = new() { Character = new() { Sequential = pilotId } } },
                Payload = Any.Pack(payload),
            };
            return Response.Parser.ParseFrom(gateway.Handle(Request.Parser.ParseFrom(request.ToByteArray()), accountId).ToByteArray());
        }
    }
}
