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
    // Optional; when true, every non-safe method requires a valid antiforgery token.
    options.RequireAntiforgeryByDefault = false;
});
// Configure a default authentication scheme (for example JwtBearer) in a real app.
// AddAuthentication() without a scheme is not enough: authorized endpoints need a default.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();
builder.Services.AddAuthorization();
// Needed when an endpoint uses [DynamicAntiforgery] or RequireAntiforgeryByDefault is enabled.
builder.Services.AddAntiforgery();

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
- Framework binding attributes that DynamicHttp does not support (`Microsoft.AspNetCore.Mvc.From*`, `AsParameters`, `FromKeyedServices`) are rejected: use the DynamicHttp attributes instead, so a parameter cannot silently bind from the wrong source.
- An endpoint can have at most one body parameter.
- A `[FromRoute]` parameter must exist in the route template.
- Endpoint methods must be instance methods and cannot be generic; services cannot be open generic types.
- Two endpoints that normalize to the same route template and can overlap (for example `/x/{id}` and `/x/{name}`, optional/default variants, or overlapping constraints) are rejected. Disjoint constraints such as `{id:int}` and `{id:guid}` remain allowed.

Parameters are required unless they are nullable (`string?`, `int?`, `Dto?`) or have a C# default value. When nullable annotations are unavailable (`#nullable disable`), the parameter is treated as required. A missing required route, query or header value returns 400. `[FromBody]` requires `application/json`: an empty body returns 400, and a JSON `null` is accepted only for nullable parameters.

Enum values are accepted by name or by a declared numeric value. Undefined numeric values (for example `999` for an enum with three members) are rejected with 400. For `[Flags]` enums, combinations of declared non-negative flags are accepted; a negative sentinel such as `All = -1` is accepted only when it is the exact declared value, not as a wildcard for arbitrary bits.

## Validation

DataAnnotations validation is applied after binding:

- Attributes on a parameter, such as `[FromQuery, Range(1, 10)] int page`, are validated for every binding source.
- Body models are validated recursively, including complex properties and collection elements. Nested errors carry their object path (for example `Child.Price` or `Lines[0].Price`). Object cycles are handled without recursing forever.
- Validation metadata is discovered once at startup; the plan for a body type is built during discovery, and runtime-derived types are planned on first use and cached. Types with validation metadata anywhere in their reachable object graph are validated; types without any are not reflected over per request.
- Invalid input returns 400 ProblemDetails. Error messages never include the raw request value.
- Set `options.ValidateModels = false` to disable DataAnnotations validation for both parameters
  and body models (it defaults to `true`). Binding and type conversion still run.

DynamicHttp binds the whole JSON body, so the MVC-only `[BindNever]` attribute cannot be honored. Discovery rejects body models (including nested models and collection elements) and parameters decorated with `[BindNever]`; use a dedicated request DTO with `[JsonIgnore]` for properties that must not be bound. This is also the recommended protection against over-posting: never bind directly to persistence entities.

## Request body limit

Set `options.MaxRequestBodyBytes` to reject oversized payloads with `413 Payload Too Large`. The limit is enforced before antiforgery validation, so a large form body is rejected without being parsed. The limit applies to both `Content-Length` and chunked bodies. When unset, the server's own limit applies.

- A declared `Content-Length` above the limit is rejected immediately.
- When the server exposes a writable `IHttpMaxRequestBodySizeFeature`, the limit is lowered but never raised above an existing lower server limit.
- When the feature is read-only or absent and the body is chunked, DynamicHttp wraps the request stream and counts bytes as they are read, throwing `PayloadTooLargeHttpException` on overflow.

## Return values

Endpoints may return `T`, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or `IResult` (including `Task<IResult>` and `ValueTask<IResult>`). Values that are not `IResult` are serialized as JSON with status 200; a `null` result produces an empty 200 response.

## Authorization

`[DynamicAuthorize]` accepts `Policy`, `Roles` and `AuthenticationSchemes`. All non-empty values are applied to the same endpoint, so `Policy` and `Roles` are enforced together.

The framework `[Authorize]` and `[AllowAnonymous]` attributes are honored too and are translated into the same endpoint metadata. A service or method may mix both attribute families; every non-empty challenge is applied, so the result is fail-closed. A framework `[AllowAnonymous]` on a method overrides a class-level authorization requirement.

`[DynamicAllowAnonymous]` on a method overrides a class-level requirement (for example, a public endpoint inside an authorized service). Combining authorization and allow-anonymous on the same class or method is a startup configuration error rather than silently disabling authorization.

Authorization and allow-anonymous metadata declared on interfaces is honored as well, at both type and method level. This keeps a contract such as `interface IUserService { [DynamicAuthorize] Task<User> Get(); }` protected when implemented by a discovered service. The built-in framework `[Authorize]` cannot be placed on an interface type (its own `AttributeUsage` does not allow it), and a framework `[AllowAnonymous]` cannot be placed on an interface member either; use the DynamicHttp attributes there.

Set `options.RequireAuthorizationByDefault = true` to make authentication the default: every endpoint without `[DynamicAuthorize]`, framework `[Authorize]`, or an allow-anonymous marker requires an authenticated user. Mapping fails fast when authorization services are not registered (`AddAuthorization()` or `AddAuthorizationBuilder()`) and when `RequireAuthorizationByDefault` conflicts across `AddDynamicHttp` calls. The library cannot detect a missing `UseAuthorization()` in the pipeline: endpoint authorization metadata is only enforced when `UseAuthentication()`/`UseAuthorization()` are registered before `MapDynamicHttp()`.

### Antiforgery (CSRF)

Mark cookie-authenticated endpoints with `[DynamicAntiforgery]` (method or class). Register `AddAntiforgery()`; the library validates the token directly, so `app.UseAntiforgery()` is not required for these endpoints. Missing or invalid tokens return 400, and the check fails closed even when antiforgery services or middleware are absent.

- An explicit `[DynamicAntiforgery]` is always enforced, including on `GET` and custom verbs. The developer deliberately opted in, so the attribute is not filtered by HTTP method.
- Set `options.RequireAntiforgeryByDefault = true` to require a token on every endpoint whose method is not safe. `GET`, `HEAD`, `OPTIONS` and `TRACE` are treated as safe; all other methods, including custom verbs, require a token. The option defaults to `false`, can be merged across `AddDynamicHttp` calls, and conflicting values throw at startup.

## Exceptions

Register `UseDynamicHttpExceptionHandling()` in the pipeline; it converts exceptions into ProblemDetails responses:

- `IHttpException` / `HttpException` (for example `BadRequestHttpException`, `NotFoundHttpException`, `PayloadTooLargeHttpException`) maps to its `StatusCode` and is logged at Information with a stable message that does not contain exception messages or request values.
- Framework `BadHttpRequestException` responses (for example a server-level body limit) are returned as client errors without echoing their message.
- An unexpected exception returns a 500 ProblemDetails with a `traceId` and does not expose the exception message or stack trace. It is logged once at Error with the service/method context.
- A cancellation caused by the client disconnecting (`RequestAborted`) is not logged as an error.

Exception logs use the endpoint's route template (for example `/api/users/{id}`) instead of the concrete request path, so path segments cannot leak tokens or PII into logs. When no route template is available the path is used after sanitization. The HTTP method and trace identifier are sanitized as well; control characters, Unicode format characters (including bidi controls) and line/paragraph separators are replaced.

All log messages are written through structured logging parameters. DynamicHttp-controlled details returned to the client come from `HttpException.Detail`; that message is client-visible, so never put secrets, credentials or internal implementation data in it.

## Registration

`AddDynamicHttp` is idempotent: calling it more than once merges the scanned assemblies and registers each service once. Service descriptors created by the application (custom lifetime, factory or implementation) are left untouched. When no assembly is configured, `AddDynamicHttp` scans the calling assembly; `ScanCallingAssembly()` does the same explicitly.

## Sample

The `Sample/` app is a demo and is never packed or published. Its launch profile sets
`ASPNETCORE_ENVIRONMENT=Development`; the development authentication handler is still disabled
unless `DevAuth:Enabled=true` is passed. It only authenticates loopback requests: the remote IP must be loopback (a missing remote address fails closed) and the `Host` header must be `localhost`, `127.0.0.1` or `[::1]`. This prevents a local reverse proxy or DNS-rebinding to expose the dev identity provider. When disabled, the scheme is still the default one and challenges protected endpoints with 401:

```bash
dotnet run --project Sample -- --DevAuth:Enabled=true
curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5

# Optional: grant the Admin role (otherwise role-protected endpoints return 403).
dotnet run --project Sample -- --DevAuth:Enabled=true --DevAuth:GrantAdmin=true
```

`DevAuth:AllowRemote=true` disables both the loopback remote-IP check and the loopback `Host` check. It exists only for advanced local testing and must never be enabled outside development.

## Notes

This package intentionally uses native ASP.NET Core endpoint metadata so authorization, rate limiting, tags, response metadata and other framework features can compose with the generated endpoints.

Custom HTTP verbs can be added by deriving from `HttpMethodAttribute` and overriding `Method`.
Route conflicts are detected at startup, including optional/default parameters and overlapping
constraints (for example `{id:int}` vs `{id:min(1)}`), while clearly disjoint constraints such as
`{id:int}` and `{id:guid}` remain allowed.
