using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class ValidationTests
{
    [Fact]
    public void Nested_object_validation_reports_the_member_path()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new NestedRequest
        {
            Name = "order",
            Child = new NestedChild { Price = 0 }
        };

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("Child.Price", result.MemberNames);
    }

    [Fact]
    public void Collection_validation_reports_the_element_path()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new CollectionRequest
        {
            Lines = [new CollectionLine { Price = 0 }]
        };

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("Lines[0].Price", result.MemberNames);
    }

    [Fact]
    public void Object_cycles_are_validated_without_recursing_forever()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var node = new SelfReferencingModel { Value = 5 };
        node.Next = node;

        var results = DynamicHttpValidation.Validate(node, provider);

        Assert.Empty(results);
    }

    [Fact]
    public void Invalid_value_in_a_cycle_is_reported_once()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var node = new SelfReferencingModel { Value = 0 };
        node.Next = node;

        var results = DynamicHttpValidation.Validate(node, provider);

        var result = Assert.Single(results);
        Assert.Contains("Value", result.MemberNames);
    }

    [Fact]
    public void Recursive_validation_requirement_is_detected_at_the_root()
    {
        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(NestedRequest)));
        Assert.False(DynamicHttpValidation.RequiresObjectValidation(typeof(PlainRequest)));
    }

    [Fact]
    public void Root_collection_body_is_validated()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new List<CollectionLine> { new() { Price = 0 } };

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("[0].Price", result.MemberNames);
    }

    [Fact]
    public void Root_array_body_is_validated()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        CollectionLine[] request = [new CollectionLine { Price = 0 }];

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("[0].Price", result.MemberNames);
    }

    [Fact]
    public void Nested_root_collection_body_is_validated()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new List<List<CollectionLine>>
        {
            new() { new CollectionLine { Price = 0 } }
        };

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("[0][0].Price", result.MemberNames);
    }

    [Fact]
    public void Validation_requirement_is_detected_for_collection_roots()
    {
        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(List<CollectionLine>)));
        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(CollectionLine[])));
        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(List<List<CollectionLine>>)));
        Assert.False(DynamicHttpValidation.RequiresObjectValidation(typeof(List<int>)));
    }

    [Fact]
    public void Type_level_validation_attribute_is_enforced()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new TypeLevelModel { Name = "bad" };

        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(TypeLevelModel)));

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("type-level", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(nameof(TypeLevelModel.Name), result.MemberNames);
    }

    [Fact]
    public void Polymorphic_derived_validation_uses_the_runtime_type()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(Payment)));

        var results = DynamicHttpValidation.Validate(new CardPayment { Amount = 0 }, provider);

        var result = Assert.Single(results);
        Assert.Contains(nameof(CardPayment.Amount), result.MemberNames);
    }

    [Fact]
    public void Nested_polymorphic_property_uses_the_runtime_type()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = new PaymentRequest { Payment = new CardPayment { Amount = 0 } };

        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(PaymentRequest)));

        var results = DynamicHttpValidation.Validate(request, provider);

        var result = Assert.Single(results);
        Assert.Contains("Payment.Amount", result.MemberNames);
    }

    [Fact]
    public void Validation_results_are_capped()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var request = Enumerable.Range(0, 50)
            .Select(_ => new CollectionLine { Price = 0 })
            .ToList();

        var results = DynamicHttpValidation.Validate(request, provider);

        Assert.Equal(DynamicHttpValidation.MaxValidationErrors, results.Count);
    }

    [Fact]
    public void Non_sealed_runtime_types_always_revalidate_the_runtime_type()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        // No [JsonPolymorphic] metadata: a custom resolver can still produce a derived type, so an
        // unsealed declared type must not skip validation.
        Assert.True(DynamicHttpValidation.RequiresObjectValidation(typeof(NonSealedBase)));

        var results = DynamicHttpValidation.Validate(new NonSealedDerived { Value = 0 }, provider);

        var result = Assert.Single(results);
        Assert.Contains(nameof(NonSealedDerived.Value), result.MemberNames);
    }

    [Fact]
    public void Sealed_type_without_annotations_skips_validation()
    {
        Assert.False(DynamicHttpValidation.RequiresObjectValidation(typeof(PlainRequest)));
    }

    private sealed class NestedRequest
    {
        [Required]
        public string Name { get; set; } = string.Empty;

        public NestedChild Child { get; set; } = new();
    }

    private sealed class NestedChild
    {
        [Range(1, 100)]
        public int Price { get; set; }
    }

    private sealed class CollectionRequest
    {
        public List<CollectionLine> Lines { get; set; } = [];
    }

    private sealed class CollectionLine
    {
        [Range(1, 100)]
        public int Price { get; set; }
    }

    private sealed class SelfReferencingModel
    {
        [Range(1, 100)]
        public int Value { get; set; }

        public SelfReferencingModel? Next { get; set; }
    }

    private sealed class PlainRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    private class NonSealedBase
    {
    }

    private sealed class NonSealedDerived : NonSealedBase
    {
        [Range(1, 100)]
        public int Value { get; set; }
    }

    [CustomValidation(typeof(TypeLevelModelValidator), nameof(TypeLevelModelValidator.Validate))]
    private sealed class TypeLevelModel
    {
        public string Name { get; set; } = string.Empty;
    }

    public static class TypeLevelModelValidator
    {
        public static ValidationResult? Validate(object? value, ValidationContext context) =>
            value is TypeLevelModel { Name: "bad" }
                ? new ValidationResult("type-level validation failed",
                    [nameof(TypeLevelModel.Name)])
                : ValidationResult.Success;
    }

    [JsonPolymorphic]
    [JsonDerivedType(typeof(CardPayment), "card")]
    private abstract class Payment
    {
    }

    private sealed class CardPayment : Payment
    {
        [Range(1, 1000)]
        public decimal Amount { get; set; }
    }

    private sealed class PaymentRequest
    {
        public Payment Payment { get; set; } = new CardPayment();
    }
}
