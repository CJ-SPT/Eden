using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.PilotSessions;

internal sealed record LoginChallenge(byte[] Challenge, SignedSettings Expression, byte[] ResponseHash) : IBlueWritable
{
    public object ToBlue() =>
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

internal sealed record LoginSession(long AccountId, long ClientId) : IBlueWritable
{
    public object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["session_init"] = new AccountSessionState(AccountId),
            ["session_id"] = ClientId,
            ["sessionID"] = ClientId,
            ["clientID"] = ClientId,
            ["access_token"] = "",
            ["computer_hash"] = "",
            ["client_hash"] = "Unknown",
        };
}
