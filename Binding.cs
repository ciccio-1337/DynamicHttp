using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicHttp;

internal static class DynamicHttpBinding
{
    public static async ValueTask<object?> BindAsync(HttpContext context, ParameterDefinition definition)
    {
        return definition.Kind switch
        {
            BindingKind.CancellationToken => context.RequestAborted,
            BindingKind.Services => context.RequestServices.GetRequiredService(definition.ParameterType),
            BindingKind.HttpContext => context,
            BindingKind.HttpRequest => context.Request,
            BindingKind.HttpResponse => context.Response,
            BindingKind.User => context.User,
            BindingKind.Route => ConvertValue(context.Request.RouteValues[definition.Name], definition),
            BindingKind.Query => ConvertValue(context.Request.Query[definition.Name].FirstOrDefault(), definition),
            BindingKind.Header => ConvertValue(context.Request.Headers[definition.Name].FirstOrDefault(), definition),
            BindingKind.Body => await BindBodyAsync(context, definition),
            _ => throw new ArgumentOutOfRangeException(nameof(definition.Kind))
        };
    }

    private static async ValueTask<object?> BindBodyAsync(HttpContext context, ParameterDefinition definition)
    {
        if (!context.Request.HasJsonContentType())
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' requires a 'application/json' request body.");
        }

        if (context.Request.ContentLength is 0)
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' requires a non-empty request body.");
        }

        object? body;

        try
        {
            body = await context.Request.ReadFromJsonAsync(definition.ParameterType, context.RequestAborted);
        }
        catch (JsonException)
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' could not be deserialized: the request body is not valid JSON.");
        }
        catch (NotSupportedException exception)
        {
            throw new BadRequestHttpException(
                $"Parameter '{definition.Name}' cannot be bound from an 'application/json' request body.", exception);
        }

        // A JSON null is only valid for nullable parameters. An absent/empty body is rejected
        // above (or surfaces as a JsonException for chunked requests).
        if (body is null && !definition.IsNullable)
        {
            throw new BadRequestHttpException($"Parameter '{definition.Name}' requires a non-null JSON value.");
        }

        return body;
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

            if (!definition.IsNullable)
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
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
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
        catch (Exception exception) when (exception is FormatException or NotSupportedException or
            ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static BadRequestHttpException InvalidValue(ParameterDefinition definition, object value, Exception innerException) =>
        new($"The value '{value}' for parameter '{definition.Name}' is not valid.", innerException);
}
