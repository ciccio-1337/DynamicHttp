using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicHttp;

public sealed class DynamicHttpRegistry
{
    private readonly DynamicHttpOptions _options;
    private readonly Lazy<IReadOnlyList<EndpointDefinition>> _definitions;

    public DynamicHttpRegistry(DynamicHttpOptions options)
    {
        _options = options;
        _definitions = new(() => DynamicHttpDiscovery.Build(_options.Assemblies),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal IReadOnlyList<EndpointDefinition> Definitions => _definitions.Value;
}

public static class DynamicHttpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDynamicHttp(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<DynamicHttpRegistry>();

        foreach (var definition in registry.Definitions)
        {
            var route = endpoints.MapMethods(definition.Route,
                [definition.HttpMethod],
                async (HttpContext context) =>
                {
                    var logger = context.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("DynamicHttp");

                    Stopwatch stopwatch = Stopwatch.StartNew();

                    try
                    {
                        object?[] arguments = new object?[definition.Parameters.Count];

                        for (int i = 0; i < definition.Parameters.Count; i++)
                        {
                            arguments[i] = await BindAsync(context, definition.Parameters[i]);
                        }

                        object service = context.RequestServices.GetRequiredService(definition.ServiceType);
                        object? result = definition.Invoker(service, arguments);

                        result = await AwaitAsync(result);

                        return result is IResult httpResult ? httpResult : Results.Ok(result);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception,
                            "DynamicHttp invocation failed for {Service}.{Method}",
                            definition.ServiceType.Name,
                            definition.Method.Name);

                        throw;
                    }
                    finally
                    {
                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            logger.LogDebug("DynamicHttp {HttpMethod} {Route} completed in {ElapsedMs} ms",
                                definition.HttpMethod,
                                definition.Route,
                                stopwatch.Elapsed.TotalMilliseconds);
                        }
                    }
                });

            if (definition.AllowAnonymous)
            {
                route.AllowAnonymous();
            }

            foreach (var authorization in definition.Authorization)
            {
                if (!string.IsNullOrWhiteSpace(authorization.Policy))
                {
                    route.RequireAuthorization(authorization.Policy);
                }
                else if (!string.IsNullOrWhiteSpace(authorization.Roles))
                {
                    route.RequireAuthorization(new AuthorizeAttribute
                    {
                        Roles = authorization.Roles,
                        AuthenticationSchemes = authorization.AuthenticationSchemes
                    });
                }
                else
                {
                    route.RequireAuthorization(new AuthorizeAttribute
                    {
                        AuthenticationSchemes = authorization.AuthenticationSchemes
                    });
                }
            }

            foreach (string tag in definition.Tags)
            {
                route.WithTags(tag);
            }

            if (definition.GroupName is not null)
            {
                route.WithGroupName(definition.GroupName);
            }

            foreach (var response in definition.Responses)
            {
                if (response.ResponseType is not null)
                {
                    route.Produces(response.StatusCode, response.ResponseType);
                }
                else
                {
                    route.Produces(response.StatusCode);
                }
            }
        }

        return endpoints;
    }

    private static async ValueTask<object?> BindAsync(HttpContext context, ParameterDefinition definition)
    {
        return definition.Kind switch
        {
            BindingKind.CancellationToken => context.RequestAborted,
            BindingKind.Services => context.RequestServices.GetRequiredService(definition.ParameterType),
            BindingKind.Route => ConvertValue(context.Request.RouteValues[definition.Name], definition.ParameterType),
            BindingKind.Query => ConvertValue(context.Request.Query[definition.Name].FirstOrDefault(), definition.ParameterType),
            BindingKind.Header => ConvertValue(context.Request.Headers[definition.Name].FirstOrDefault(), definition.ParameterType),
            BindingKind.Body => await context.Request.ReadFromJsonAsync(definition.ParameterType, context.RequestAborted),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };
    }

    private static object? ConvertValue(object? value, Type type)
    {
        if (value is null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                return Activator.CreateInstance(type);
            }

            return null;
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        if (target.IsEnum)
        {
            return Enum.Parse(target, value.ToString()!, true);
        }

        if (target == typeof(Guid))
        {
            return Guid.Parse(value.ToString()!);
        }

        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<object?> AwaitAsync(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Task task)
        {
            await task.ConfigureAwait(false);

            return task.GetType().GetProperty("Result")?.GetValue(task);
        }

        if (value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);

            return null;
        }

        return value;
    }
}
