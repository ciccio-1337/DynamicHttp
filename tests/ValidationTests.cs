using System.ComponentModel.DataAnnotations;
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
}
