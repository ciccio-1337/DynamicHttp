using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void Body_limit_rejects_known_content_length()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 10;

        Assert.Throws<PayloadTooLargeHttpException>(() =>
            DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8));
    }

    [Fact]
    public void Body_limit_is_applied_to_chunked_requests()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        context.Request.ContentLength = null;

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);

        Assert.Equal(8, feature.MaxRequestBodySize);
    }

    [Fact]
    public void Body_limit_is_not_applied_to_a_read_only_feature()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);

        Assert.Null(feature.MaxRequestBodySize);
    }

    private sealed class TestMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; }
    }
}
