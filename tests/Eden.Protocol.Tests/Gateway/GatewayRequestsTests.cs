using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.Plans;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Tests.Gateway;

public sealed class GatewayRequestsTests
{
    [Fact]
    public void AmsenReportsNoPirateCorruptionToTheLocationPanel()
    {
        var request = new Request
        {
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/eve_public.pirate.corruption.api.GetSystemInfoRequest",
                // system.sequential = 30001392 (Amsen).
                Value = ByteString.CopyFrom(0x0a, 0x05, 0x08, 0xf0, 0x91, 0xa7, 0x0e),
            },
        };
        var response = new GatewayRequests(TimeProvider.System).Handle(request);
        Assert.Equal(200u, response.StatusCode);
        var system = response.Payload.Unpack<Protocol.Gateway.Corruption.GetSystemInfoResponse>();
        Assert.Equal(0u, system.Stage);
        Assert.All(new[] { system.TotalProgress, system.EveContribution, system.VanguardContribution }, progress =>
        {
            Assert.Equal(0ul, progress.Numerator);
            Assert.Equal(1ul, progress.Denominator);
        });
    }

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
