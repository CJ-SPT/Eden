using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.Plans;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Tests.Gateway;

public sealed class GatewayRequestsTests
{
    [Fact]
    public void SkillPlanResponsesCarryTheCorrectTypeAndRequestIdentity()
    {
        var gateway = new GatewayRequests(TimeProvider.System);
        var request = new Request
        {
            ApplicationInstanceUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Payload = new Any { TypeUrl = "type.googleapis.com/eve_public.character.skill.plan.GetActiveRequest" },
        };
        var response = gateway.Handle(request);
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.ApplicationInstanceUuid, response.ApplicationInstanceUuid);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Equal(new byte[16], response.Payload.Unpack<GetActiveResponse>().SkillPlan.Uuid.ToByteArray());

        request.Payload.TypeUrl = "type.googleapis.com/eve_public.character.skill.plan.GetAllRequest";
        Assert.Empty(gateway.Handle(request).Payload.Unpack<GetAllResponse>().SkillPlans);
        request.Payload.TypeUrl = "type.googleapis.com/unknown.GetAllRequest";
        Assert.Equal(501u, gateway.Handle(request).StatusCode);
    }
}
