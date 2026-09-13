using System.Linq.Expressions;
using System.Reflection;

namespace DynamicHttp;

internal static class CompiledInvokerFactory
{
    public static Func<object, object?[], object?> Create(Type serviceType,
        MethodInfo method,
        IReadOnlyList<ParameterDefinition> parameters)
    {
        var service = Expression.Parameter(typeof(object), "service");
        var args = Expression.Parameter(typeof(object[]), "args");
        var typedService = Expression.Convert(service, serviceType);
        var callArguments = parameters.Select((parameter, index) =>
            Expression.Convert(Expression.ArrayIndex(args,
                Expression.Constant(index)),
                parameter.ParameterType)).ToArray();
        var call = Expression.Call(typedService, method, callArguments);

        // A void method cannot be converted to object; invoke it as a statement and return null.
        Expression body = method.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression.Lambda<Func<object, object?[], object?>>(body, service, args).Compile();
    }
}
