namespace Eden.Infrastructure.Operations;

// Native scene motion and authoritative mutations must enter the same owner first.
public interface IWorldSynchronization
{
    object Gate { get; }
    IDisposable Enter(string operation);
}
