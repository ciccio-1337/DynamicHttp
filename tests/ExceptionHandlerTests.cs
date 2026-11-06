using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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

    [Theory]
    [InlineData("/api/values/\r\nInjected", "/api/values/??Injected")]
    [InlineData("/api/values/\tTab", "/api/values/?Tab")]
    public void Log_path_sanitizes_control_characters(string input, string expected)
    {
        Assert.Equal(expected, DynamicHttpExceptionHandler.SanitizeForLog(input));
    }
}
