# DynamicHttp

Production-oriented attribute-driven HTTP endpoints for ASP.NET Core Minimal APIs.

## Design

DynamicHttp performs discovery and expression/delegate compilation once during startup. The request path uses the already-built endpoint metadata and compiled invokers.

It integrates with ASP.NET Core instead of replacing it:

- endpoint routing
- dependency injection
- authentication/authorization
- ProblemDetails
- endpoint metadata
- logging

## Example

```csharp
builder.Services.AddDynamicHttp(options =>
{
    options.ScanAssemblies(typeof(UserService).Assembly);
    // Optional; defaults to ServiceLifetime.Scoped.
    options.DefaultServiceLifetime = ServiceLifetime.Scoped;
});
// Configure at least one authentication scheme (for example JwtBearer) in a real app.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

// Must be registered before authentication/authorization and before MapDynamicHttp.
app.UseDynamicHttpExceptionHandling();
app.UseAuthentication();
app.UseAuthorization();
app.MapDynamicHttp();
app.Run();
```

```csharp
[HttpService("/api/users", Tags = ["Users"])]
[DynamicAuthorize(Policy = "users.read")]
public sealed class UserService
{
    [HttpGet("/{id}")]
    [ProducesResponseType(200, typeof(UserDto))]
    [ProducesResponseType(404)]
    public async Task<UserDto> Get(
        [FromRoute] int id,
        [FromServices] IUserRepository repository,
        CancellationToken cancellationToken)
    {
        return await repository.GetAsync(id, cancellationToken);
    }

    [HttpPost]
    [DynamicAuthorize(Roles = "Admin")]
    public Task<UserDto> Create(
        [FromBody] CreateUserRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserDto(1, request.Name));
    }

    [HttpGet("/public")]
    [DynamicAllowAnonymous]
    public string Public() => "hello";
}
```

## Binding

DynamicHttp uses the ASP.NET Core source attributes:

| Attribute | Source |
| --- | --- |
| `[FromRoute]` | Route values |
| `[FromQuery]` | Query string (default for simple types) |
| `[FromHeader]` | Request headers |
| `[FromBody]` | JSON body (default for complex types) |
| `[FromServices]` | Dependency injection |
| `CancellationToken` | `HttpContext.RequestAborted` |
| `HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal` | Current request |

Rules enforced at startup:

- Only one binding attribute is allowed per parameter.
- An endpoint can have at most one body parameter.
- A `[FromRoute]` parameter must exist in the route template.
- Endpoint methods must be instance methods and cannot be generic; services cannot be open generic types.
- Two endpoints that normalize to the same route template (for example `/x/{id}` and `/x/{name}`) are rejected.

Parameters are required unless they are nullable (`string?`, `int?`, `Dto?`) or have a C# default value. A missing required route, query or header value returns 400. `[FromBody]` requires `application/json`: an empty body returns 400, and a JSON `null` is accepted only for nullable parameters.

## Return values

Endpoints may return `T`, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or `IResult` (including `Task<IResult>` and `ValueTask<IResult>`). Values that are not `IResult` are serialized as JSON with status 200; a `null` result produces an empty 200 response.

## Authorization

`[DynamicAuthorize]` accepts `Policy`, `Roles` and `AuthenticationSchemes`. All non-empty values are applied to the same endpoint, so `Policy` and `Roles` are enforced together.

`[DynamicAllowAnonymous]` on a method overrides a class-level requirement (for example, a public endpoint inside an authorized service). Combining `[DynamicAuthorize]` and `[DynamicAllowAnonymous]` on the same class or method is a startup configuration error rather than silently disabling authorization.

## Exceptions

Register `UseDynamicHttpExceptionHandling()` in the pipeline; it converts exceptions into ProblemDetails responses:

- `IHttpException` / `HttpException` (for example `BadRequestHttpException`, `NotFoundHttpException`) maps to its `StatusCode` and is logged at Information.
- An unexpected exception returns a 500 ProblemDetails with a `traceId` and does not expose the exception message or stack trace. It is logged once at Error with the service/method context.
- A cancellation caused by the client disconnecting (`RequestAborted`) is not logged as an error.

## Registration

`AddDynamicHttp` is idempotent: calling it more than once merges the scanned assemblies and registers each service once. Service descriptors created by the application (custom lifetime, factory or implementation) are left untouched. When no assembly is configured, `AddDynamicHttp` scans the calling assembly; `ScanCallingAssembly()` does the same explicitly.

## Notes

This package intentionally uses native ASP.NET Core endpoint metadata so authorization, rate limiting, tags, response metadata and other framework features can compose with the generated endpoints.
