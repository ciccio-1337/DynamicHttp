using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;

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
    /// True when the parameter is explicitly nullable, so a missing route/query/header value
    /// or a JSON null body binds as null instead of a 400. When nullable annotations are not
    /// available (<see cref="System.Reflection.NullabilityState.Unknown"/>) the parameter is
    /// treated as required.
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>DataAnnotations attributes declared directly on the parameter.</summary>
    public IReadOnlyList<ValidationAttribute> ValidationAttributes { get; init; } = [];

    /// <summary>
    /// True when the parameter is a body model that declares DataAnnotations attributes or
    /// implements <see cref="IValidatableObject"/>, so object validation must run after binding.
    /// </summary>
    public bool RequiresObjectValidation { get; init; }
}

public sealed record EndpointDefinition(Type ServiceType,
    MethodInfo Method,
    string Route,
    string HttpMethod,
    IReadOnlyList<ParameterDefinition> Parameters,
    Func<object, object?[], object?> Invoker,
    bool AllowAnonymous,
    IReadOnlyList<AuthorizeAttribute> Authorization,
    IReadOnlyList<ProducesResponseTypeAttribute> Responses,
    string[] Tags,
    string? GroupName,
    bool RequireAntiforgery = false);
