using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DynamicHttp;

internal static class DynamicHttpAwaiting
{
    // The request path must not perform reflection lookups. Accessors are compiled once per
    // concrete task/value-task type and cached for the lifetime of the process.
    private static readonly ConcurrentDictionary<Type, Func<object, Task>> ValueTaskAsTaskAccessors = new();
    private static readonly ConcurrentDictionary<Type, Func<Task, object?>> TaskResultAccessors = new();

    public static async ValueTask<object?> AwaitAsync(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case Task task:
                await task.ConfigureAwait(false);

                return GetTaskResult(task);
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);

                return null;
        }

        Type valueType = value.GetType();

        if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            // A boxed ValueTask<T> is never `is ValueTask`, so the generic case must be handled
            // explicitly by converting it to a Task and reading the result.
            Func<object, Task> asTask = ValueTaskAsTaskAccessors.GetOrAdd(
                valueType, static type => CreateValueTaskAsTaskAccessor(type));
            Task task = asTask(value);

            await task.ConfigureAwait(false);

            return GetTaskResult(task);
        }

        return value;
    }

    private static Func<object, Task> CreateValueTaskAsTaskAccessor(Type valueTaskType)
    {
        ParameterExpression value = Expression.Parameter(typeof(object), "value");
        MethodInfo asTask = valueTaskType.GetMethod(nameof(ValueTask<int>.AsTask))!;

        return Expression.Lambda<Func<object, Task>>(
            Expression.Call(Expression.Convert(value, valueTaskType), asTask), value).Compile();
    }

    private static object? GetTaskResult(Task task)
    {
        Func<Task, object?> accessor = TaskResultAccessors.GetOrAdd(
            task.GetType(), static type => CreateTaskResultAccessor(type));

        return accessor(task);
    }

    private static Func<Task, object?> CreateTaskResultAccessor(Type taskType)
    {
        // Walk the hierarchy to Task<T> instead of looking for a property named "Result" on the
        // runtime type: a custom non-generic Task subclass may expose an unrelated Result
        // property, which must not be mistaken for the task's value.
        Type? genericTask = taskType;

        while (genericTask is not null &&
            !(genericTask.IsGenericType &&
              genericTask.GetGenericTypeDefinition() == typeof(Task<>)))
        {
            genericTask = genericTask.BaseType;
        }

        PropertyInfo? result = genericTask?.GetProperty(nameof(Task<int>.Result));

        if (result is null)
        {
            return static _ => null;
        }

        // Non-generic Task and async Task methods complete as the internal
        // Task<VoidTaskResult>; that marker must not leak into the HTTP response as a value.
        if (genericTask!.GetGenericArguments()[0].FullName == "System.Threading.Tasks.VoidTaskResult")
        {
            return static _ => null;
        }

        ParameterExpression task = Expression.Parameter(typeof(Task), "task");
        // Cast to the declaring type (Task<T>) rather than the concrete runtime type: async
        // methods may return internal Task<T> subclasses that are not accessible from here.
        Expression typedTask = Expression.Convert(task, result.DeclaringType!);

        return Expression.Lambda<Func<Task, object?>>(
            Expression.Convert(Expression.Property(typedTask, result), typeof(object)), task).Compile();
    }
}
