using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Eden.World.Runtime.Economy;

// Bind typed query operators once. Runtime actions interpret small predicates;
// they must not pay expression-to-IL compilation for each wallet/order read.
internal static class EconomyEnumerable
{
    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo[]> Bindings = new();
    private static readonly MethodInfo[] Methods = typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static);

    internal static Expression Call(MethodInfo query, IReadOnlyList<Expression> arguments)
    {
        var candidates = Bindings.GetOrAdd(query, Bind);
        foreach (var method in candidates)
        {
            var parameters = method.GetParameters();
            if (parameters.Select((parameter, i) => parameter.ParameterType.IsAssignableFrom(arguments[i].Type)).All(match => match))
            {
                return Expression.Call(method, arguments);
            }
        }
        throw new NotSupportedException($"The resident query operator {query.Name} has no typed enumerable binding.");
    }

    private static MethodInfo[] Bind(MethodInfo query)
    {
        var types = query.GetGenericArguments();
        int count = query.GetParameters().Length;
        return Methods
            .Where(method =>
                method.Name == query.Name && method.GetParameters().Length == count && method.GetGenericArguments().Length == types.Length
            )
            .Select(method => method.IsGenericMethodDefinition ? method.MakeGenericMethod(types) : method)
            .ToArray();
    }
}
