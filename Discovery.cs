using System.Reflection;
using System.Text.RegularExpressions;

namespace DynamicHttp;

internal static partial class DynamicHttpDiscovery
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
            types = exception.Types.Where(t => t is not null).ToArray()!;
        }

        return [.. types.Where(t => t is { IsClass: true, IsAbstract: false } &&
            t.GetCustomAttribute<HttpServiceAttribute>() is not null)];
    }

    public static IReadOnlyList<EndpointDefinition> Build(IEnumerable<Assembly> assemblies)
    {
        List<EndpointDefinition> result = [];

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var serviceType in FindServices(assembly))
            {
                var service = serviceType.GetCustomAttribute<HttpServiceAttribute>()!;

                foreach (var method in serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    var httpAttributes = method.GetCustomAttributes<HttpMethodAttribute>(true).ToArray();

                    if (httpAttributes.Length == 0)
                    {
                        continue;
                    }

                    if (httpAttributes.Length > 1)
                    {
                        throw Configuration(serviceType, method, "A method can have only one HTTP method attribute.");
                    }

                    var http = httpAttributes[0];

                    string route = CombineRoutes(service.RoutePrefix, http.Template);

                    var parameters = BuildParameters(method);

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

    private static ParameterDefinition[] BuildParameters(MethodInfo method) =>
        [.. method.GetParameters().Select(p =>
    {
        var attribute = p.GetCustomAttributes().FirstOrDefault(a => a is FromRouteAttribute or
            FromQueryAttribute or FromHeaderAttribute or FromBodyAttribute or FromServicesAttribute);

        // Auto-detect CancellationToken only when no binding attribute is present; an explicit
        // attribute always wins.
        if (attribute is null && p.ParameterType == typeof(CancellationToken))
        {
            return new ParameterDefinition(p,
                BindingKind.CancellationToken,
                p.Name ?? "cancellationToken",
                p.ParameterType);
        }

        return attribute switch
        {
            FromRouteAttribute a => new ParameterDefinition(p, BindingKind.Route, a.Name ?? p.Name!, p.ParameterType),
            FromQueryAttribute a => new ParameterDefinition(p, BindingKind.Query, a.Name ?? p.Name!, p.ParameterType),
            FromHeaderAttribute a => new ParameterDefinition(p, BindingKind.Header, a.Name ?? p.Name!, p.ParameterType),
            FromBodyAttribute => new ParameterDefinition(p, BindingKind.Body, p.Name!, p.ParameterType),
            FromServicesAttribute => new ParameterDefinition(p, BindingKind.Services, p.Name!, p.ParameterType),
            _ when IsSimple(p.ParameterType) => new ParameterDefinition(p, BindingKind.Query, p.Name!, p.ParameterType),
            _ => new ParameterDefinition(p, BindingKind.Body, p.Name!, p.ParameterType)
        };
    })];

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

    [GeneratedRegex(@"\{\*?([^}:?*\s]+)")]
    private static partial Regex RouteParameterRegex();

    private static void ValidateParameters(string route,
        IReadOnlyList<ParameterDefinition> parameters,
        Type service,
        MethodInfo method)
    {
        HashSet<string> routeNames = RouteParameterRegex().Matches(route)
            .Select(x => x.Groups[1].Value)
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

    private static void ValidateConflicts(IEnumerable<EndpointDefinition> endpoints)
    {
        var duplicate = endpoints
            .GroupBy(x => $"{x.HttpMethod} {x.Route}", StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);

        if (duplicate is not null)
        {
            throw new DynamicHttpConfigurationException($"Conflicting dynamic endpoints: {duplicate.Key}");
        }
    }

    private static DynamicHttpConfigurationException Configuration(Type service, MethodInfo method, string message) =>
        new($"DynamicHttp configuration error for '{service.FullName}.{method.Name}': {message}");
}
