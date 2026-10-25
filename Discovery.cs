using System.Reflection;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DynamicHttp;

internal static class DynamicHttpDiscovery
{
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

                    var authorization = serviceType.GetCustomAttributes<DynamicAuthorizeAttribute>(true)
                        .Concat(method.GetCustomAttributes<DynamicAuthorizeAttribute>(true))
                        .ToArray();
                    var responses = serviceType.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
                        .Concat(method.GetCustomAttributes<ProducesResponseTypeAttribute>(true))
                        .ToArray();
                    bool allowAnonymous = serviceType.IsDefined(typeof(DynamicAllowAnonymousAttribute), true) ||
                        method.IsDefined(typeof(DynamicAllowAnonymousAttribute), true);

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
                        service.GroupName));
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
        {
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

            // Auto-detect framework types and CancellationToken only when no binding attribute is
            // present; an explicit attribute always wins.
            if (attribute is null)
            {
                if (p.ParameterType == typeof(CancellationToken))
                {
                    return CreateParameter(p,
                        BindingKind.CancellationToken,
                        p.Name ?? "cancellationToken",
                        nullability);
                }

                if (p.ParameterType == typeof(HttpContext))
                {
                    return CreateParameter(p, BindingKind.HttpContext, p.Name!, nullability);
                }

                if (p.ParameterType == typeof(HttpRequest))
                {
                    return CreateParameter(p, BindingKind.HttpRequest, p.Name!, nullability);
                }

                if (p.ParameterType == typeof(HttpResponse))
                {
                    return CreateParameter(p, BindingKind.HttpResponse, p.Name!, nullability);
                }

                if (p.ParameterType == typeof(ClaimsPrincipal))
                {
                    return CreateParameter(p, BindingKind.User, p.Name!, nullability);
                }
            }

            return attribute switch
            {
                FromRouteAttribute a => CreateParameter(p, BindingKind.Route, a.Name ?? p.Name!, nullability),
                FromQueryAttribute a => CreateParameter(p, BindingKind.Query, a.Name ?? p.Name!, nullability),
                FromHeaderAttribute a => CreateParameter(p, BindingKind.Header, a.Name ?? p.Name!, nullability),
                FromBodyAttribute => CreateParameter(p, BindingKind.Body, p.Name!, nullability),
                FromServicesAttribute => CreateParameter(p, BindingKind.Services, p.Name!, nullability),
                _ when IsSimple(p.ParameterType) => CreateParameter(p, BindingKind.Query, p.Name!, nullability),
                _ => CreateParameter(p, BindingKind.Body, p.Name!, nullability)
            };
        })];
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

        // Unknown means the declaring assembly does not use nullable reference annotations;
        // keep the historical (optional) behavior instead of enforcing a missing value.
        return nullability.Create(parameter).ReadState != NullabilityState.NotNull;
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
        bool classAnonymous = serviceType.IsDefined(typeof(DynamicAllowAnonymousAttribute), true);
        bool methodAnonymous = method.IsDefined(typeof(DynamicAllowAnonymousAttribute), true);
        bool classAuthorized = serviceType.IsDefined(typeof(DynamicAuthorizeAttribute), true);
        bool methodAuthorized = method.IsDefined(typeof(DynamicAuthorizeAttribute), true);

        if (classAuthorized && classAnonymous)
        {
            throw Configuration(serviceType, method,
                "A service cannot combine [DynamicAuthorize] and [DynamicAllowAnonymous] on the same class.");
        }

        if (methodAuthorized && (classAnonymous || methodAnonymous))
        {
            throw Configuration(serviceType, method,
                "An endpoint that requires [DynamicAuthorize] cannot also be [DynamicAllowAnonymous]: 'AllowAnonymous' would silently bypass authorization.");
        }
    }

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
