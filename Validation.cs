using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DynamicHttp;

/// <summary>
/// Builds and caches a validation plan for an object graph. The plan records which types have
/// direct DataAnnotations metadata and which properties/collections must be traversed, so the
/// request path does not need to inspect attributes on every object.
/// </summary>
internal static class DynamicHttpValidation
{
    private static readonly ConcurrentDictionary<Type, ObjectValidationPlan> Plans = new();
    private static readonly ConcurrentDictionary<Type, bool> Requirements = new();

    /// <summary>
    /// Returns true when the type or any type reachable from it declares DataAnnotations
    /// metadata or implements <see cref="IValidatableObject"/>.
    /// </summary>
    public static bool RequiresObjectValidation(Type type) =>
        Requirements.GetOrAdd(type, static candidate => ComputeRequires(candidate));

    /// <summary>
    /// Warms the plan for a body type at startup, including every reachable complex type.
    /// </summary>
    public static void WarmPlans(Type type)
    {
        Queue<Type> queue = new();
        HashSet<Type> visited = [];
        queue.Enqueue(Unwrap(type));

        while (queue.TryDequeue(out Type? current))
        {
            if (!visited.Add(current) || IsSimple(current))
            {
                continue;
            }

            ObjectValidationPlan plan = GetPlan(current);

            foreach (ValidationPropertyPlan property in plan.Properties)
            {
                if (property.ChildType is not null)
                {
                    queue.Enqueue(property.ChildType);
                }
            }
        }
    }

    /// <summary>
    /// Recursively validates a value and returns the accumulated results. Nested member names
    /// are prefixed with their object path (for example <c>lines[0].price</c>). The raw value is
    /// never included in the result.
    /// </summary>
    public static IReadOnlyList<ValidationResult> Validate(object value, IServiceProvider services)
    {
        List<ValidationResult> results = [];
        ValidateNode(value, string.Empty, services, results,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        return results;
    }

    /// <summary>
    /// Returns the path of the first property decorated with <c>[BindNever]</c> in the object
    /// graph, or null when there is none. DynamicHttp binds JSON bodies as a whole and cannot
    /// honor the MVC-only <c>[BindNever]</c> contract, so discovery rejects such models.
    /// </summary>
    public static string? FindBindNeverProperty(Type type)
    {
        Queue<(Type Type, string Path)> queue = new();
        HashSet<Type> visited = [];
        queue.Enqueue((Unwrap(type), string.Empty));

        while (queue.TryDequeue(out (Type Type, string Path) current))
        {
            if (!visited.Add(current.Type) || IsSimple(current.Type))
            {
                continue;
            }

            foreach (PropertyInfo property in current.Type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                string path = string.IsNullOrEmpty(current.Path)
                    ? property.Name
                    : $"{current.Path}.{property.Name}";

                if (property.IsDefined(typeof(BindNeverAttribute), inherit: true))
                {
                    return path;
                }

                Type propertyType = Unwrap(property.PropertyType);

                if (IsSimple(propertyType))
                {
                    continue;
                }

                if (TryGetEnumerableElementType(propertyType, out Type elementType))
                {
                    elementType = Unwrap(elementType);

                    if (!IsSimple(elementType))
                    {
                        queue.Enqueue((elementType, $"{path}[]"));
                    }
                }
                else
                {
                    queue.Enqueue((propertyType, path));
                }
            }
        }

        return null;
    }

    internal static bool IsSimple(Type type)
    {
        type = Unwrap(type);

        return type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(Guid) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) ||
            type == typeof(decimal);
    }

    private static ObjectValidationPlan GetPlan(Type type) =>
        Plans.GetOrAdd(Unwrap(type), static candidate => BuildPlan(candidate));

    private static ObjectValidationPlan BuildPlan(Type type)
    {
        bool hasDirectValidation = typeof(IValidatableObject).IsAssignableFrom(type);

        List<ValidationPropertyPlan> properties = [];

        foreach (PropertyInfo property in type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            if (!hasDirectValidation &&
                property.GetCustomAttributes<ValidationAttribute>(true).Any())
            {
                hasDirectValidation = true;
            }

            Type propertyType = Unwrap(property.PropertyType);

            if (IsSimple(propertyType))
            {
                continue;
            }

            if (TryGetEnumerableElementType(propertyType, out Type elementType))
            {
                elementType = Unwrap(elementType);

                if (!IsSimple(elementType))
                {
                    properties.Add(new ValidationPropertyPlan(property, IsEnumerable: true, elementType));
                }
            }
            else
            {
                properties.Add(new ValidationPropertyPlan(property, IsEnumerable: false, propertyType));
            }
        }

        return new ObjectValidationPlan(type, hasDirectValidation, properties);
    }

    private static bool ComputeRequires(Type root)
    {
        Queue<Type> queue = new();
        HashSet<Type> visited = [];
        queue.Enqueue(Unwrap(root));

        while (queue.TryDequeue(out Type? type))
        {
            if (!visited.Add(type) || IsSimple(type))
            {
                continue;
            }

            if (typeof(IValidatableObject).IsAssignableFrom(type))
            {
                return true;
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (property.GetCustomAttributes<ValidationAttribute>(true).Any())
                {
                    return true;
                }

                Type propertyType = Unwrap(property.PropertyType);

                if (IsSimple(propertyType))
                {
                    continue;
                }

                if (TryGetEnumerableElementType(propertyType, out Type elementType))
                {
                    elementType = Unwrap(elementType);

                    if (!IsSimple(elementType))
                    {
                        queue.Enqueue(elementType);
                    }
                }
                else
                {
                    queue.Enqueue(propertyType);
                }
            }
        }

        return false;
    }

    private static void ValidateNode(object value, string path, IServiceProvider services,
        List<ValidationResult> results, HashSet<object> visited)
    {
        Type type = value.GetType();

        if (IsSimple(type))
        {
            return;
        }

        if (!type.IsValueType && !visited.Add(value))
        {
            // The graph contains a cycle (or a shared reference); validate each object once.
            return;
        }

        ObjectValidationPlan plan = GetPlan(type);

        if (plan.HasDirectValidation)
        {
            List<ValidationResult> nodeResults = [];
            var context = new ValidationContext(value, services, items: null);

            Validator.TryValidateObject(value, context, nodeResults, validateAllProperties: true);

            foreach (ValidationResult result in nodeResults)
            {
                results.Add(Prefix(result, path));
            }
        }

        foreach (ValidationPropertyPlan property in plan.Properties)
        {
            object? child;

            try
            {
                child = property.Property.GetValue(value);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // A property getter is application code. Do not let a broken getter turn input
                // validation into a 500; skip the property and let the endpoint decide.
                continue;
            }

            if (child is null)
            {
                continue;
            }

            string childPath = string.IsNullOrEmpty(path)
                ? property.Property.Name
                : $"{path}.{property.Property.Name}";

            if (property.IsEnumerable)
            {
                int index = 0;

                foreach (object? item in (IEnumerable)child)
                {
                    if (item is not null)
                    {
                        ValidateNode(item, $"{childPath}[{index}]", services, results, visited);
                    }

                    index++;
                }
            }
            else
            {
                ValidateNode(child, childPath, services, results, visited);
            }
        }
    }

    private static ValidationResult Prefix(ValidationResult result, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return result;
        }

        string[] members = result.MemberNames
            .Select(member => string.IsNullOrEmpty(member) ? path : $"{path}.{member}")
            .ToArray();

        return new ValidationResult(result.ErrorMessage, members.Length == 0 ? [path] : members);
    }

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = type;

            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;

            return true;
        }

        Type[] interfaces = type.IsInterface
            ? [type, .. type.GetInterfaces()]
            : type.GetInterfaces();

        foreach (Type candidate in interfaces)
        {
            if (candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = candidate.GetGenericArguments()[0];

                return true;
            }
        }

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            // Non-generic collections are traversed with their runtime element types.
            elementType = typeof(object);

            return true;
        }

        elementType = type;

        return false;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private sealed record ObjectValidationPlan(Type Type,
        bool HasDirectValidation,
        IReadOnlyList<ValidationPropertyPlan> Properties);

    private sealed record ValidationPropertyPlan(PropertyInfo Property,
        bool IsEnumerable,
        Type? ChildType);
}
