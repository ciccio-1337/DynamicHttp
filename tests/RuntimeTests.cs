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

    [Fact]
    public async Task Body_limit_wraps_a_read_only_chunked_body()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        context.Request.ContentLength = null;
        context.Request.Body = new MemoryStream(new byte[20]);

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);

        await Assert.ThrowsAsync<PayloadTooLargeHttpException>(async () =>
        {
            int read = await context.Request.Body.ReadAsync(new byte[20]);
            Assert.True(read > 0);
        });
    }

    [Fact]
    public async Task Wrapped_body_reads_values_within_the_limit()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        context.Request.ContentLength = null;
        context.Request.Body = new MemoryStream(new byte[8]);

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);

        int read = await context.Request.Body.ReadAsync(new byte[16]);

        Assert.Equal(8, read);
    }

    [Fact]
    public void Body_limit_does_not_raise_a_lower_server_limit()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { MaxRequestBodySize = 4 };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);

        Assert.Equal(4, feature.MaxRequestBodySize);
    }

    [Fact]
    public void Body_limit_accepts_content_length_equal_to_the_limit()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 8;

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8);
    }

    [Fact]
    public void Body_limit_rejects_content_length_one_over_the_limit()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 9;

        Assert.Throws<PayloadTooLargeHttpException>(() =>
            DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, 8));
    }

    [Fact]
    public async Task Body_limit_at_long_max_value_does_not_truncate()
    {
        var context = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true };
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        context.Request.ContentLength = null;
        context.Request.Body = new MemoryStream("hello"u8.ToArray());

        DynamicHttpEndpointRouteBuilderExtensions.EnforceRequestBodyLimit(context, long.MaxValue);

        byte[] buffer = new byte[16];
        int read = await context.Request.Body.ReadAsync(buffer);

        Assert.Equal(5, read);
        Assert.Equal("hello"u8.ToArray(), buffer[..read]);
    }

    [Fact]
    public void Client_disconnect_predicate_matches_expected_failures()
    {
        using var aborted = new CancellationTokenSource();
        using var notAborted = new CancellationTokenSource();
        aborted.Cancel();

        Assert.True(DynamicHttpEndpointRouteBuilderExtensions.IsClientDisconnect(
            new OperationCanceledException(), aborted.Token));
        Assert.True(DynamicHttpEndpointRouteBuilderExtensions.IsClientDisconnect(
            new IOException(), aborted.Token));
        Assert.False(DynamicHttpEndpointRouteBuilderExtensions.IsClientDisconnect(
            new IOException(), notAborted.Token));
        Assert.False(DynamicHttpEndpointRouteBuilderExtensions.IsClientDisconnect(
            new InvalidOperationException(), aborted.Token));
    }

    private sealed class TestMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; init; }

        public long? MaxRequestBodySize { get; set; }
    }
}
