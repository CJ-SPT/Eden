using System.Collections.Concurrent;
using System.Net;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Maps;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Eden.Protocol.Gateway;

internal sealed record GatewayReply(HttpStatusCode Status, IMessage? Payload = null);

[Injectable(InjectionType.Singleton)]
public sealed class GatewayRequests(
    TimeProvider clock,
    ICelestialResourceDirectory celestialResources,
    ICharacterDirectory characters,
    IPlayerCorporations corporations,
    Eden.Gameplay.Sovereignty.ISovereigntyResources? sovereignty = null,
    Eden.Protocol.Sovereignty.SovereigntyGatewayUpdates? sovereigntyUpdates = null,
    IFactionWarzones? factionWarzones = null,
    IPersonalSkillPlans? personalSkillPlans = null,
    ShipSkinGateway? shipSkins = null
)
{
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<int, MilitiaAdvantageState>> _factionObservations = new();

    private readonly ByteString _instanceId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
    private const string FreelanceProjects = "eve_public.freelance.project.api.";

    public Response Handle(Request request, long? authenticatedAccount = null)
    {
        string type = Any.GetTypeName(request.Payload?.TypeUrl ?? "");
        var payload = request.Payload?.Value ?? ByteString.Empty;
        var reply =
            (
                sovereignty is null
                    ? null
                    : SovereigntyResourceRequests.Read(
                        type,
                        payload,
                        request.AuthoritativeContext,
                        authenticatedAccount,
                        sovereignty,
                        celestialResources,
                        sovereigntyUpdates
                    )
            )
            ?? (
                factionWarzones is null
                    ? null
                    : FactionActivityRequests.Read(
                        type,
                        payload,
                        factionWarzones,
                        clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                        ObserveFactionScores
                    )
            )
            ?? (
                personalSkillPlans is null
                    ? null
                    : PersonalSkillPlanRequests.Read(type, payload, request.AuthoritativeContext, authenticatedAccount, personalSkillPlans)
            )
            ?? shipSkins?.Read(type, payload, request.AuthoritativeContext, authenticatedAccount)
            ?? CorporationPaletteRequests.Read(type, payload, request.AuthoritativeContext, characters, corporations)
            ?? CelestialResourceRequests.Read(type, payload, celestialResources);
        IMessage? result =
            reply?.Payload
            ?? (
                type switch
                {
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

        void ObserveFactionScores(MilitiaAdvantageState scores)
        {
            if (authenticatedAccount is long account)
            {
                _factionObservations.GetOrAdd(account, static _ => new())[scores.SystemId] = scores;
            }
        }
    }

    public IReadOnlyList<Notice> SovereigntyNotices(long accountId) => sovereigntyUpdates?.Notices(accountId) ?? [];

    public IReadOnlyList<Notice> ShipSkinNotices(long accountId, ShipSkinNoticeCursor cursor) =>
        shipSkins?.Notices(accountId, cursor) ?? [];

    public IReadOnlyList<Notice> FactionActivityNotices(long accountId)
    {
        if (factionWarzones is null)
        {
            return [];
        }

        var now = clock.GetUtcNow();
        long time = now.UtcDateTime.ToFileTimeUtc();
        List<Notice> result = factionWarzones
            .AdvantageContributions(accountId, time)
            .Select(contribution => Wrap(FactionActivityRequests.Contribution(contribution)))
            .ToList();
        if (!_factionObservations.TryGetValue(accountId, out var systems))
        {
            return result;
        }
        foreach (var (system, before) in systems)
        {
            var current = factionWarzones.Advantage(system, time);
            if (current is not null && current != before && systems.TryUpdate(system, current, before))
            {
                result.Add(Wrap(FactionActivityRequests.Changed(current)));
            }
        }
        return result;

        Notice Wrap(IMessage message) =>
            new()
            {
                Dispatched = Timestamp.FromDateTimeOffset(now),
                Uuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
                InternalOrigin = "eden",
                Payload = Any.Pack(message),
            };
    }

    public void ForgetSovereigntyNotices(long accountId) => sovereigntyUpdates?.Forget(accountId);
}
