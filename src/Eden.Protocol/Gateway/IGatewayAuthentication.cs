namespace Eden.Protocol.Gateway;

public interface IGatewayAuthentication
{
    long? Account(string authorization);
}
