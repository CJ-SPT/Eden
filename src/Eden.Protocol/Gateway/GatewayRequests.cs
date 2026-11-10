using Eden.Infrastructure.Registration;
using Eden.Protocol.Gateway.Plans;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Gateway;

[Injectable(InjectionType.Singleton)]
public sealed class GatewayRequests(TimeProvider clock)
{
    private readonly ByteString _instanceId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
    private const string PersonalPlans = "eve_public.character.skill.plan.";

    public Response Handle(Request request)
    {
        string type = Any.GetTypeName(request.Payload?.TypeUrl ?? "");
        IMessage? result = type switch
        {
            // The stock client compares the untracked plan to UUID(int=0).
            PersonalPlans + "GetActiveRequest" => new GetActiveResponse
            {
                SkillPlan = new Identifier { Uuid = ByteString.CopyFrom(new byte[16]) },
            },
            PersonalPlans + "GetAllRequest" => new GetAllResponse(),
            // Location Info reads the local corruption stage before drawing even a high-security system.
            "eve_public.pirate.corruption.api.GetSystemInfoRequest" => new Corruption.GetSystemInfoResponse
            {
                TotalProgress = new Fraction { Denominator = 1 },
                EveContribution = new Fraction { Denominator = 1 },
                VanguardContribution = new Fraction { Denominator = 1 },
            },
            // Local accounts have no PLEX holdings or purchasing service. A valid
            // zero balance avoids the stock wallet's failure callback re-entering
            // PersonalWalletPanel.Load while its tabs are still being constructed.
            "eve_public.plex.vault.api.BalanceRequest" => new PlexVault.BalanceResponse { Balance = new Plex.Currency() },
            // Corporation Show Info has a Freelance Projects tab. Eden publishes
            // no projects, so the repository receives a successful empty collection.
            "eve_public.freelance.project.api.GetAllActiveForCorporationRequest" => new Freelance.GetAllActiveForCorporationResponse(),
            _ => null,
        };
        var response = new Response
        {
            StatusCode = result is null ? 501u : 200u,
            CorrelationUuid = request.CorrelationUuid,
            ApplicationInstanceUuid = request.ApplicationInstanceUuid,
            GatewayInstanceUuid = _instanceId,
            InternalOrigin = "eden",
            Dispatched = Timestamp.FromDateTimeOffset(clock.GetUtcNow()),
        };
        if (result is not null)
        {
            response.Payload = new Any
            {
                TypeUrl = request.Payload!.TypeUrl[..^"Request".Length] + "Response",
                Value = result.ToByteString(),
            };
        }
        return response;
    }
}
