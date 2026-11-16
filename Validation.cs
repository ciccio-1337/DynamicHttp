using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DynamicHttp;

/// <summary>
/// Builds and caches a validation plan for an object graph. The plan records which types have
/// direct DataAnnotations metadata and which properties/collections must be traversed, so the
/// request path does not need to inspect attributes on every object.
/// </summary>
internal static class DynamicHttpValidation
{
    /// <summary>
    /// Upper bound on the number of validation errors reported for one argument. Without a cap a
    /// large invalid collection can amplify CPU, memory and response size.
    /// </summary>
    internal const int MaxValidationErrors = 10;

    /// <summary>
    /// One extra result is collected so the caller can tell whether the cap truncated the graph
    /// and append an "additional errors omitted" marker without changing the reported limit.
    /// </summary>
    private const int MaxCollectedErrors = MaxValidationErrors + 1;

    private static readonly ConcurrentDictionary<Type, ObjectValidationPlan> Plans = new();
    private static readonly ConcurrentDictionary<Type, bool> Requirements = new();

    /// <summary>
    /// Returns true when the type or any type reachable from it declares DataAnnotations
    /// metadata or implements <see cref="IValidatableObject"/>. Polymorphic, abstract and
    /// interface inputs always require validation because the runtime type is only known after
    /// deserialization.
    /// </summary>
    public static bool RequiresObjectValidation(Type type) =>
        Requirements.GetOrAdd(type, static candidate => ComputeRequires(candidate));

    /// <summary>
    /// Warms the plan for a body type at startup, including every reachable complex type and
    /// every declared polymorphic derived type.
    /// </summary>
    public static void WarmPlans(Type type)
    {
        foreach ((Type candidate, _) in Traverse(type))
        {
            _ = GetPlan(candidate);
        }
    }

    /// <summary>
    /// Recursively validates a value and returns the accumulated results. Nested member names
    /// are prefixed with their object path (for example <c>lines[0].price</c>). The raw value is
    /// never included in the result. At most <see cref="MaxValidationErrors"/> results are
    /// produced.
    /// </summary>
    public static IReadOnlyList<ValidationResult> Validate(object value, IServiceProvider services) =>
        Validate(value, services, out _);

    /// <summary>
    /// Recursively validates a value and returns the accumulated results, reporting whether more
    /// errors existed than <see cref="MaxValidationErrors"/>. Nested member names are prefixed
    /// with their object path (for example <c>lines[0].price</c>). The raw value is never included
    /// in the result.
    /// </summary>
    public static IReadOnlyList<ValidationResult> Validate(object value, IServiceProvider services,
        out bool truncated)
    {
        List<ValidationResult> results = [];
        ValidateNode(value, string.Empty, services, results,
            new HashSet<object>(ReferenceEqualityComparer.Instance));

        truncated = results.Count > MaxValidationErrors;

        if (truncated)
        {
            results.RemoveRange(MaxValidationErrors, results.Count - MaxValidationErrors);
        }

        return results;
    }

    /// <summary>
    /// Returns the path of the first use of <c>[BindNever]</c> in the object graph, or null when
    /// there is none. DynamicHttp binds JSON bodies as a whole and cannot honor the MVC-only
    /// <c>[BindNever]</c> contract, so discovery rejects such models. The check covers the model
    /// type itself, nested complex types, collection elements, constructor/record positional
    /// parameters and declared polymorphic derived types.
    /// </summary>
    public static string? FindBindNeverProperty(Type bodyType)
    {
        foreach ((Type type, string path) in Traverse(bodyType))
        {
            string label = string.IsNullOrEmpty(path) ? type.Name : path;

            if (type.IsDefined(typeof(BindNeverAttribute), inherit: true))
            {
                return label;
            }

            foreach (ParameterInfo parameter in GetConstructorParameters(type))
            {
                if (parameter.IsDefined(typeof(BindNeverAttribute), inherit: true))
                {
                    return $"{label}(constructor parameter '{parameter.Name}')";
                }
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (property.IsDefined(typeof(BindNeverAttribute), inherit: true))
                {
                    return string.IsNullOrEmpty(path)
                        ? property.Name
                        : $"{path}.{property.Name}";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the path of the first validation attribute that is declared only on a constructor
    /// (or record positional) parameter, or null when every constructor attribute is mirrored on
    /// the matching property. DataAnnotations validates properties, not constructor parameters,
    /// so an attribute that exists only on the parameter would silently do nothing. Discovery
    /// rejects that placement instead of accepting a false sense of validation.
    /// </summary>
    public static string? FindUnsupportedConstructorValidation(Type bodyType)
    {
        foreach ((Type type, string path) in Traverse(bodyType))
        {
            string label = string.IsNullOrEmpty(path) ? type.Name : path;

            foreach (ParameterInfo parameter in GetConstructorParameters(type))
            {
                ValidationAttribute[] attributes = parameter
                    .GetCustomAttributes<ValidationAttribute>(inherit: true)
                    .ToArray();

                if (attributes.Length == 0)
                {
                    continue;
                }

                PropertyInfo? property = type.GetProperty(parameter.Name!,
                    BindingFlags.Public | BindingFlags.Instance);

                foreach (ValidationAttribute attribute in attributes)
                {
                    if (property is null ||
                        !property.GetCustomAttributes(attribute.GetType(), inherit: true).Any())
                    {
                        return $"{label}.{parameter.Name} ({attribute.GetType().Name})";
                    }
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
        bool hasDirectValidation =
            typeof(IValidatableObject).IsAssignableFrom(type) ||
            type.GetCustomAttributes<ValidationAttribute>(inherit: true).Any();

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
        foreach ((Type type, _) in Traverse(root))
        {
            // A polymorphic, abstract or interface input is deserialized to a runtime type that is
            // only known after binding. Validation must run against that runtime type, otherwise
            // annotations declared on a derived type are skipped. Any non-sealed reference type
            // can be replaced by a derived type (including via a custom JSON type resolver that
            // carries no attribute), so it always validates the runtime type as well.
            if (type.IsInterface || type.IsAbstract || !type.IsSealed || IsPolymorphic(type))
            {
                return true;
            }

            if (typeof(IValidatableObject).IsAssignableFrom(type))
            {
                return true;
            }

            // Type-level attributes (for example [CustomValidation] on the model class) are
            // executed by Validator.TryValidateObject when the plan is marked as directly
            // validated.
            if (type.GetCustomAttributes<ValidationAttribute>(inherit: true).Any())
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
            }
        }

        return false;
    }

    private static void ValidateNode(object value, string path, IServiceProvider services,
        List<ValidationResult> results, HashSet<object> visited)
    {
        if (results.Count >= MaxCollectedErrors)
        {
            return;
        }

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

        if (TryGetEnumerableElementType(type, out _))
        {
            // A body root (and any element reached through another collection) can be a
            // collection: List<T>, T[], List<List<T>>. The DataAnnotations metadata lives on the
            // elements, never on the collection wrapper, so enumerate instead of inspecting
            // properties. The visited set above still protects against self-referencing
            // collections.
            int index = 0;

            foreach (object? item in (IEnumerable)value)
            {
                if (results.Count >= MaxCollectedErrors)
                {
                    return;
                }

                if (item is not null)
                {
                    ValidateNode(item, $"{path}[{index}]", services, results, visited);
                }

                index++;
            }

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

                if (results.Count >= MaxCollectedErrors)
                {
                    return;
                }
            }
        }

        foreach (ValidationPropertyPlan property in plan.Properties)
        {
            if (results.Count >= MaxCollectedErrors)
            {
                return;
            }

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
                    if (results.Count >= MaxCollectedErrors)
                    {
                        return;
                    }

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

    /// <summary>
    /// Enumerates the types that can carry DataAnnotations metadata, following nested complex
    /// properties, collection elements and declared <c>[JsonDerivedType]</c> targets. The path is
    /// the object path used in error messages (for example <c>Child</c> or <c>Items[]</c>).
    /// </summary>
    private static IEnumerable<(Type Type, string Path)> Traverse(Type root)
    {
        Queue<(Type Type, string Path)> queue = new();
        HashSet<Type> visited = [];

        foreach (Type rootType in ValidationRoots(root))
        {
            queue.Enqueue((rootType, string.Empty));
        }

        while (queue.TryDequeue(out (Type Type, string Path) current))
        {
            Type type = Unwrap(current.Type);

            if (!visited.Add(type) || IsSimple(type))
            {
                continue;
            }

            yield return (type, current.Path);

            foreach (Type derived in GetJsonDerivedTypes(type))
            {
                queue.Enqueue((derived, current.Path));
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                Type propertyType = Unwrap(property.PropertyType);

                if (IsSimple(propertyType))
                {
                    continue;
                }

                string path = string.IsNullOrEmpty(current.Path)
                    ? property.Name
                    : $"{current.Path}.{property.Name}";

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
    }

    private static bool IsPolymorphic(Type type) =>
        type.IsDefined(typeof(JsonPolymorphicAttribute), inherit: false) ||
        type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Any();

    private static IEnumerable<Type> GetJsonDerivedTypes(Type type)
    {
        foreach (JsonDerivedTypeAttribute attribute in
            type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false))
        {
            yield return attribute.DerivedType;
        }
    }

    private static IEnumerable<ParameterInfo> GetConstructorParameters(Type type)
    {
        foreach (ConstructorInfo constructor in type.GetConstructors(
            BindingFlags.Public | BindingFlags.Instance))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                yield return parameter;
            }
        }
    }

    /// <summary>
    /// Expands a candidate body type to the types that can actually carry DataAnnotations
    /// metadata, unwrapping arbitrary nesting of collections. For a plain object this yields the
    /// type itself; for <c>List&lt;Foo&gt;</c> it yields <c>Foo</c>; for
    /// <c>List&lt;List&lt;Foo&gt;&gt;</c> it yields <c>Foo</c>. Simple types are skipped because
    /// they have no validation graph.
    /// </summary>
    private static IEnumerable<Type> ValidationRoots(Type type)
    {
        Queue<Type> queue = new();
        HashSet<Type> visited = [];
        queue.Enqueue(Unwrap(type));

        while (queue.TryDequeue(out Type? current))
        {
            current = Unwrap(current);

            if (current == typeof(string) || IsSimple(current))
            {
                continue;
            }

            if (TryGetEnumerableElementType(current, out Type elementType))
            {
                // Recurse instead of yielding the collection wrapper: List<List<Foo>> must
                // produce Foo, not List<Foo>.
                queue.Enqueue(elementType);

                continue;
            }

            if (visited.Add(current))
            {
                yield return current;
            }
        }
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private sealed record ObjectValidationPlan(Type Type,
        bool HasDirectValidation,
        IReadOnlyList<ValidationPropertyPlan> Properties);

    private sealed record ValidationPropertyPlan(PropertyInfo Property,
        bool IsEnumerable,
        Type? ChildType);
}
