using System.Security.Cryptography;

namespace Eden.Protocol.PilotSessions;

public interface ILoginIdentity
{
    RSA Key { get; }
    long? ValidateTicket(string ticket);
    long ReadRoles(long accountId) => (long)PilotRole.Player;
}
