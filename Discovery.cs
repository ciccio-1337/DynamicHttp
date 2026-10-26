using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DynamicHttp;

internal static class DynamicHttpDiscovery
{
    private static readonly ConcurrentDictionary<Type, bool> ObjectValidationCache = new();

    private static readonly Type[] MvcBindingAttributeTypes =
    [
        typeof(Microsoft.AspNetCore.Mvc.FromRouteAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromQueryAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromHeaderAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromBodyAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromFormAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromServicesAttribute)
    ];

    public static IReadOnlyList<Type> FindServices(Assembly assembly)
    {
        // GetTypes() throws ReflectionTypeLoadException when some types can't be loaded (e.g.
        // missing dependencies). Load whatever is loadable instead of failing the whole scan.
        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.OfType<Type>().ToArray();
        }

        Type[] services = [.. types.Where(t => t is { IsClass: true, IsAbstract: false } &&
            t.GetCustomAttribute<HttpServiceAttribute>() is not null)];

        foreach (Type service in services)
        {
            ValidateServiceType(service);
        }

        return services;
    }

    internal static void ValidateServiceType(Type serviceType)
    {
        if (serviceType.IsGenericTypeDefinition)
        {
            throw new DynamicHttpConfigurationException(
                $"DynamicHttp configuration error for '{serviceType.FullName}': open generic services are not supported.");
        }
    }

    public static IReadOnlyList<EndpointDefinition> Build(IEnumerable<Assembly> assemblies)
    {
        List<EndpointDefinition> result = [];

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var serviceType in FindServices(assembly))
            {
                var service = serviceType.GetCustomAttribute<HttpServiceAttribute>()!;

                foreach (var method in serviceType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public))
                {
                    var http = GetHttpAttribute(serviceType, method);

                    if (http is null)
                    {
                        continue;
                    }

                    ValidateEndpointMethod(serviceType, method);

                    string route = CombineRoutes(service.RoutePrefix, http.Template);

                    var parameters = BuildParameters(serviceType, method);

                    ValidateParameters(route, parameters, serviceType, method);

                    var authorization = GetAuthorization(serviceType, method);
                    var responses = serviceType.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
                        .Concat(method.GetCustomAttributes<ProducesResponseTypeAttribute>(true))
                        .ToArray();
                    bool allowAnonymous = IsAllowAnonymous(serviceType) || IsAllowAnonymous(method);

                    ValidateAuthorization(serviceType, method);

                    result.Add(new EndpointDefinition(serviceType,
                        method,
                        route,
                        GetVerb(http),
                        parameters,
                        CompiledInvokerFactory.Create(serviceType, method, parameters),
                        allowAnonymous,
                        authorization,
                        responses,
                        [.. service.Tags.Concat(http.Tags).Distinct()],
                        service.GroupName,
                        IsAntiforgeryRequired(serviceType, method)));
                }
            }
        }

        ValidateConflicts(result);

        return result;
    }

    internal static HttpMethodAttribute? GetHttpAttribute(Type serviceType, MethodInfo method)
    {
        var httpAttributes = method.GetCustomAttributes<HttpMethodAttribute>(true).ToArray();

        if (httpAttributes.Length == 0)
        {
            return null;
        }

        if (httpAttributes.Length > 1)
        {
            throw Configuration(serviceType, method, "A method can have only one HTTP method attribute.");
        }

        return httpAttributes[0];
    }

    internal static void ValidateEndpointMethod(Type serviceType, MethodInfo method)
    {
        if (method.IsStatic)
        {
            throw Configuration(serviceType, method,
                "Endpoint methods must be instance methods; static methods are not supported.");
        }

        if (method.IsGenericMethodDefinition)
        {
            throw Configuration(serviceType, method, "Generic endpoint methods are not supported.");
        }
    }

    internal static ParameterDefinition[] BuildParameters(Type serviceType, MethodInfo method)
    {
        var nullability = new NullabilityInfoContext();

        return [.. method.GetParameters().Select(p =>
            CreateDefinition(serviceType, method, p, nullability))];
    }

    private static ParameterDefinition CreateDefinition(Type serviceType,
        MethodInfo method,
        ParameterInfo p,
        NullabilityInfoContext nullability)
    {
        // The library defines its own binding attributes. An MVC/ASP.NET attribute with the same
        // name would be silently ignored and the parameter would bind from the default source
        // (possibly a querystring instead of a header), so reject the ambiguity at startup.
        var mvcBindingAttributes = p.GetCustomAttributes()
            .Where(a => MvcBindingAttributeTypes.Any(t => t.IsInstanceOfType(a)))
            .ToArray();

        if (mvcBindingAttributes.Length > 0)
        {
            throw Configuration(serviceType, method,
                $"Parameter '{p.Name}' uses the MVC binding attribute " +
                $"'{mvcBindingAttributes[0].GetType().Name}'; use the DynamicHttp attribute instead.");
        }

        var bindingAttributes = p.GetCustomAttributes()
            .Where(a => a is FromRouteAttribute or FromQueryAttribute or FromHeaderAttribute or
                FromBodyAttribute or FromServicesAttribute)
            .ToArray();

        if (bindingAttributes.Length > 1)
        {
            throw Configuration(serviceType, method,
                $"Parameter '{p.Name}' has multiple binding attributes " +
                $"({string.Join(", ", bindingAttributes.Select(a => a.GetType().Name))}); use only one.");
        }

        var attribute = bindingAttributes.FirstOrDefault();

        ParameterDefinition definition;

        // Auto-detect framework types and CancellationToken only when no binding attribute is
        // present; an explicit attribute always wins.
        if (attribute is null && p.ParameterType == typeof(CancellationToken))
        {
            definition = CreateParameter(p,
                BindingKind.CancellationToken,
                p.Name ?? "cancellationToken",
                nullability);
        }
        else if (attribute is null && p.ParameterType == typeof(HttpContext))
        {
            definition = CreateParameter(p, BindingKind.HttpContext, p.Name!, nullability);
        }
        else if (attribute is null && p.ParameterType == typeof(HttpRequest))
        {
            definition = CreateParameter(p, BindingKind.HttpRequest, p.Name!, nullability);
        }
        else if (attribute is null && p.ParameterType == typeof(HttpResponse))
        {
            definition = CreateParameter(p, BindingKind.HttpResponse, p.Name!, nullability);
        }
        else if (attribute is null && p.ParameterType == typeof(ClaimsPrincipal))
        {
            definition = CreateParameter(p, BindingKind.User, p.Name!, nullability);
        }
        else
        {
            definition = attribute switch
            {
                FromRouteAttribute a => CreateParameter(p, BindingKind.Route, a.Name ?? p.Name!, nullability),
                FromQueryAttribute a => CreateParameter(p, BindingKind.Query, a.Name ?? p.Name!, nullability),
                FromHeaderAttribute a => CreateParameter(p, BindingKind.Header, a.Name ?? p.Name!, nullability),
                FromBodyAttribute => CreateParameter(p, BindingKind.Body, p.Name!, nullability),
                FromServicesAttribute => CreateParameter(p, BindingKind.Services, p.Name!, nullability),
                _ when IsSimple(p.ParameterType) => CreateParameter(p, BindingKind.Query, p.Name!, nullability),
                _ => CreateParameter(p, BindingKind.Body, p.Name!, nullability)
            };
        }

        return definition with
        {
            ValidationAttributes = p.GetCustomAttributes<ValidationAttribute>(true).ToArray(),
            // Object validation applies to body models. Injected services and framework
            // parameters are not DataAnnotations models and must not be validated.
            RequiresObjectValidation = definition.Kind == BindingKind.Body &&
                HasObjectValidation(p.ParameterType)
        };
    }

    private static ParameterDefinition CreateParameter(ParameterInfo parameter,
        BindingKind kind,
        string name,
        NullabilityInfoContext nullability) =>
        new(parameter, kind, name, parameter.ParameterType)
        {
            IsNullable = IsNullable(parameter, nullability)
        };

    private static bool IsNullable(ParameterInfo parameter, NullabilityInfoContext nullability)
    {
        Type type = parameter.ParameterType;

        if (Nullable.GetUnderlyingType(type) is not null)
        {
            return true;
        }

        if (type.IsValueType)
        {
            return false;
        }

        // Unknown means the declaring assembly does not use nullable reference annotations.
        // Treat it as required instead of silently turning a required value into null; declared
        // C# default values are still honored by the binder.
        return nullability.Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static bool HasObjectValidation(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return ObjectValidationCache.GetOrAdd(type, static candidate =>
        {
            if (typeof(IValidatableObject).IsAssignableFrom(candidate))
            {
                return true;
            }

            return candidate.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.GetCustomAttributes<ValidationAttribute>(true).Any());
        });
    }

    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) ||
            type == typeof(decimal);
    }

    internal static void ValidateAuthorization(Type serviceType, MethodInfo method)
    {
        bool classAnonymous = IsAllowAnonymous(serviceType);
        bool methodAnonymous = IsAllowAnonymous(method);
        bool classAuthorized = IsAuthorized(serviceType);
        bool methodAuthorized = IsAuthorized(method);

        if (classAuthorized && classAnonymous)
        {
            throw Configuration(serviceType, method,
                "A service cannot combine an authorization attribute and an allow-anonymous attribute on the same class.");
        }

        if (methodAuthorized && (classAnonymous || methodAnonymous))
        {
            throw Configuration(serviceType, method,
                "An endpoint that requires authorization cannot also be marked as anonymous: 'AllowAnonymous' would silently bypass authorization.");
        }
    }

    /// <summary>
    /// Collects framework <see cref="IAuthorizeData"/> attributes and the library's
    /// <see cref="DynamicAuthorizeAttribute"/> into normalized <see cref="AuthorizeAttribute"/>
    /// metadata. All of them are applied, so mixing the two attribute families is fail-closed.
    /// </summary>
    private static AuthorizeAttribute[] GetAuthorization(Type serviceType, MethodInfo method) =>
    [
        .. serviceType.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
            .Select(ToAuthorizeAttribute),
        .. method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
            .Select(ToAuthorizeAttribute),
        .. serviceType.GetCustomAttributes<DynamicAuthorizeAttribute>(true)
            .Select(ToAuthorizeAttribute),
        .. method.GetCustomAttributes<DynamicAuthorizeAttribute>(true)
            .Select(ToAuthorizeAttribute)
    ];

    private static AuthorizeAttribute ToAuthorizeAttribute(IAuthorizeData data) =>
        CreateAuthorization(data.Policy, data.Roles, data.AuthenticationSchemes);

    private static AuthorizeAttribute ToAuthorizeAttribute(DynamicAuthorizeAttribute data) =>
        CreateAuthorization(data.Policy, data.Roles, data.AuthenticationSchemes);

    private static AuthorizeAttribute CreateAuthorization(string? policy, string? roles, string? schemes) =>
        new()
        {
            Policy = Whitelist(policy),
            Roles = Whitelist(roles),
            AuthenticationSchemes = Whitelist(schemes)
        };

    private static string? Whitelist(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsAllowAnonymous(Type serviceType) =>
        serviceType.IsDefined(typeof(DynamicAllowAnonymousAttribute), true) ||
        serviceType.IsDefined(typeof(AllowAnonymousAttribute), true);

    private static bool IsAllowAnonymous(MethodInfo method) =>
        method.IsDefined(typeof(DynamicAllowAnonymousAttribute), true) ||
        method.IsDefined(typeof(AllowAnonymousAttribute), true);

    private static bool IsAuthorized(Type serviceType) =>
        serviceType.IsDefined(typeof(DynamicAuthorizeAttribute), true) ||
        serviceType.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any();

    private static bool IsAuthorized(MethodInfo method) =>
        method.IsDefined(typeof(DynamicAuthorizeAttribute), true) ||
        method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any();

    private static bool IsAntiforgeryRequired(Type serviceType, MethodInfo method) =>
        serviceType.IsDefined(typeof(DynamicAntiforgeryAttribute), true) ||
        method.IsDefined(typeof(DynamicAntiforgeryAttribute), true);

    private static string CombineRoutes(string prefix, string template)
    {
        string a = prefix.Trim('/');
        string b = template.Trim('/');
        string route = string.Join("/", new[] { a, b }.Where(x => x.Length > 0));

        return "/" + route;
    }

    private static string GetVerb(HttpMethodAttribute attribute) => attribute switch
    {
        HttpGetAttribute => "GET",
        HttpPostAttribute => "POST",
        HttpPutAttribute => "PUT",
        HttpPatchAttribute => "PATCH",
        HttpDeleteAttribute => "DELETE",
        HttpHeadAttribute => "HEAD",
        HttpOptionsAttribute => "OPTIONS",
        _ => throw new DynamicHttpConfigurationException($"Unsupported HTTP attribute '{attribute.GetType().Name}'.")
    };

    internal static void ValidateParameters(string route,
        IReadOnlyList<ParameterDefinition> parameters,
        Type service,
        MethodInfo method)
    {
        HashSet<string> routeNames = RoutePatternFactory.Parse(route).Parameters
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters.Where(x => x.Kind == BindingKind.Route))
        {
            if (!routeNames.Contains(parameter.Name))
            {
                throw Configuration(service, method, $"Route '{route}' has no parameter named '{parameter.Name}'.");
            }
        }

        foreach (var parameter in parameters.Where(x => x.Parameter.ParameterType.IsByRef))
        {
            throw Configuration(service, method, $"Parameter '{parameter.Name}' cannot be bound: 'ref'/'out'/'in' parameters are not supported.");
        }

        int bodyCount = parameters.Count(x => x.Kind == BindingKind.Body);

        if (bodyCount > 1)
        {
            throw Configuration(service, method, "An endpoint can have at most one body parameter.");
        }
    }

    internal static void ValidateConflicts(IEnumerable<EndpointDefinition> endpoints)
    {
        var duplicate = endpoints
            .GroupBy(x => $"{x.HttpMethod} {NormalizeRoute(x.Route)}", StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicate is not null)
        {
            string routes = string.Join(", ", duplicate.Select(x => $"{x.HttpMethod} {x.Route}"));
            throw new DynamicHttpConfigurationException($"Conflicting dynamic endpoints: {routes}");
        }
    }

    /// <summary>
    /// Builds a canonical representation of a route template with parameter names removed.
    /// Two templates that normalize to the same value have the same precedence and can both
    /// match the same request, which makes ASP.NET Core throw at request time.
    /// </summary>
    internal static string NormalizeRoute(string route)
    {
        RoutePattern pattern = RoutePatternFactory.Parse(route);
        StringBuilder builder = new();

        foreach (RoutePatternPathSegment segment in pattern.PathSegments)
        {
            builder.Append('/');

            foreach (RoutePatternPart part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        // Route matching is case-insensitive for literals, but route constraints
                        // are not, so only literals are case-folded.
                        builder.Append(literal.Content.ToUpperInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        builder.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        builder.Append('{');
                        if (parameter.IsCatchAll)
                        {
                            builder.Append('*');
                        }
                        builder.Append('_');
                        if (parameter.IsOptional)
                        {
                            builder.Append('?');
                        }
                        if (parameter.Default is not null)
                        {
                            builder.Append('=').Append(parameter.Default);
                        }
                        foreach (var policy in parameter.ParameterPolicies
                            .OrderBy(p => p.Content, StringComparer.Ordinal))
                        {
                            builder.Append(':').Append(policy.Content);
                        }
                        builder.Append('}');
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static DynamicHttpConfigurationException Configuration(Type service, MethodInfo method, string message) =>
        new($"DynamicHttp configuration error for '{service.FullName}.{method.Name}': {message}");
}
