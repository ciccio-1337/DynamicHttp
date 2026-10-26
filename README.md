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
    // Optional; when true, endpoints without authorization metadata require an authenticated user.
    options.RequireAuthorizationByDefault = false;
});
// Configure at least one authentication scheme (for example JwtBearer) in a real app.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
// Only needed when an endpoint uses [DynamicAntiforgery].
builder.Services.AddAntiforgery();

var app = builder.Build();

// Must be registered before authentication/authorization and before MapDynamicHttp.
app.UseDynamicHttpExceptionHandling();
app.UseAuthentication();
app.UseAuthorization();
// Must precede the endpoint mapping; only needed for [DynamicAntiforgery].
app.UseAntiforgery();
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
- MVC binding attributes (`Microsoft.AspNetCore.Mvc.From*`) are rejected: use the DynamicHttp attributes instead, so a parameter cannot silently bind from the wrong source.
- An endpoint can have at most one body parameter.
- A `[FromRoute]` parameter must exist in the route template.
- Endpoint methods must be instance methods and cannot be generic; services cannot be open generic types.
- Two endpoints that normalize to the same route template (for example `/x/{id}` and `/x/{name}`) are rejected.

Parameters are required unless they are nullable (`string?`, `int?`, `Dto?`) or have a C# default value. When nullable annotations are unavailable (`#nullable disable`), the parameter is treated as required. A missing required route, query or header value returns 400. `[FromBody]` requires `application/json`: an empty body returns 400, and a JSON `null` is accepted only for nullable parameters.

Enum values are accepted by name or by a declared numeric value. Undefined numeric values (for example `999` for an enum with three members) are rejected with 400.

## Validation

DataAnnotations validation is applied after binding:

- Attributes on a parameter, such as `[FromQuery, Range(1, 10)] int page`, are validated for every binding source.
- Body models are validated when the type declares validation attributes on its properties or implements `IValidatableObject`. Validation metadata is discovered once at startup; types without validation metadata are not reflected over per request.
- Invalid input returns 400 ProblemDetails. Error messages never include the raw request value.
- Set `options.ValidateModels = false` to disable validation (it defaults to `true`).

## Request body limit

Set `options.MaxRequestBodyBytes` to reject oversized payloads before binding with `413 Payload Too Large`. The limit applies to both `Content-Length` and chunked bodies. When unset, the server's own limit applies.

## Return values

Endpoints may return `T`, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or `IResult` (including `Task<IResult>` and `ValueTask<IResult>`). Values that are not `IResult` are serialized as JSON with status 200; a `null` result produces an empty 200 response.

## Authorization

`[DynamicAuthorize]` accepts `Policy`, `Roles` and `AuthenticationSchemes`. All non-empty values are applied to the same endpoint, so `Policy` and `Roles` are enforced together.

The framework `[Authorize]` and `[AllowAnonymous]` attributes are honored too and are translated into the same endpoint metadata. A service or method may mix both attribute families; every non-empty challenge is applied, so the result is fail-closed. A framework `[AllowAnonymous]` on a method overrides a class-level authorization requirement.

`[DynamicAllowAnonymous]` on a method overrides a class-level requirement (for example, a public endpoint inside an authorized service). Combining authorization and allow-anonymous on the same class or method is a startup configuration error rather than silently disabling authorization.

Set `options.RequireAuthorizationByDefault = true` to make authentication the default: every endpoint without `[DynamicAuthorize]`, framework `[Authorize]`, or an allow-anonymous marker requires an authenticated user. Mapping fails fast when authorization services are not registered (`AddAuthorization()` or `AddAuthorizationBuilder()`), and `UseAuthentication()`/`UseAuthorization()` must still be added to the pipeline.

### Antiforgery (CSRF)

Mark cookie-authenticated state-changing endpoints with `[DynamicAntiforgery]` (method or class). The library adds standard antiforgery endpoint metadata; register `AddAntiforgery()` and call `app.UseAntiforgery()` before mapping. The token is validated for `POST`/`PUT`/`PATCH`/`DELETE`; missing or invalid tokens return 400, and the check fails closed if the middleware is not in the pipeline.

## Exceptions

Register `UseDynamicHttpExceptionHandling()` in the pipeline; it converts exceptions into ProblemDetails responses:

- `IHttpException` / `HttpException` (for example `BadRequestHttpException`, `NotFoundHttpException`, `PayloadTooLargeHttpException`) maps to its `StatusCode` and is logged at Information with a stable message that does not contain exception messages or request values.
- Framework `BadHttpRequestException` responses (for example a server-level body limit) are returned as client errors without echoing their message.
- An unexpected exception returns a 500 ProblemDetails with a `traceId` and does not expose the exception message or stack trace. It is logged once at Error with the service/method context.
- A cancellation caused by the client disconnecting (`RequestAborted`) is not logged as an error.

## Registration

`AddDynamicHttp` is idempotent: calling it more than once merges the scanned assemblies and registers each service once. Service descriptors created by the application (custom lifetime, factory or implementation) are left untouched. When no assembly is configured, `AddDynamicHttp` scans the calling assembly; `ScanCallingAssembly()` does the same explicitly.

## Sample

The `Sample/` app is a demo and is never packed or published. Its development authentication handler is disabled by default; it is registered only when `ASPNETCORE_ENVIRONMENT=Development` **and** `DevAuth:Enabled=true`, and it only authenticates loopback requests:

```bash
dotnet run --project Sample -- --DevAuth:Enabled=true
curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5

# Optional: grant the Admin role (otherwise role-protected endpoints return 403).
dotnet run --project Sample -- --DevAuth:Enabled=true --DevAuth:GrantAdmin=true
```

`DevAuth:AllowRemote=true` exists only for advanced local testing and must never be enabled outside development.

## Notes

This package intentionally uses native ASP.NET Core endpoint metadata so authorization, rate limiting, tags, response metadata and other framework features can compose with the generated endpoints.
