using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
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

    /// <summary>
    /// Applies DataAnnotations validation to a bound argument when
    /// <see cref="DynamicHttpOptions.ValidateModels"/> is enabled. Parameter attributes and object
    /// validation share the same switch. Error messages never include the raw request value.
    /// </summary>
    public static void Validate(object? value, ParameterDefinition definition,
        IServiceProvider services, bool validateModels)
    {
        if (!validateModels)
        {
            return;
        }

        if (definition.ValidationAttributes.Count > 0)
        {
            // ValidationContext requires a non-null instance even when validating a null value.
            var context = new ValidationContext(value ?? new object(), services, items: null)
            {
                MemberName = definition.Name
            };
            var results = new List<ValidationResult>();

            if (!Validator.TryValidateValue(value, context, results, definition.ValidationAttributes))
            {
                throw new BadRequestHttpException(FormatErrors(definition.Name, results));
            }
        }

        if (!definition.RequiresObjectValidation || value is null)
        {
            return;
        }

        IReadOnlyList<ValidationResult> objectResults =
            DynamicHttpValidation.Validate(value, services);

        if (objectResults.Count > 0)
        {
            throw new BadRequestHttpException(FormatErrors(definition.Name, objectResults));
        }
    }

    private static string FormatErrors(string parameterName, IEnumerable<ValidationResult> results)
    {
        string messages = string.Join("; ", results
            .Where(result => !string.IsNullOrWhiteSpace(result.ErrorMessage))
            .Select(result =>
            {
                string? member = result.MemberNames
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

                return string.IsNullOrEmpty(member)
                    ? result.ErrorMessage
                    : $"{member}: {result.ErrorMessage}";
            }));

        return messages.Length == 0
            ? $"Parameter '{parameterName}' is not valid."
            : $"Parameter '{parameterName}' is not valid: {messages}";
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

        // An explicitly empty query/header value (for example "?page=") is treated as absent for
        // nullable value types, matching the usual minimal-API behavior.
        if (value is string { Length: 0 } && Nullable.GetUnderlyingType(type) is not null)
        {
            value = null;
        }

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
            object parsed;

            try
            {
                parsed = Enum.Parse(target, value.ToString()!, ignoreCase: true);
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                throw InvalidValue(definition, exception);
            }

            if (!IsDefinedEnumValue(target, parsed))
            {
                throw InvalidValue(definition);
            }

            return parsed;
        }

        if (target == typeof(Guid))
        {
            try
            {
                return Guid.Parse(value.ToString()!);
            }
            catch (FormatException exception)
            {
                throw InvalidValue(definition, exception);
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

            throw InvalidValue(definition, exception);
        }
    }

    /// <summary>
    /// Enum.Parse accepts any numeric value that fits the underlying type, including values that
    /// are not declared on the enum. Reject those so an endpoint cannot receive undefined states.
    /// </summary>
    private static bool IsDefinedEnumValue(Type enumType, object value)
    {
        if (!enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            return Enum.IsDefined(enumType, value);
        }

        // Accept any value that is declared exactly. This includes a negative sentinel such as
        // "All = -1", which would otherwise act as a wildcard for every bit combination.
        if (Enum.IsDefined(enumType, value))
        {
            return true;
        }

        ulong allowed = 0;

        foreach (object defined in Enum.GetValues(enumType))
        {
            // Negative sentinels must not widen the mask to every bit. They remain accepted only
            // through the exact-match check above.
            if (!IsNegativeEnumValue(defined))
            {
                allowed |= EnumBits(defined);
            }
        }

        // Undefined combinations are accepted only when every bit is part of a declared,
        // non-negative flag.
        return (EnumBits(value) & ~allowed) == 0;
    }

    private static bool IsNegativeEnumValue(object value)
    {
        Type underlying = Enum.GetUnderlyingType(value.GetType());

        return Type.GetTypeCode(underlying) switch
        {
            TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 =>
                Convert.ToInt64(value, CultureInfo.InvariantCulture) < 0,
            _ => false
        };
    }

    private static ulong EnumBits(object value)
    {
        // Enum values are boxed as their declared enum type, not as the underlying integer type.
        Type underlying = Enum.GetUnderlyingType(value.GetType());

        return Type.GetTypeCode(underlying) switch
        {
            TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 =>
                unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture)
        };
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

    private static BadRequestHttpException InvalidValue(ParameterDefinition definition, Exception? innerException = null) =>
        new($"The value for parameter '{definition.Name}' is not valid for type '{definition.ParameterType.Name}'.",
            innerException ?? new FormatException("The value could not be converted."));
}
