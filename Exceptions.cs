using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DynamicHttp;

public class DynamicHttpException(string message) : Exception(message);

public sealed class DynamicHttpConfigurationException(string message) : DynamicHttpException(message);

public interface IHttpException
{
    int StatusCode { get; }
    string Title { get; }
    string? Detail { get; }
    string? Type { get; }
}

public class NotFoundHttpException : Exception, IHttpException
{
    public NotFoundHttpException(string detail) : base(detail) { }
    public NotFoundHttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public int StatusCode => StatusCodes.Status404NotFound;
    public string Title => "Resource not found";
    public string? Detail => Message;
    public string? Type => "https://httpstatuses.com/404";
}

public class BadRequestHttpException : Exception, IHttpException
{
    public BadRequestHttpException(string detail) : base(detail) { }
    public BadRequestHttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public int StatusCode => StatusCodes.Status400BadRequest;
    public string Title => "Bad request";
    public string? Detail => Message;
    public string? Type => "https://httpstatuses.com/400";
}

public sealed class DynamicHttpExceptionHandler(RequestDelegate next, ILogger<DynamicHttpExceptionHandler> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            IHttpException? typed = exception as IHttpException;

            if (typed is not null)
            {
                logger.LogInformation(exception,
                    "DynamicHttp request failed for {Method} {Path} with status code {StatusCode}",
                    context.Request.Method,
                    context.Request.Path,
                    typed.StatusCode);
            }
            else
            {
                logger.LogError(exception,
                    "Unhandled DynamicHttp exception for {Method} {Path}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path,
                    context.TraceIdentifier);
            }

            if (context.Response.HasStarted)
            {
                throw;
            }

            await Results.Problem(statusCode: typed?.StatusCode ?? StatusCodes.Status500InternalServerError,
                title: typed?.Title ?? "An unexpected error occurred.",
                detail: typed?.Detail,
                type: typed?.Type,
                extensions: new Dictionary<string, object?>
                {
                    ["traceId"] = context.TraceIdentifier
                }).ExecuteAsync(context);
        }
    }
}

public static class DynamicHttpExceptionApplicationExtensions
{
    public static IApplicationBuilder UseDynamicHttpExceptionHandling(this IApplicationBuilder app) =>
        app.UseMiddleware<DynamicHttpExceptionHandler>();
}
