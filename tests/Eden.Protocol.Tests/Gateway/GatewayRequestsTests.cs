using Eden.Gameplay.Corporations;
using Eden.Protocol.Gateway;
using Eden.Protocol.Gateway.Plans;
using Eden.Protocol.Tests.PilotSessions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Gateway;

public sealed class GatewayRequestsTests
{
    private const string PaletteApi = "type.googleapis.com/eve_public.cosmetic.corporation.palette.api.";
    private const string MercenaryDenActivityApi = "type.googleapis.com/eve_public.sovereignty.mercenaryden.activity.api.";

    [Fact]
    public void EmptySpaceMenuReceivesASuccessfulEmptyMercenaryDenActivityList()
    {
        using TestWorld world = new();
        var gateway = world.Services.GetRequiredService<GatewayRequests>();
        var request = new Request
        {
            ApplicationInstanceUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            AuthoritativeContext = new AuthoritativeContext
            {
                Identity = new ActiveIdentity { Character = new CharacterIdentifier { Sequential = 90000001 } },
            },
            Payload = new Any { TypeUrl = MercenaryDenActivityApi + "GetAllRequest" },
        };

        var response = Response.Parser.ParseFrom(gateway.Handle(request).ToByteArray());
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.ApplicationInstanceUuid, response.ApplicationInstanceUuid);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Equal(MercenaryDenActivityApi + "GetAllResponse", response.Payload.TypeUrl);
        Assert.Empty(response.Payload.Unpack<Protocol.Gateway.MercenaryDenActivity.GetAllResponse>().ToByteArray());

        request.Payload.TypeUrl = MercenaryDenActivityApi + "StartRequest";
        var unsupported = gateway.Handle(request);
        Assert.Equal(501u, unsupported.StatusCode);
        Assert.Null(unsupported.Payload);
    }

    [Theory]
    [InlineData("Get", 672, "0A0E0A08089601108001185918012801")]
    [InlineData("GetOwn", 672, "0A0E0A08089601108001185918012801")]
    [InlineData("GetOwn", null, "0A0F0A0908FF0110FF0118FF0118012801")]
    public void PlayerCorporationShowInfoReceivesTheNativeBannerPalette(string route, int? logoColor, string expected)
    {
        using TestWorld world = new();
        PlayerCorporationTests.Prepare(world);
        var details = PlayerCorporationTests.Details() with { Logo = new(415, null, null, logoColor, null, null, null) };
        var corporation = world.Services.GetRequiredService<IPlayerCorporations>().Create(90000001, details).Corporation;
        Assert.Equal(CorporationRules.FirstCorporationId, corporation.Id);

        // Build 3662168 descriptors encode the public corporation ID under identifier.corporation.sequential
        // and the viewing pilot under authoritative_context.identity.character.sequential.
        var request = Request.Parser.ParseFrom(
            Convert.FromHexString(route == "GetOwn" ? "320952070A05088195F52A" : "320952070A05088295F52A")
        );
        request.ApplicationInstanceUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
        request.CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray());
        request.Payload = new Any
        {
            TypeUrl = PaletteApi + route + "Request",
            Value = route == "GetOwn" ? ByteString.Empty : ByteString.CopyFrom(Convert.FromHexString("0A070A050880B9DD2E")),
        };
        var response = Response.Parser.ParseFrom(world.Services.GetRequiredService<GatewayRequests>().Handle(request).ToByteArray());
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.ApplicationInstanceUuid, response.ApplicationInstanceUuid);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Equal(PaletteApi + route + "Response", response.Payload.TypeUrl);
        Assert.Equal(Convert.FromHexString(expected), response.Payload.Value.ToByteArray());
    }

    [Theory]
    [InlineData("Get", "0AFF", false, 400u)]
    [InlineData("Get", "", false, 400u)]
    [InlineData("Get", "0A070A050881B9DD2E", false, 404u)]
    [InlineData("GetOwn", "", false, 400u)]
    [InlineData("GetOwn", "", true, 404u)]
    [InlineData("Set", "", true, 501u)]
    public void PaletteReadsValidateTargetsAndLeaveWritesUnsupported(string route, string payload, bool activePilot, uint status)
    {
        using TestWorld world = new();
        var request = new Request
        {
            AuthoritativeContext = activePilot
                ? new AuthoritativeContext
                {
                    Identity = new ActiveIdentity { Character = new CharacterIdentifier { Sequential = 90000002 } },
                }
                : null,
            Payload = new Any { TypeUrl = PaletteApi + route + "Request", Value = ByteString.CopyFrom(Convert.FromHexString(payload)) },
        };
        var response = world.Services.GetRequiredService<GatewayRequests>().Handle(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Payload);
    }

    [Fact]
    public void CorporationFreelanceTabReceivesAnEmptyNativeProjectCollection()
    {
        using TestWorld world = new();
        var request = new Request
        {
            ApplicationInstanceUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Payload = new Any { TypeUrl = "type.googleapis.com/eve_public.freelance.project.api.GetAllActiveForCorporationRequest" },
        };
        var response = world.Services.GetRequiredService<GatewayRequests>().Handle(request);
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.ApplicationInstanceUuid, response.ApplicationInstanceUuid);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Equal("type.googleapis.com/eve_public.freelance.project.api.GetAllActiveForCorporationResponse", response.Payload.TypeUrl);
        Assert.Empty(response.Payload.Unpack<Protocol.Gateway.Freelance.GetAllActiveForCorporationResponse>().Projects);
        Assert.Empty(response.Payload.Value);
    }

    [Fact]
    public void LocalPlexBalanceUsesTheNativeSuccessShapeInsteadOfWalletFailureCallback()
    {
        using TestWorld world = new();
        var request = new Request
        {
            CorrelationUuid = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()),
            Payload = new Any { TypeUrl = "type.googleapis.com/eve_public.plex.vault.api.BalanceRequest" },
        };
        var response = world.Services.GetRequiredService<GatewayRequests>().Handle(request);
        Assert.Equal(200u, response.StatusCode);
        Assert.Equal(request.CorrelationUuid, response.CorrelationUuid);
        Assert.Equal("type.googleapis.com/eve_public.plex.vault.api.BalanceResponse", response.Payload.TypeUrl);
        var balance = response.Payload.Unpack<Protocol.Gateway.PlexVault.BalanceResponse>();
        Assert.NotNull(balance.Balance);
        Assert.Equal(0, balance.Balance.TotalInCents);
        Assert.Equal(new byte[] { 0x0a, 0x00 }, response.Payload.Value.ToByteArray());
    }

    [Fact]
    public void AmsenReportsNoPirateCorruptionToTheLocationPanel()
    {
        using TestWorld world = new();
        var request = new Request
        {
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/eve_public.pirate.corruption.api.GetSystemInfoRequest",
                // system.sequential = 30001392 (Amsen).
                Value = ByteString.CopyFrom(0x0a, 0x05, 0x08, 0xf0, 0x91, 0xa7, 0x0e),
            },
        };
        var response = world.Services.GetRequiredService<GatewayRequests>().Handle(request);
        Assert.Equal(200u, response.StatusCode);
        var system = response.Payload.Unpack<Protocol.Gateway.Corruption.GetSystemInfoResponse>();
        Assert.Equal(0u, system.Stage);
        Assert.All(
            new[] { system.TotalProgress, system.EveContribution, system.VanguardContribution },
            progress =>
            {
                Assert.Equal(0ul, progress.Numerator);
                Assert.Equal(1ul, progress.Denominator);
            }
        );
    }

    [Fact]
    public void SkillPlanResponsesCarryTheCorrectTypeAndRequestIdentity()
    {
        using TestWorld world = new();
        var gateway = world.Services.GetRequiredService<GatewayRequests>();
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
