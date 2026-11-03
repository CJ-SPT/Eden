using System.Security.Cryptography;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.PilotSessions;

internal static class PilotLoginWire
{
    public static void ValidateCommand(byte[] command)
    {
        var fields = BlueRead.Tuple(BlueMarshal.Decode(command), 3);
        if (fields[0] is not null || BlueMarshal.Text(fields[1]) != "VK")
        {
            throw new InvalidDataException("Expected a local login request.");
        }
    }

    public static byte[] HashArgument(byte[] value) => SHA256.HashData(BlueMarshal.Encode(new object?[] { value }));
}

internal sealed record LoginKeyExchange(byte[] SessionKey, byte[] SessionIv)
{
    public static LoginKeyExchange Read(object? value)
    {
        var exchange = BlueRead.Tuple(value, 2);
        var keys = BlueRead.Fields(exchange[1]);
        return new(BlueRead.Bytes(keys["crypting_sessionkey"]), BlueRead.Bytes(keys["crypting_sessioniv"]));
    }
}

internal sealed record LoginCredentials(byte[] Challenge, string Ticket)
{
    public static LoginCredentials Read(object? value)
    {
        var fields = BlueRead.Tuple(value, 2);
        var credentials = BlueRead.Fields(fields[1]);
        return new(BlueRead.Bytes(fields[0]), BlueMarshal.Text(credentials["user_sso_token"]));
    }
}

internal sealed record LoginProof(byte[] ResponseHash)
{
    public static LoginProof Read(object? value) => new(BlueRead.Bytes(BlueRead.Tuple(value, 3)[0]));
}

internal sealed record LoginChallenge(byte[] Challenge, SignedSettings Expression, byte[] ResponseHash) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Challenge,
            Expression,
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>
            {
                ["challenge_responsehash"] = ResponseHash,
                ["macho_version"] = 496,
                ["boot_version"] = 24.01,
                ["boot_build"] = CompatibilityHandshake.Build,
                ["boot_codename"] = "V24.01",
                ["boot_region"] = "ccp",
                ["cluster_usercount"] = 1,
                ["proxy_nodeid"] = PilotLogin.NodeId,
                ["user_logonqueueposition"] = 0,
                ["config_vals"] = new Dictionary<string, object?>(),
            },
        };
}

internal sealed record LoginSession(long AccountId, long ClientId, long Roles = (long)PilotRole.Player) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["session_init"] = new AccountSessionState(AccountId, Roles),
            ["session_id"] = ClientId,
            ["sessionID"] = ClientId,
            ["clientID"] = ClientId,
            ["access_token"] = "",
            ["computer_hash"] = "",
            ["client_hash"] = "Unknown",
        };
}
