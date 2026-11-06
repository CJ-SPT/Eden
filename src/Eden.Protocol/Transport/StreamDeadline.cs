namespace Eden.Protocol.Transport;

internal sealed class StreamDeadline : IDisposable
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _linked;
    internal CancellationToken Token => _linked.Token;

    internal StreamDeadline(CancellationToken cancellation, TimeProvider? clock = null)
    {
        _timeout = new(OperationTimeout, clock ?? TimeProvider.System);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _timeout.Token);
    }

    public void Dispose()
    {
        _linked.Dispose();
        _timeout.Dispose();
    }
}
