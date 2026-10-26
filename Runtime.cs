using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    internal bool RequireAuthorizationByDefault => _options.RequireAuthorizationByDefault;

    internal bool ValidateModels => _options.ValidateModels;

    internal long? MaxRequestBodyBytes => _options.MaxRequestBodyBytes;
}

public static class DynamicHttpEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapDynamicHttp(this IEndpointRouteBuilder endpoints)
    {
        var registry = endpoints.ServiceProvider.GetRequiredService<DynamicHttpRegistry>();

        if (registry.RequireAuthorizationByDefault &&
            endpoints.ServiceProvider.GetService<IAuthorizationPolicyProvider>() is null)
        {
            throw new DynamicHttpConfigurationException(
                "DynamicHttp configuration error: RequireAuthorizationByDefault requires authorization services. " +
                "Call AddAuthorization()/AddAuthorizationBuilder() when registering services and add " +
                "UseAuthentication()/UseAuthorization() to the pipeline.");
        }

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
                        if (definition.RequireAntiforgery &&
                            IsStateChangingMethod(definition.HttpMethod) &&
                            context.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
                        {
                            // AntiforgeryMiddleware validates the token and records the result in
                            // this feature. Fail closed: a missing middleware (null feature) must
                            // reject too, otherwise [DynamicAntiforgery] would silently do nothing.
                            throw new BadRequestHttpException("The antiforgery token is missing or invalid.");
                        }

                        if (registry.MaxRequestBodyBytes is long maxRequestBodyBytes)
                        {
                            if (context.Request.ContentLength > maxRequestBodyBytes)
                            {
                                throw new PayloadTooLargeHttpException(
                                    $"The request body exceeds the configured limit of {maxRequestBodyBytes} bytes.");
                            }

                            // Enforce the same limit for chunked bodies, where Content-Length is absent.
                            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                            {
                                feature.MaxRequestBodySize = maxRequestBodyBytes;
                            }
                        }

                        object?[] arguments = new object?[definition.Parameters.Count];

                        for (int i = 0; i < definition.Parameters.Count; i++)
                        {
                            object? argument = await DynamicHttpBinding.BindAsync(context, definition.Parameters[i]);
                            DynamicHttpBinding.Validate(argument, definition.Parameters[i],
                                context.RequestServices, registry.ValidateModels);
                            arguments[i] = argument;
                        }

                        object service = context.RequestServices.GetRequiredService(definition.ServiceType);
                        object? result = definition.Invoker(service, arguments);

                        result = await DynamicHttpAwaiting.AwaitAsync(result);

                        return result is IResult httpResult ? httpResult : Results.Ok(result);
                    }
                    catch (Exception exception) when (exception is not IHttpException and not BadHttpRequestException)
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

            foreach (AuthorizeAttribute authorization in definition.Authorization)
            {
                // Discovery normalizes each attribute (policy/roles/schemes) and applies all of
                // them to the same endpoint, so mixing DynamicHttp and framework attributes is
                // fail-closed.
                route.RequireAuthorization(authorization);
            }

            if (registry.RequireAuthorizationByDefault &&
                definition.Authorization.Count == 0 &&
                !definition.AllowAnonymous)
            {
                route.RequireAuthorization();
            }

            if (definition.RequireAntiforgery)
            {
                // The endpoint metadata consumed by AntiforgeryMiddleware. Requires
                // app.UseAntiforgery() in the pipeline.
                route.WithMetadata(new RequireAntiforgeryTokenAttribute(required: true));
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

    private static bool IsStateChangingMethod(string httpMethod) =>
        HttpMethods.IsPost(httpMethod) ||
        HttpMethods.IsPut(httpMethod) ||
        HttpMethods.IsPatch(httpMethod) ||
        HttpMethods.IsDelete(httpMethod);
}
