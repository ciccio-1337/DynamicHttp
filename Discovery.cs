using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;

namespace DynamicHttp;

internal static class DynamicHttpDiscovery
{
    private static readonly Type[] UnsupportedBindingAttributeTypes =
    [
        typeof(Microsoft.AspNetCore.Mvc.FromRouteAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromQueryAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromHeaderAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromBodyAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromFormAttribute),
        typeof(Microsoft.AspNetCore.Mvc.FromServicesAttribute),
        typeof(Microsoft.AspNetCore.Http.AsParametersAttribute),
        typeof(Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute)
    ];

    public static IReadOnlyList<Type> FindServices(Assembly assembly, ILogger? logger = null)
    {
        // GetTypes() throws ReflectionTypeLoadException when some types can't be loaded (e.g.
        // missing dependencies). Load whatever is loadable instead of failing the whole scan,
        // but keep the load failures visible in the logs.
        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            if (logger is not null)
            {
                logger.LogWarning(
                    "DynamicHttp could not load {FailedTypeCount} type(s) from assembly '{Assembly}'. " +
                    "Loadable types will still be scanned. First failure: {Failure}",
                    exception.LoaderExceptions.Length,
                    assembly.GetName().Name,
                    exception.LoaderExceptions.FirstOrDefault()?.Message);
            }

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

    public static IReadOnlyList<EndpointDefinition> Build(IEnumerable<Assembly> assemblies,
        ILogger? logger = null)
    {
        List<EndpointDefinition> result = [];

        foreach (var assembly in assemblies.Distinct())
        {
            foreach (var serviceType in FindServices(assembly, logger))
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

                    if (service.RoutePrefix is null || http.Template is null)
                    {
                        throw Configuration(serviceType, method,
                            "The service route prefix and the HTTP method template must not be null.");
                    }

                    string route = CombineRoutes(service.RoutePrefix, http.Template);

                    var parameters = BuildParameters(serviceType, method);

                    ValidateParameters(route, parameters, serviceType, method);

                    var authorization = GetAuthorization(serviceType, method);
                    var responses = serviceType.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
                        .Concat(method.GetCustomAttributes<ProducesResponseTypeAttribute>(true))
                        .GroupBy(response => (response.StatusCode, response.ResponseType))
                        .Select(group => group.First())
                        .ToArray();
                    bool allowAnonymous =
                        IsAllowAnonymous(serviceType) || IsAllowAnonymous(serviceType, method);

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

        if (method.ReturnType == typeof(void) &&
            method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false))
        {
            throw Configuration(serviceType, method,
                "Async void endpoint methods are not supported; return Task or ValueTask instead.");
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
        // The library defines its own binding attributes. A framework binding attribute would be
        // silently ignored and the parameter could bind from the wrong source (for example a
        // querystring instead of a header), so reject the ambiguity at startup.
        var unsupportedBindingAttributes = p.GetCustomAttributes()
            .Where(a => UnsupportedBindingAttributeTypes.Any(t => t.IsInstanceOfType(a)))
            .ToArray();

        if (unsupportedBindingAttributes.Length > 0)
        {
            throw Configuration(serviceType, method,
                $"Parameter '{p.Name}' uses the framework binding attribute " +
                $"'{unsupportedBindingAttributes[0].GetType().Name}' which DynamicHttp does not support; " +
                "use the DynamicHttp attribute instead.");
        }

        if (p.IsDefined(typeof(BindNeverAttribute), inherit: true))
        {
            throw Configuration(serviceType, method,
                $"Parameter '{p.Name}' uses [BindNever], which DynamicHttp does not support. " +
                "Use a request DTO and [JsonIgnore] on properties that must not be bound.");
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

        bool requiresObjectValidation = false;

        if (definition.Kind == BindingKind.Body)
        {
            string? bindNeverProperty = DynamicHttpValidation.FindBindNeverProperty(p.ParameterType);

            if (bindNeverProperty is not null)
            {
                throw Configuration(serviceType, method,
                    $"Body model '{p.ParameterType.Name}' uses [BindNever] on '{bindNeverProperty}'. " +
                    "DynamicHttp binds the whole JSON body; use a dedicated request DTO and [JsonIgnore] " +
                    "for properties that must not be bound.");
            }

            requiresObjectValidation =
                DynamicHttpValidation.RequiresObjectValidation(p.ParameterType);

            if (requiresObjectValidation)
            {
                // Build the validation plan during discovery, not on the first request.
                DynamicHttpValidation.WarmPlans(p.ParameterType);
            }
        }

        return definition with
        {
            ValidationAttributes = p.GetCustomAttributes<ValidationAttribute>(true).ToArray(),
            // Object validation applies to body models. Injected services and framework
            // parameters are not DataAnnotations models and must not be validated.
            RequiresObjectValidation = requiresObjectValidation
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
        bool methodAnonymous = IsAllowAnonymous(serviceType, method);
        bool classAuthorized = IsAuthorized(serviceType);
        bool methodAuthorized = IsAuthorized(serviceType, method);

        if (classAuthorized && classAnonymous)
        {
            throw Configuration(serviceType, method,
                "A service cannot combine an authorization attribute and an allow-anonymous attribute on the same class or interface.");
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
    /// metadata. Attributes declared on implemented interfaces are included at both type and
    /// method level. All of them are applied, so mixing the two attribute families is fail-closed.
    /// </summary>
    private static AuthorizeAttribute[] GetAuthorization(Type serviceType, MethodInfo method)
    {
        List<AuthorizeAttribute> result = [];

        foreach (Type scope in GetTypesInScope(serviceType))
        {
            result.AddRange(scope.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
                .Select(ToAuthorizeAttribute));
            result.AddRange(scope.GetCustomAttributes<DynamicAuthorizeAttribute>(true)
                .Select(ToAuthorizeAttribute));
        }

        foreach (MethodInfo scope in GetMethodsInScope(serviceType, method))
        {
            result.AddRange(scope.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>()
                .Select(ToAuthorizeAttribute));
            result.AddRange(scope.GetCustomAttributes<DynamicAuthorizeAttribute>(true)
                .Select(ToAuthorizeAttribute));
        }

        return [.. result
            .GroupBy(attribute => (attribute.Policy, attribute.Roles, attribute.AuthenticationSchemes))
            .Select(group => group.First())];
    }

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

    private static IEnumerable<Type> GetTypesInScope(Type serviceType) =>
        [serviceType, .. serviceType.GetInterfaces()];

    private static IEnumerable<MethodInfo> GetMethodsInScope(Type serviceType, MethodInfo method) =>
        [method, .. GetInterfaceMethods(serviceType, method)];

    private static IEnumerable<MethodInfo> GetInterfaceMethods(Type serviceType, MethodInfo method)
    {
        foreach (Type @interface in serviceType.GetInterfaces())
        {
            InterfaceMapping map;

            try
            {
                map = serviceType.GetInterfaceMap(@interface);
            }
            catch (ArgumentException)
            {
                // A malformed or partially implemented interface cannot contribute attributes.
                continue;
            }

            for (int i = 0; i < map.TargetMethods.Length; i++)
            {
                if (map.TargetMethods[i].Equals(method))
                {
                    yield return map.InterfaceMethods[i];
                }
            }
        }
    }

    private static bool IsAllowAnonymous(Type serviceType) =>
        IsAllowAnonymousOnType(serviceType) ||
        serviceType.GetInterfaces().Any(IsAllowAnonymousOnType);

    private static bool IsAllowAnonymous(Type serviceType, MethodInfo method) =>
        IsAllowAnonymousOnMethod(method) ||
        GetInterfaceMethods(serviceType, method).Any(IsAllowAnonymousOnMethod);

    private static bool IsAllowAnonymousOnType(Type type) =>
        type.IsDefined(typeof(DynamicAllowAnonymousAttribute), true) ||
        type.IsDefined(typeof(AllowAnonymousAttribute), true);

    private static bool IsAllowAnonymousOnMethod(MethodInfo method) =>
        method.IsDefined(typeof(DynamicAllowAnonymousAttribute), true) ||
        method.IsDefined(typeof(AllowAnonymousAttribute), true);

    private static bool IsAuthorized(Type serviceType) =>
        IsAuthorizedOnType(serviceType) ||
        serviceType.GetInterfaces().Any(IsAuthorizedOnType);

    private static bool IsAuthorized(Type serviceType, MethodInfo method) =>
        IsAuthorizedOnMethod(method) ||
        GetInterfaceMethods(serviceType, method).Any(IsAuthorizedOnMethod);

    private static bool IsAuthorizedOnType(Type type) =>
        type.IsDefined(typeof(DynamicAuthorizeAttribute), true) ||
        type.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any();

    private static bool IsAuthorizedOnMethod(MethodInfo method) =>
        method.IsDefined(typeof(DynamicAuthorizeAttribute), true) ||
        method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().Any();

    private static bool IsAntiforgeryRequired(Type serviceType, MethodInfo method) =>
        serviceType.IsDefined(typeof(DynamicAntiforgeryAttribute), true) ||
        method.IsDefined(typeof(DynamicAntiforgeryAttribute), true);

    internal static string CombineRoutes(string? prefix, string? template)
    {
        if (prefix is null || template is null)
        {
            throw new DynamicHttpConfigurationException(
                "DynamicHttp configuration error: the route prefix and HTTP method template must not be null.");
        }

        string a = prefix.Trim('/');
        string b = template.Trim('/');
        string route = string.Join("/", new[] { a, b }.Where(x => x.Length > 0));

        return "/" + route;
    }

    internal static string GetVerb(HttpMethodAttribute attribute)
    {
        string? method = attribute.Method;

        if (string.IsNullOrWhiteSpace(method))
        {
            throw new DynamicHttpConfigurationException(
                $"DynamicHttp configuration error: HTTP attribute '{attribute.GetType().Name}' " +
                "specified an empty HTTP method.");
        }

        return method.Trim().ToUpperInvariant();
    }

    internal static void ValidateParameters(string route,
        IReadOnlyList<ParameterDefinition> parameters,
        Type service,
        MethodInfo method)
    {
        HashSet<string> routeNames = ParseRoutePattern(route, service, method).Parameters
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
        var candidates = endpoints
            .GroupBy(x => $"{x.HttpMethod} {NormalizeRoute(x.Route)}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1);

        foreach (var group in candidates)
        {
            EndpointDefinition[] items = [.. group];

            for (int i = 0; i < items.Length; i++)
            {
                for (int j = i + 1; j < items.Length; j++)
                {
                    if (!ConstraintsMayOverlap(items[i].Route, items[j].Route))
                    {
                        continue;
                    }

                    string routes = string.Join(", ", items.Select(x => $"{x.HttpMethod} {x.Route}"));
                    throw new DynamicHttpConfigurationException($"Conflicting dynamic endpoints: {routes}");
                }
            }
        }
    }

    /// <summary>
    /// Builds a canonical representation of a route template that ignores parameter names,
    /// optional/default markers and constraint content, while preserving route precedence:
    /// simple parameters, constrained parameters and catch-all parameters produce different keys.
    /// Templates with the same key can match the same request and are checked for overlapping
    /// constraints by <see cref="ValidateConflicts"/>.
    /// </summary>
    internal static string NormalizeRoute(string route)
    {
        RoutePattern pattern = ParseRoutePattern(route);
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
                        if (parameter.ParameterPolicies.Count > 0)
                        {
                            // Any constraint makes the parameter more specific than an
                            // unconstrained one, regardless of the constraint content.
                            builder.Append(":C");
                        }
                        builder.Append('}');
                        break;
                }
            }
        }

        return builder.ToString();
    }

    private static bool ConstraintsMayOverlap(string leftRoute, string rightRoute)
    {
        RoutePattern left = ParseRoutePattern(leftRoute);
        RoutePattern right = ParseRoutePattern(rightRoute);

        RoutePatternParameterPart[] leftParameters = [.. left.Parameters];
        RoutePatternParameterPart[] rightParameters = [.. right.Parameters];

        if (leftParameters.Length != rightParameters.Length)
        {
            return true;
        }

        for (int i = 0; i < leftParameters.Length; i++)
        {
            if (!PoliciesMayOverlap(leftParameters[i].ParameterPolicies,
                rightParameters[i].ParameterPolicies))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PoliciesMayOverlap(
        IReadOnlyList<RoutePatternParameterPolicyReference> left,
        IReadOnlyList<RoutePatternParameterPolicyReference> right)
    {
        ConstraintKind leftKind = ClassifyConstraints(left);
        ConstraintKind rightKind = ClassifyConstraints(right);

        // Unknown or purely neutral constraints cannot be proven disjoint.
        if (leftKind is ConstraintKind.None or ConstraintKind.Unknown ||
            rightKind is ConstraintKind.None or ConstraintKind.Unknown)
        {
            return true;
        }

        return leftKind == rightKind ||
            (leftKind == ConstraintKind.Boolean && rightKind == ConstraintKind.Alpha) ||
            (leftKind == ConstraintKind.Alpha && rightKind == ConstraintKind.Boolean);
    }

    private static ConstraintKind ClassifyConstraints(
        IReadOnlyList<RoutePatternParameterPolicyReference> policies)
    {
        ConstraintKind kind = ConstraintKind.None;

        foreach (var policy in policies)
        {
            string content = policy.Content ?? string.Empty;
            int parenthesis = content.IndexOf('(');
            string name = (parenthesis >= 0 ? content[..parenthesis] : content)
                .Trim()
                .ToLowerInvariant();

            ConstraintKind candidate = name switch
            {
                "int" or "long" or "short" or "byte" or "decimal" or "double" or "float" or
                    "min" or "max" or "range" => ConstraintKind.Numeric,
                "guid" => ConstraintKind.Guid,
                "bool" => ConstraintKind.Boolean,
                "datetime" => ConstraintKind.DateTime,
                "alpha" => ConstraintKind.Alpha,
                "required" or "length" or "minlength" or "maxlength" => ConstraintKind.Neutral,
                _ => ConstraintKind.Unknown
            };

            if (candidate == ConstraintKind.Unknown)
            {
                return ConstraintKind.Unknown;
            }

            if (candidate == ConstraintKind.Neutral)
            {
                continue;
            }

            if (kind == ConstraintKind.None)
            {
                kind = candidate;
            }
            else if (kind != candidate)
            {
                // Combined constraints from different families cannot be compared safely.
                return ConstraintKind.Unknown;
            }
        }

        return kind;
    }

    private static RoutePattern ParseRoutePattern(string route, Type? service = null,
        MethodInfo? method = null)
    {
        try
        {
            return RoutePatternFactory.Parse(route);
        }
        catch (RoutePatternException exception)
        {
            string context = service is not null && method is not null
                ? $"DynamicHttp configuration error for '{service.FullName}.{method.Name}': "
                : "DynamicHttp configuration error: ";

            throw new DynamicHttpConfigurationException(
                $"{context}route template '{route}' is not valid: {exception.Message}", exception);
        }
    }

    private enum ConstraintKind
    {
        None,
        Unknown,
        Neutral,
        Numeric,
        Guid,
        Boolean,
        DateTime,
        Alpha
    }

    private static DynamicHttpConfigurationException Configuration(Type service, MethodInfo method, string message) =>
        new($"DynamicHttp configuration error for '{service.FullName}.{method.Name}': {message}");
}
