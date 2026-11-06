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

    internal bool RequireAntiforgeryByDefault => _options.RequireAntiforgeryByDefault;

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
                        // Enforce the body limit first: antiforgery may read a form body, so an
                        // oversized request must be rejected before any body parsing happens.
                        EnforceRequestBodyLimit(context, registry.MaxRequestBodyBytes);

                        // An explicit [DynamicAntiforgery] always validates, even on a safe method
                        // such as GET: the developer deliberately opted in. The option only infers
                        // the requirement for methods that can change state.
                        if (definition.RequireAntiforgery ||
                            (registry.RequireAntiforgeryByDefault &&
                                !IsSafeMethod(definition.HttpMethod)))
                        {
                            await ValidateAntiforgeryAsync(context);
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
            throw PayloadTooLarge(maxRequestBodyBytes);
        }

        IHttpMaxRequestBodySizeFeature? feature =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            // Never raise a limit that the server has already configured lower than ours.
            long current = feature.MaxRequestBodySize ?? long.MaxValue;
            feature.MaxRequestBodySize = Math.Min(current, maxRequestBodyBytes);

            return;
        }

        // The server feature is absent or read-only. For a chunked body the declared length is
        // unknown, so wrap the stream and count bytes as they are read. Keep a lower server-side
        // limit if the read-only feature exposes one.
        if (context.Request.ContentLength is null)
        {
            long effectiveLimit = feature?.MaxRequestBodySize is long serverLimit
                ? Math.Min(serverLimit, maxRequestBodyBytes)
                : maxRequestBodyBytes;

            context.Request.Body = new LimitedReadStream(context.Request.Body, effectiveLimit);
        }
    }

    private static bool IsSafeMethod(string httpMethod) =>
        HttpMethods.IsGet(httpMethod) ||
        HttpMethods.IsHead(httpMethod) ||
        HttpMethods.IsOptions(httpMethod) ||
        HttpMethods.IsTrace(httpMethod);

    private static PayloadTooLargeHttpException PayloadTooLarge(long maxRequestBodyBytes) =>
        new($"The request body exceeds the configured limit of {maxRequestBodyBytes} bytes.");

    /// <summary>
    /// Counts bytes read from a request body and rejects the request when the configured limit
    /// is exceeded. Used when the server cannot enforce the limit through
    /// <see cref="IHttpMaxRequestBodySizeFeature"/>.
    /// </summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _bytesRead;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(inner.Read(buffer, offset, Clamp(count)));

        public override int Read(Span<byte> buffer) =>
            Count(inner.Read(buffer[..Clamp(buffer.Length)]));

        public override int ReadByte()
        {
            Span<byte> buffer = stackalloc byte[1];
            int read = inner.Read(buffer);

            return read == 0 ? -1 : CountByte(buffer[0]);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ReadAsyncCore(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private async ValueTask<int> ReadAsyncCore(Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            int read = await inner.ReadAsync(buffer[..Clamp(buffer.Length)], cancellationToken)
                .ConfigureAwait(false);

            return Count(read);
        }

        private int Count(int read)
        {
            if (read <= 0)
            {
                return read;
            }

            _bytesRead += read;

            if (_bytesRead > limit)
            {
                throw PayloadTooLarge(limit);
            }

            return read;
        }

        private int CountByte(byte value)
        {
            _bytesRead++;

            if (_bytesRead > limit)
            {
                throw PayloadTooLarge(limit);
            }

            return value;
        }

        private int Clamp(int requested)
        {
            if (requested <= 0)
            {
                return 0;
            }

            long remaining = limit - _bytesRead;

            if (remaining < 0)
            {
                throw PayloadTooLarge(limit);
            }

            // Read at most one byte past the limit so the overflow is detected without
            // buffering an arbitrarily large chunk.
            return (int)Math.Min(requested, remaining + 1);
        }
    }
}
