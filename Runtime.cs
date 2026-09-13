using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
            BindingKind.Route => ConvertValue(context.Request.RouteValues[definition.Name], definition),
            BindingKind.Query => ConvertValue(context.Request.Query[definition.Name].FirstOrDefault(), definition),
            BindingKind.Header => ConvertValue(context.Request.Headers[definition.Name].FirstOrDefault(), definition),
            BindingKind.Body => await BindBodyAsync(context, definition),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };
    }

    private static async ValueTask<object?> BindBodyAsync(HttpContext context, ParameterDefinition definition)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' requires a 'application/json' request body.");
        }

        try
        {
            object? body = await context.Request.ReadFromJsonAsync(definition.ParameterType, context.RequestAborted);

            if (body is null)
            {
                throw new BadRequestHttpException($"Parameter '{definition.Name}' requires a non-empty request body.");
            }

            return body;
        }
        catch (JsonException)
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' could not be deserialized: the request body is not valid JSON.");
        }
    }

    private static object? ConvertValue(object? value, ParameterDefinition definition)
    {
        Type type = definition.ParameterType;

        if (value is null)
        {
            if (definition.Parameter.HasDefaultValue && definition.Parameter.DefaultValue is not DBNull)
            {
                // A declared default is honored for every binding kind: non-nullable value
                // types, nullable value types and reference types alike.
                return definition.Parameter.DefaultValue;
            }

            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null)
            {
                throw new BadRequestHttpException($"A value for parameter '{definition.Name}' is required.");
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
            try
            {
                return Enum.Parse(target, value.ToString()!, ignoreCase: true);
            }
            catch (ArgumentException exception)
            {
                throw InvalidValue(definition, value, exception);
            }
        }

        if (target == typeof(Guid))
        {
            try
            {
                return Guid.Parse(value.ToString()!);
            }
            catch (FormatException exception)
            {
                throw InvalidValue(definition, value, exception);
            }
        }

        try
        {
            return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            if (TryConvertWithTypeConverter(value, target, out object? converted))
            {
                return converted;
            }

            throw InvalidValue(definition, value, exception);
        }
    }

    private static bool TryConvertWithTypeConverter(object value, Type target, out object? converted)
    {
        converted = null;

        TypeConverter converter;

        try
        {
            converter = TypeDescriptor.GetConverter(target);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!converter.CanConvertFrom(typeof(string)))
        {
            return false;
        }

        try
        {
            converted = converter.ConvertFromInvariantString(value.ToString()!);
            
            return true;
        }
        catch (Exception exception) when (exception is FormatException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static BadRequestHttpException InvalidValue(ParameterDefinition definition, object value, Exception innerException) =>
        new($"The value '{value}' for parameter '{definition.Name}' is not valid.", innerException);

    private static async ValueTask<object?> AwaitAsync(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is Task task)
        {
            await task.ConfigureAwait(false);

            return GetTaskResult(task);
        }

        if (value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);

            return null;
        }

        if (value.GetType() is { IsGenericType: true } valueType &&
            valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            // A boxed ValueTask<T> is never `is ValueTask`, so the generic case must be handled
            // explicitly by awaiting AsTask() and reading Result.
            if (valueType.GetMethod(nameof(ValueTask<int>.AsTask))?.Invoke(value, null) is Task genericTask)
            {
                await genericTask.ConfigureAwait(false);
            }

            return valueType.GetProperty(nameof(ValueTask<int>.Result))?.GetValue(value);
        }

        return value;
    }

    private static object? GetTaskResult(Task task)
    {
        Type type = task.GetType();

        return type.IsGenericType
            ? type.GetProperty(nameof(Task<int>.Result))?.GetValue(task)
            : null;
    }
}
