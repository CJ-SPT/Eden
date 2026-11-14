using System.Net;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Maps;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Gateway.Plans;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Gateway;

internal sealed record GatewayReply(HttpStatusCode Status, IMessage? Payload = null);

[Injectable(InjectionType.Singleton)]
public sealed class GatewayRequests(
    TimeProvider clock,
    ICelestialResourceDirectory celestialResources,
    ICharacterDirectory characters,
    IPlayerCorporations corporations
)
{
    private readonly ByteString _instanceId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
    private const string PersonalPlans = "eve_public.character.skill.plan.";
    private const string FreelanceProjects = "eve_public.freelance.project.api.";

    public Response Handle(Request request)
    {
        string type = Any.GetTypeName(request.Payload?.TypeUrl ?? "");
        var payload = request.Payload?.Value ?? ByteString.Empty;
        var reply =
            CorporationPaletteRequests.Read(type, payload, request.AuthoritativeContext, characters, corporations)
            ?? CelestialResourceRequests.Read(type, payload, celestialResources);
        IMessage? result =
            reply?.Payload
            ?? (
                type switch
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
                    // Opportunities and Corporation Show Info share the freelance repository. Eden publishes no projects.
                    FreelanceProjects + "GetAllActiveForCorporationRequest" => new Freelance.GetAllActiveForCorporationResponse(),
                    FreelanceProjects + "GetAllBroadcastedRequest" => new Freelance.GetAllBroadcastedResponse(),
                    FreelanceProjects + "GetAllCommittedRequest" => new Freelance.GetAllCommittedResponse(),
                    FreelanceProjects + "GetAllUnredeemedRequest" => new Freelance.GetAllUnredeemedResponse(),
                    // The empty-space menu primes the activity cache synchronously until this read succeeds.
                    "eve_public.sovereignty.mercenaryden.activity.api.GetAllRequest" => new MercenaryDenActivity.GetAllResponse(),
                    _ => null,
                }
            );
        var response = new Response
        {
            StatusCode =
                reply is not null ? (uint)reply.Status
                : result is null ? 501u
                : 200u,
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
