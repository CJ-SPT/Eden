namespace Eden.Infrastructure.Registration;

public enum InjectionType
{
    Transient,
    Singleton,
    Scoped,
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class InjectableAttribute(InjectionType lifetime = InjectionType.Transient) : Attribute
{
    public InjectionType Lifetime { get; } = lifetime;
}
