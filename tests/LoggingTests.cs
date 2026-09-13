using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DynamicHttp.Tests;

internal sealed record LogRecord(LogLevel Level, string Message);

public sealed class LoggingTests
{
    [Fact]
    public async Task Controlled_http_exceptions_are_not_logged_as_errors()
    {
        var provider = new CapturingLoggerProvider();
        await using WebApplication app = await StartAppAsync(provider);
        var client = app.GetTestClient();

        // Missing required query parameter -> controlled BadRequestHttpException (400).
        var response = await client.GetAsync("/api/values/query");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The exception handler is the only component that should log a controlled error, and it
        // does so at Information. The endpoint must not double-log it at Error level.
        Assert.DoesNotContain(provider.Records, r => r.Level == LogLevel.Error);

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Information && r.Message.Contains("400", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unexpected_exceptions_are_still_logged_as_errors()
    {
        var provider = new CapturingLoggerProvider();
        await using WebApplication app = await StartAppAsync(provider);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/explode");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        Assert.Contains(provider.Records,
            r => r.Level == LogLevel.Error &&
                 r.Message.Contains(nameof(ExplodingService), StringComparison.Ordinal));
    }

    private static async Task<WebApplication> StartAppAsync(CapturingLoggerProvider provider)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.AddProvider(provider);
        builder.Services.AddDynamicHttp(options =>
            options.ScanAssemblies(typeof(ValuesService).Assembly));

        WebApplication app = builder.Build();
        app.UseDynamicHttpExceptionHandling();
        app.MapDynamicHttp();

        await app.StartAsync();
        return app;
    }
}

[HttpService("/api/explode")]
public sealed class ExplodingService
{
    [HttpGet]
    public string Explode() => throw new InvalidOperationException("boom");
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogRecord> _records = [];

    public IReadOnlyList<LogRecord> Records
    {
        get { lock (_records) { return _records.ToArray(); } }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (provider._records)
            {
                provider._records.Add(new LogRecord(logLevel, formatter(state, exception)));
            }
        }
    }
}