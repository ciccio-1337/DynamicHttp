using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DynamicHttp;

public class DynamicHttpException : Exception
{
    public DynamicHttpException(string message) : base(message) { }
    public DynamicHttpException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class DynamicHttpConfigurationException : DynamicHttpException
{
    public DynamicHttpConfigurationException(string message) : base(message) { }
    public DynamicHttpConfigurationException(string message, Exception innerException) : base(message, innerException) { }
}

public interface IHttpException
{
    int StatusCode { get; }
    string Title { get; }
    string? Detail { get; }
    string? Type { get; }
}

/// <summary>
/// Base class for exceptions that map to a controlled ProblemDetails response.
/// </summary>
public abstract class HttpException : Exception, IHttpException
{
    protected HttpException(string detail) : base(detail) { }
    protected HttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public abstract int StatusCode { get; }
    public abstract string Title { get; }

    /// <summary>
    /// Client-visible detail returned in the ProblemDetails response. Never put secrets,
    /// credentials or internal implementation data in this message.
    /// </summary>
    public string? Detail => Message;

    public abstract string? Type { get; }
}

public class NotFoundHttpException : HttpException
{
    public NotFoundHttpException(string detail) : base(detail) { }
    public NotFoundHttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public override int StatusCode => StatusCodes.Status404NotFound;
    public override string Title => "Resource not found";
    public override string? Type => "https://httpstatuses.com/404";
}

public class BadRequestHttpException : HttpException
{
    public BadRequestHttpException(string detail) : base(detail) { }
    public BadRequestHttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public override int StatusCode => StatusCodes.Status400BadRequest;
    public override string Title => "Bad request";
    public override string? Type => "https://httpstatuses.com/400";
}

public class PayloadTooLargeHttpException : HttpException
{
    public PayloadTooLargeHttpException(string detail) : base(detail) { }
    public PayloadTooLargeHttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public override int StatusCode => StatusCodes.Status413PayloadTooLarge;
    public override string Title => "Payload too large";
    public override string? Type => "https://httpstatuses.com/413";
}

public sealed class DynamicHttpExceptionHandler(RequestDelegate next, ILogger<DynamicHttpExceptionHandler> logger)
{
    internal const string LoggedExceptionItemKey = "DynamicHttp.ExceptionLogged";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                // The client went away; there is nothing meaningful to respond or log as an error.
                throw;
            }

            IHttpException? typed = exception as IHttpException;
            int statusCode;
            string title;
            string? detail;
            string? type;

            // Prefer the route template over the concrete request path: paths can contain
            // tokens, PII or other attacker-controlled values. Method and trace id are
            // sanitized as well because they can be influenced by the client/proxy.
            string method = SanitizeForLog(context.Request.Method);
            string path = GetLogPath(context);
            string traceId = SanitizeForLog(context.TraceIdentifier);

            if (typed is not null)
            {
                statusCode = typed.StatusCode;
                title = typed.Title;
                detail = typed.Detail;
                type = typed.Type;

                // Log a stable message only: exception messages can contain attacker-controlled
                // values (log injection) or sensitive data copied from the request.
                logger.LogInformation(
                    "DynamicHttp request failed for {Method} {Path} with status code {StatusCode}",
                    method,
                    path,
                    statusCode);
            }
            else if (exception is BadHttpRequestException badRequest)
            {
                // Framework rejections (for example a body larger than the server limit) are
                // client errors. Never echo their message, which may contain internals.
                statusCode = badRequest.StatusCode;
                title = TitleFor(statusCode);
                detail = null;
                type = null;

                logger.LogInformation(
                    "DynamicHttp request failed for {Method} {Path} with status code {StatusCode}",
                    method,
                    path,
                    statusCode);
            }
            else
            {
                statusCode = StatusCodes.Status500InternalServerError;
                title = "An unexpected error occurred.";
                detail = null;
                type = null;

                if (!context.Items.ContainsKey(LoggedExceptionItemKey))
                {
                    // DynamicHttp endpoints log unexpected exceptions with service/method context;
                    // only log here when the exception did not come from a mapped endpoint.
                    logger.LogError(exception,
                        "Unhandled DynamicHttp exception for {Method} {Path}. TraceId: {TraceId}",
                        method,
                        path,
                        traceId);
                }
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            await Results.Problem(statusCode: statusCode,
                title: title,
                detail: detail,
                type: type,
                extensions: new Dictionary<string, object?>
                {
                    ["traceId"] = context.TraceIdentifier
                }).ExecuteAsync(context);
        }
    }

    internal static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return string.Create(value.Length, value, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                UnicodeCategory category = char.GetUnicodeCategory(source[i]);

                span[i] = char.IsControl(source[i]) ||
                    category is UnicodeCategory.Format or
                        UnicodeCategory.LineSeparator or
                        UnicodeCategory.ParagraphSeparator
                    ? '?'
                    : source[i];
            }
        });
    }

    private static string GetLogPath(HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } route })
        {
            return SanitizeForLog(route);
        }

        return SanitizeForLog(context.Request.Path.Value);
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad request",
        StatusCodes.Status413PayloadTooLarge => "Payload too large",
        _ => "Request rejected"
    };
}

public static class DynamicHttpExceptionApplicationExtensions
{
    public static IApplicationBuilder UseDynamicHttpExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<DynamicHttpExceptionHandler>();
}
