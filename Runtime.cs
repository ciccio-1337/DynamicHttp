using System.Diagnostics;
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
                            arguments[i] = await DynamicHttpBinding.BindAsync(context, definition.Parameters[i]);
                        }

                        object service = context.RequestServices.GetRequiredService(definition.ServiceType);
                        object? result = definition.Invoker(service, arguments);

                        result = await DynamicHttpAwaiting.AwaitAsync(result);

                        return result is IResult httpResult ? httpResult : Results.Ok(result);
                    }
                    catch (Exception exception) when (exception is not IHttpException)
                    {
                        // Controlled HTTP exceptions (400, 404, …) are intentionally thrown by
                        // the service; DynamicHttpExceptionHandler logs them at Information. Log
                        // only unexpected errors here, and mark them so the handler does not log
                        // the same exception a second time.
                        if (!(exception is OperationCanceledException &&
                            context.RequestAborted.IsCancellationRequested))
                        {
                            context.Items[DynamicHttpExceptionHandler.LoggedExceptionItemKey] = true;
                            logger.LogError(exception,
                                "DynamicHttp invocation failed for {Service}.{Method}",
                                definition.ServiceType.Name,
                                definition.Method.Name);
                        }

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
                // Apply every non-empty property on the same AuthorizeAttribute. Requiring each
                // property separately could not express Policy + Roles + AuthenticationSchemes,
                // and silently dropping a property would weaken authorization.
                route.RequireAuthorization(new AuthorizeAttribute
                {
                    Policy = Whitelist(authorization.Policy),
                    Roles = Whitelist(authorization.Roles),
                    AuthenticationSchemes = Whitelist(authorization.AuthenticationSchemes)
                });
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

    private static string? Whitelist(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
