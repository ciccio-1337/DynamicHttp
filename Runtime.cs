using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly ILogger? _logger;
    private readonly Lazy<IReadOnlyList<EndpointDefinition>> _definitions;

    public DynamicHttpRegistry(DynamicHttpOptions options, ILoggerFactory? loggerFactory = null)
    {
        _options = options;
        _logger = loggerFactory?.CreateLogger("DynamicHttp");
        _definitions = new(() => DynamicHttpDiscovery.Build(_options.Assemblies, _logger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal IReadOnlyList<EndpointDefinition> Definitions => _definitions.Value;

    internal bool RequireAuthorizationByDefault => _options.RequireAuthorizationByDefault;

    internal bool ValidateModels => _options.ValidateModels;

    internal long? MaxRequestBodyBytes => _options.MaxRequestBodyBytes;
}

public static class DynamicHttpEndpointRouteBuilderExtensions
{
    private static readonly ConditionalWeakTable<IEndpointRouteBuilder, object> MappedBuilders = new();

    public static IEndpointRouteBuilder MapDynamicHttp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (MappedBuilders.TryGetValue(endpoints, out _))
        {
            // Mapping is idempotent: calling it again must not register duplicate endpoints,
            // which would make routing ambiguous at request time.
            return endpoints;
        }

        var registry = endpoints.ServiceProvider.GetService<DynamicHttpRegistry>();

        if (registry is null)
        {
            throw new DynamicHttpConfigurationException(
                "DynamicHttp configuration error: AddDynamicHttp() must be called before MapDynamicHttp().");
        }

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
                            IsStateChangingMethod(definition.HttpMethod))
                        {
                            await ValidateAntiforgeryAsync(context);
                        }

                        EnforceRequestBodyLimit(context, registry.MaxRequestBodyBytes);

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

        MappedBuilders.Add(endpoints, new object());

        return endpoints;
    }

    internal static async Task ValidateAntiforgeryAsync(HttpContext context)
    {
        // Validate directly instead of relying on AntiforgeryMiddleware. The framework rejects
        // endpoints carrying antiforgery metadata when app.UseAntiforgery() is missing, turning a
        // missing middleware into a 500. Validating here keeps the check fail-closed with a 400
        // whether or not the middleware is in the pipeline.
        IAntiforgery? antiforgery = context.RequestServices.GetService<IAntiforgery>();

        if (antiforgery is null)
        {
            throw new BadRequestHttpException("The antiforgery token is missing or invalid.");
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            throw new BadRequestHttpException("The antiforgery token is missing or invalid.");
        }
    }

    internal static void EnforceRequestBodyLimit(HttpContext context, long? limit)
    {
        if (limit is not long maxRequestBodyBytes)
        {
            return;
        }

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

    private static bool IsStateChangingMethod(string httpMethod) =>
        HttpMethods.IsPost(httpMethod) ||
        HttpMethods.IsPut(httpMethod) ||
        HttpMethods.IsPatch(httpMethod) ||
        HttpMethods.IsDelete(httpMethod);
}
