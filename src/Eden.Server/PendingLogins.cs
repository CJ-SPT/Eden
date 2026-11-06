using System.Net;
using Eden.Server.Launcher;

namespace Eden.Server;

internal sealed class PendingLogins(LauncherOptions options)
{
    private readonly Dictionary<IPAddress, int> _addresses = [];
    private int _count;

    internal IDisposable? TryEnter(IPAddress address)
    {
        address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        lock (_addresses)
        {
            int count = _addresses.GetValueOrDefault(address);
            int limit = IPAddress.IsLoopback(address) ? options.MaximumPendingLogins : options.MaximumPendingLoginsPerAddress;
            if (_count >= options.MaximumPendingLogins || count >= limit)
            {
                return null;
            }
            _count++;
            _addresses[address] = count + 1;
            return new Lease(this, address);
        }
    }

    private sealed class Lease(PendingLogins owner, IPAddress address) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }
            lock (owner._addresses)
            {
                owner._count--;
                if (--owner._addresses[address] == 0)
                {
                    owner._addresses.Remove(address);
                }
            }
        }
    }
}
