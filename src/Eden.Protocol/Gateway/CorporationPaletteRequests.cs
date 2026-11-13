using System.Net;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Corporations;
using Eden.Protocol.Gateway.CorporationPalette;
using Google.Protobuf;

namespace Eden.Protocol.Gateway;

internal static class CorporationPaletteRequests
{
    private const string Api = "eve_public.cosmetic.corporation.palette.api.";

    internal static GatewayReply? Read(
        string type,
        ByteString payload,
        AuthoritativeContext? context,
        ICharacterDirectory characters,
        IPlayerCorporations corporations
    )
    {
        try
        {
            switch (type)
            {
                case Api + "GetRequest":
                    uint? id = GetRequest.Parser.ParseFrom(payload).Identifier?.Corporation?.Sequential;
                    if (id is not (> 0 and <= int.MaxValue))
                    {
                        return new(HttpStatusCode.BadRequest);
                    }
                    return corporations.Read((int)id.Value) is { } target
                        ? new(HttpStatusCode.OK, new GetResponse { Attributes = BannerColors(target.Details.Logo) })
                        : new(HttpStatusCode.NotFound);
                case Api + "GetOwnRequest":
                    _ = GetOwnRequest.Parser.ParseFrom(payload);
                    if (context?.Identity?.Character?.Sequential is not > 0)
                    {
                        return new(HttpStatusCode.BadRequest);
                    }
                    // The client identity only selects which already-public logo color to read.
                    return
                        characters.Corporation(context.Identity.Character.Sequential) is int corporationId
                        && corporations.Read(corporationId) is { } own
                        ? new(HttpStatusCode.OK, new GetOwnResponse { Attributes = BannerColors(own.Details.Logo) })
                        : new(HttpStatusCode.NotFound);
                default:
                    return null;
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return new(HttpStatusCode.BadRequest);
        }
    }

    private static Attributes BannerColors(CorporationLogo logo)
    {
        // Build 3662168 appConst.graphicCorpLogoLibColors and CORPLOGO_DEFAULT_COLOR,
        // converted to 8-bit RGB as expected by CorpColor.from_rgba_8bit.
        var (red, green, blue) = logo.Color1 switch
        {
            671 => (32u, 32u, 32u),
            672 => (150u, 128u, 89u),
            673 => (168u, 212u, 255u),
            674 => (255u, 255u, 255u),
            675 => (74u, 74u, 74u),
            676 => (168u, 255u, 255u),
            677 => (255u, 255u, 128u),
            678 => (145u, 153u, 153u),
            679 => (255u, 120u, 0u),
            680 => (150u, 0u, 0u),
            681 or 683 => (125u, 128u, 128u),
            682 => (0u, 0u, 0u),
            684 => (232u, 232u, 232u),
            685 => (255u, 179u, 61u),
            _ => (255u, 255u, 255u),
        };
        return new()
        {
            MainColor = new RGB
            {
                Red = red,
                Green = green,
                Blue = blue,
            },
            NoSecondaryColor = true,
            NoTertiaryColor = true,
        };
    }
}
