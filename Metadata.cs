using System.Reflection;

namespace DynamicHttp;

public enum BindingKind
{
    Route, Query, Header, Body, Services, CancellationToken
}

public sealed record ParameterDefinition(ParameterInfo Parameter,
    BindingKind Kind,
    string Name,
    Type ParameterType);

public sealed record EndpointDefinition(Type ServiceType,
    MethodInfo Method,
    string Route,
    string HttpMethod,
    IReadOnlyList<ParameterDefinition> Parameters,
    Func<object, object?[], object?> Invoker,
    bool AllowAnonymous,
    IReadOnlyList<DynamicAuthorizeAttribute> Authorization,
    IReadOnlyList<ProducesResponseTypeAttribute> Responses,
    IReadOnlyList<Type> Filters,
    string[] Tags,
    string? GroupName);
