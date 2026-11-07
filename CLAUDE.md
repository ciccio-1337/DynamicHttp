# CLAUDE.md

DynamicHttp is a single NuGet library (`net10.0`) providing attribute-driven ASP.NET Core Minimal API endpoints. The repo root contains the library; `tests/` and `Sample/` are separate projects.

## Commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release

# One test class / one test
dotnet test tests/DynamicHttp.Tests.csproj --filter "FullyQualifiedName~IntegrationTests"
dotnet test tests/DynamicHttp.Tests.csproj --filter "FullyQualifiedName~Task_endpoint_returns_result"

# Sample app (dev auth is opt-in; requires a loopback remote IP and loopback Host header;
# the launch profile sets Development):
dotnet run --project Sample -- --DevAuth:Enabled=true
curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5
# Add --DevAuth:GrantAdmin=true to receive the Admin role.

# Release artifact
dotnet pack --configuration Release --output ./artifacts
```

There is no separate lint/format task. CI runs `dotnet restore`, `dotnet build -c Release --no-restore`, then `dotnet test -c Release --no-build` on every push/PR.

## Build gotchas

- Put library source files **only in the repo root**. `DynamicHttp.csproj` explicitly removes `Sample/**/*.cs` and `tests/**/*.cs`; library code added under a subdirectory will silently not compile.
- `tests/DynamicHttp.Tests.Fixtures` is a separate assembly for deliberately invalid services. It is intentionally absent from `DynamicHttp.sln` but referenced by the test project. Because it sits inside `tests/`, `DynamicHttp.Tests.csproj` removes its `**` globs — keep those `Remove` lines.
- The root project enables nullable, implicit usings, XML docs, `TreatWarningsAsErrors`, and latest analyzers. Any analyzer/compiler warning breaks the library build. CS1591 is suppressed, so public members do not need XML comments.
- `.editorconfig`: 4 spaces, LF, UTF-8, no trailing whitespace, System usings first. `var` is only a suggestion and is discouraged for built-in/apparent types.

## Architecture

- Startup flow: `AddDynamicHttp` scans configured assemblies (or the calling assembly if none) and registers discovered services through `TryAdd` (repeated calls merge assemblies and preserve application registrations); `MapDynamicHttp` lazily builds all endpoint definitions once through `DynamicHttpDiscovery` and expression-tree-compiled invokers. Do not add reflection to the request path; `Awaiting.cs` caches compiled `Task<T>.Result` / `ValueTask<T>.AsTask` accessors per concrete runtime type.
- Routes combine the class `RoutePrefix` with the method template; a method may have exactly one HTTP verb attribute. Explicit `[FromRoute/Query/Header/Body/Services]` wins; otherwise simple types bind from query, complex types from body, and `CancellationToken`, `HttpContext`, `HttpRequest`, `HttpResponse` and `ClaimsPrincipal` are injected. Reject MVC `Microsoft.AspNetCore.Mvc.From*` and other framework binding attributes (`AsParameters`, `FromKeyedServices`), multiple binding/body attributes, `ref`/`out`/`in` parameters, static/generic/`async void` endpoint methods, open generic services, and route parameters that are absent from the template at discovery time. Route conflicts are detected after normalizing parameter names and precedence (for example `/x/{id}` vs `/x/{name}`), including optional/default variants and overlapping constraints, while disjoint constraints such as `int` vs `guid` remain allowed. Custom verbs work by deriving from `HttpMethodAttribute` and overriding `Method`.
- Binding metadata includes C# nullability (`ParameterDefinition.IsNullable`): missing route/query/header values and JSON `null` bodies are 400 for non-nullable parameters, while nullable parameters and C# default values are honored. `NullabilityState.Unknown` (nullable-oblivious assemblies) is treated as required. `[FromBody]` requires JSON content; empty bodies, malformed JSON and unsupported body types are 400. Undefined numeric enum values are rejected. For `[Flags]` enums, combinations of declared non-negative bits are accepted, but a negative sentinel such as `All = -1` is accepted only as its exact declared value (never as a wildcard). `Body`/conversion helpers live in `Binding.cs`, awaiting helpers in `Awaiting.cs`.
- After binding, `DynamicHttpBinding.Validate` applies DataAnnotations when `DynamicHttpOptions.ValidateModels` is true (default): parameter attributes and, for body models, recursive object validation. `Validation.cs` builds and caches an `ObjectValidationPlan` per type at startup (including nested complex types and collection elements), detects `IValidatableObject`/`ValidationAttribute` anywhere reachable, validates the object graph with cycle protection, and reports member paths such as `Child.Price` or `Lines[0].Price`. When the flag is false all DataAnnotations validation is skipped; binding/conversion still run. Validation errors never echo raw values.
- DynamicHttp binds the whole JSON body, so MVC's `[BindNever]` is not honored. Discovery rejects `[BindNever]` on parameters and anywhere in a body model graph (including nested models and collection elements) with a configuration error pointing at `[JsonIgnore]`/dedicated DTOs.
- `DynamicHttpOptions.MaxRequestBodyBytes` is an optional per-endpoint limit enforced in `Runtime.cs` before antiforgery and binding: `ContentLength` is checked explicitly (413), a writable `IHttpMaxRequestBodySizeFeature` is lowered (never raised above a lower server limit), and when that feature is absent/read-only a chunked body is wrapped in a counting stream that throws `PayloadTooLargeHttpException` on overflow.
- Return values: `IResult` is passed through; all other non-null values become `Results.Ok(value)`. `Task`, `Task<T>` (including subclasses), `ValueTask`, and boxed `ValueTask<T>` are handled in `DynamicHttpAwaiting.AwaitAsync`; a `null` result produces an empty 200.
- Authorization attributes are translated into native endpoint metadata. Every non-empty `Policy`/`Roles`/`AuthenticationSchemes` property is applied on the same `AuthorizeAttribute`. Both `[DynamicAuthorize]` and framework `IAuthorizeData` (`[Authorize]`) are collected from the class/method and from implemented interfaces (type-level via `GetInterfaces()`, method-level via `GetInterfaceMap`); framework `[AllowAnonymous]` and `[DynamicAllowAnonymous]` are honored the same way. Class-level `[DynamicAuthorize]` plus method-level `[DynamicAllowAnonymous]` is valid; authorization and allow-anonymous on the same scope, or method-level authorize with class-level anonymous, throw a configuration error. `RequireAuthorizationByDefault` adds a bare `RequireAuthorization()` to unannotated, non-anonymous endpoints and fails fast if authorization services are absent; it cannot detect a missing `UseAuthorization()`.
- `[DynamicAntiforgery]` validates the token directly through `IAntiforgery`, so it fails closed with a 400 whether or not `UseAntiforgery()` is in the pipeline. An explicit attribute is always enforced, including on `GET` and custom verbs. `RequireAntiforgeryByDefault` (default `false`, merge/conflict-checked like other options) requires a token on every method that is not `GET`/`HEAD`/`OPTIONS`/`TRACE`. Body-limit enforcement runs before antiforgery so an oversized form is rejected without parsing.
- Controlled failures implement `IHttpException` (base class `HttpException`, e.g. `BadRequestHttpException`, `NotFoundHttpException`, `PayloadTooLargeHttpException`) and should be thrown from endpoints. `HttpException.Detail` is client-visible, so it must not contain secrets. The endpoint logs unexpected exceptions once with service/method context and marks `HttpContext.Items["DynamicHttp.ExceptionLogged"]`; `DynamicHttpExceptionHandler` logs controlled ones at Information with a stable message (no exception message or request values), turns `BadHttpRequestException` into a client error without leaking its message, skips a duplicate Error log for marked exceptions, and writes ProblemDetails. Logs use the route template (`RouteEndpoint.RoutePattern.RawText`) rather than the concrete path when available, and sanitize method/trace id and control/Unicode format/line/paragraph separator characters. Client disconnects (`RequestAborted`) are never logged as errors.
- Test services defined in the main test assembly are scanned by every integration/logging test. Add services that intentionally fail discovery (bad routes, `ref` parameters) to the Fixtures assembly instead; discovery validators that are exercised directly live in `DiscoveryTests`.

## Release

CI and CD are in `.github/workflows/`. Actions are pinned to full commit SHAs; CI declares `permissions: contents: read`. CD triggers on a published GitHub release, runs in the protected `release` environment (configure required reviewers in repository settings), reruns build/test, packs, and pushes to both GitHub Packages and NuGet. Bump `<Version>` in `DynamicHttp.csproj` and tag before publishing. The `NuGet/login` action uses the GitHub repository owner as the nuget.org username (they are the same account for this repository).
