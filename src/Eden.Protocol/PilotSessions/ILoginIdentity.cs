using System.Security.Cryptography;

namespace Eden.Protocol.PilotSessions;

public interface ILoginIdentity
{
    RSA Key { get; }
    ValueTask<long?> ValidateTicketAsync(string ticket, CancellationToken cancellationToken);
    long ReadRoles(long accountId) => (long)PilotRole.Player;
}
