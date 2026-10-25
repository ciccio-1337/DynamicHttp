using System.Reflection;

namespace DynamicHttp;

public enum BindingKind
{
    Route,
    Query,
    Header,
    Body,
    Services,
    CancellationToken,
    HttpContext,
    HttpRequest,
    HttpResponse,
    User
}

public sealed record ParameterDefinition(ParameterInfo Parameter,
    BindingKind Kind,
    string Name,
    Type ParameterType)
{
    /// <summary>
    /// True when the parameter is nullable (or its nullability is unknown), so a missing
    /// route/query/header value or a JSON null body binds as null instead of a 400.
    /// </summary>
    public bool IsNullable { get; init; }
}

public sealed record EndpointDefinition(Type ServiceType,
    MethodInfo Method,
    string Route,
    string HttpMethod,
    IReadOnlyList<ParameterDefinition> Parameters,
    Func<object, object?[], object?> Invoker,
    bool AllowAnonymous,
    IReadOnlyList<DynamicAuthorizeAttribute> Authorization,
    IReadOnlyList<ProducesResponseTypeAttribute> Responses,
    string[] Tags,
    string? GroupName);
