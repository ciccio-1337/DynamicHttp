namespace DynamicHttp;

public sealed record ValidationError(string PropertyName, string ErrorMessage);

public sealed class ValidationResult(IReadOnlyList<ValidationError> errors)
{
    public IReadOnlyList<ValidationError> Errors { get; } = errors;
    public bool IsValid => Errors.Count == 0;
    public static ValidationResult Success { get; } = new([]);
}

public interface IDynamicHttpValidator
{
    ValueTask<ValidationResult> ValidateAsync(object instance, CancellationToken cancellationToken);
}

internal sealed class NoOpValidator : IDynamicHttpValidator
{
    public ValueTask<ValidationResult> ValidateAsync(object instance,
        CancellationToken cancellationToken) => ValueTask.FromResult(ValidationResult.Success);
}
