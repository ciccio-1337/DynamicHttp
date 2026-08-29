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

public class NotFoundHttpException(string detail) : Exception(detail), IHttpException
{
    public int StatusCode => StatusCodes.Status404NotFound;
    public string Title => "Resource not found";
    public string? Detail => Message;
    public string? Type => "https://httpstatuses.com/404";
}

public class BadRequestHttpException(string detail) : Exception(detail), IHttpException
{
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
            logger.LogError(exception,
                "Unhandled DynamicHttp exception for {Method} {Path}. TraceId: {TraceId}",
                context.Request.Method,
                context.Request.Path,
                context.TraceIdentifier);

            if (context.Response.HasStarted)
            {
                throw;
            }

            IHttpException? typed = exception as IHttpException;

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
