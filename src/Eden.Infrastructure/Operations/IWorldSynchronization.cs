namespace Eden.Infrastructure.Operations;

// Native scene motion and authoritative mutations must enter the same owner first.
public interface IWorldSynchronization
{
    bool IsEntered { get; }
    IDisposable Enter(string operation, int? systemId = null);
}
