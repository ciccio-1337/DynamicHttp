using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DynamicHttp.Tests;

public sealed class ExceptionHandlerTests
{
    [Fact]
    public async Task Bad_request_exception_maps_to_413_without_leaking_the_message()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        var body = new MemoryStream();
        context.Response.Body = body;

        var handler = new DynamicHttpExceptionHandler(
            _ => throw new BadHttpRequestException("secret detail", StatusCodes.Status413PayloadTooLarge),
            NullLogger<DynamicHttpExceptionHandler>.Instance);

        await handler.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);

        body.Position = 0;
        string responseBody = await new StreamReader(body).ReadToEndAsync();

        Assert.DoesNotContain("secret detail", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unmatched_request_path_is_not_logged()
    {
        var provider = new CapturingLoggerProvider();
        using ILoggerFactory loggerFactory = LoggerFactory.Create(
            builder => builder.AddProvider(provider));

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        context.Request.Method = "GET";
        context.Request.Path = "/password-reset/supersecret-token";
        context.Response.Body = new MemoryStream();

        var handler = new DynamicHttpExceptionHandler(
            _ => throw new InvalidOperationException("boom"),
            loggerFactory.CreateLogger<DynamicHttpExceptionHandler>());

        await handler.InvokeAsync(context);

        Assert.DoesNotContain(provider.Records,
            record => record.Message.Contains("supersecret-token", StringComparison.Ordinal));
        Assert.Contains(provider.Records,
            record => record.Message.Contains("(unmatched request)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/api/values/\r\nInjected", "/api/values/??Injected")]
    [InlineData("/api/values/\tTab", "/api/values/?Tab")]
    [InlineData("/api/values/\u2028Line", "/api/values/?Line")]
    [InlineData("/api/values/\u202EParagraph", "/api/values/?Paragraph")]
    public void Log_path_sanitizes_control_characters(string input, string expected)
    {
        Assert.Equal(expected, DynamicHttpExceptionHandler.SanitizeForLog(input));
    }
}
