namespace Eden.Infrastructure.Operations;

public interface IPilotOperationScope
{
    IDisposable Begin();
}
