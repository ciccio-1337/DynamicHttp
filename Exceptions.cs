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

/// <summary>
/// Base class for exceptions that map to a controlled ProblemDetails response.
/// </summary>
public abstract class HttpException : Exception, IHttpException
{
    protected HttpException(string detail) : base(detail) { }
    protected HttpException(string detail, Exception innerException) : base(detail, innerException) { }

    public abstract int StatusCode { get; }
    public abstract string Title { get; }
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

            if (typed is not null)
            {
                logger.LogInformation(exception,
                    "DynamicHttp request failed for {Method} {Path} with status code {StatusCode}",
                    context.Request.Method,
                    context.Request.Path,
                    typed.StatusCode);
            }
            else if (!context.Items.ContainsKey(LoggedExceptionItemKey))
            {
                // DynamicHttp endpoints log unexpected exceptions with service/method context;
                // only log here when the exception did not come from a mapped endpoint.
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
