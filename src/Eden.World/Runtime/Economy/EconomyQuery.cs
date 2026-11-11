using System.Collections;
using System.Linq.Expressions;

namespace Eden.World.Runtime.Economy;

// Push the complete scalar predicate into the typed snapshot/index before cloning
// matching entities. A station refresh must not materialize its unchanged assortment.
internal sealed class EconomyQueryProvider<T>(EconomySet<T> source) : IQueryProvider
    where T : class
{
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new EconomyQuery<TElement>(this, expression);

    public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException("Use typed resident queries.");

    public TResult Execute<TResult>(Expression expression)
    {
        var rewritten = new Rewrite(source).Visit(expression);
        return Expression.Lambda<Func<TResult>>(Expression.Convert(rewritten, typeof(TResult))).Compile(preferInterpretation: true)();
    }

    public object? Execute(Expression expression) => Execute<object?>(expression);

    private sealed class Rewrite(EconomySet<T> source) : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (
                node.Method.DeclaringType == typeof(Queryable)
                && node.Arguments.Count > 0
                && Predicate(node.Arguments[0], out var projectionFilter)
            )
            {
                LambdaExpression? selector =
                    node.Method.Name == nameof(Queryable.Select)
                    && node.Arguments[1] is UnaryExpression { Operand: LambdaExpression selected }
                    && selected.Parameters.Count == 1
                        ? selected
                        : null;
                bool aggregate =
                    node.Arguments.Count == 1
                    && node.Method.Name is nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any);
                if (selector is not null || aggregate)
                {
                    selector ??= (Expression<Func<T, int>>)(_ => 1);
                    var method = typeof(EconomySet<T>)
                        .GetMethod(
                            nameof(EconomySet<T>.Project),
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                        )!
                        .MakeGenericMethod(selector.ReturnType);
                    var projected = Expression.Call(
                        Expression.Constant(source),
                        method,
                        Expression.Constant(selector),
                        Expression.Constant(projectionFilter, typeof(Expression<Func<T, bool>>))
                    );
                    return aggregate ? Expression.Call(typeof(Enumerable), node.Method.Name, [typeof(int)], projected) : projected;
                }
            }
            if (Predicate(node, out var predicate))
            {
                return Expression.Constant(source.SelectRows(predicate), typeof(IEnumerable<T>));
            }
            if (node.Method.DeclaringType == typeof(Queryable))
            {
                var arguments = node
                    .Arguments.Select(argument =>
                        argument is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
                            ? Expression.Constant(lambda.Compile(preferInterpretation: true))
                            : Visit(argument)
                    )
                    .ToArray();
                return EconomyEnumerable.Call(node.Method, arguments);
            }
            return base.VisitMethodCall(node);
        }

        protected override Expression VisitConstant(ConstantExpression node) =>
            ReferenceEquals(node.Value, source)
                ? Expression.Constant(source.SelectRows(null), typeof(IEnumerable<T>))
                : base.VisitConstant(node);

        private bool Predicate(Expression expression, out Expression<Func<T, bool>>? predicate)
        {
            predicate = null;
            if (expression is ConstantExpression constant && ReferenceEquals(constant.Value, source))
            {
                return true;
            }
            if (
                expression is not MethodCallExpression { Method.Name: nameof(Queryable.Where) } where
                || where.Method.DeclaringType != typeof(Queryable)
                || !Predicate(where.Arguments[0], out var previous)
                || where.Arguments[1] is not UnaryExpression { Operand: Expression<Func<T, bool>> next }
            )
            {
                return false;
            }
            predicate = previous is null
                ? next
                : Expression.Lambda<Func<T, bool>>(
                    Expression.AndAlso(previous.Body, new Replace(next.Parameters[0], previous.Parameters[0]).Visit(next.Body)),
                    previous.Parameters
                );
            return true;
        }

        private sealed class Replace(ParameterExpression before, ParameterExpression after) : ExpressionVisitor
        {
            protected override Expression VisitParameter(ParameterExpression node) => node == before ? after : node;
        }
    }
}

internal sealed class EconomyQuery<T>(IQueryProvider provider, Expression expression) : IOrderedQueryable<T>
{
    public Type ElementType => typeof(T);
    public Expression Expression => expression;
    public IQueryProvider Provider => provider;

    public IEnumerator<T> GetEnumerator() => provider.Execute<IEnumerable<T>>(expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
