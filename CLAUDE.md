# CLAUDE.md

DynamicHttp is a single NuGet library (`net10.0`) providing attribute-driven ASP.NET Core Minimal API endpoints. The repo root contains the library; `tests/` and `Sample/` are separate projects.

## Commands

```bash
dotnet build --configuration Release
dotnet test --configuration Release

# One test class / one test
dotnet test tests/DynamicHttp.Tests.csproj --filter "FullyQualifiedName~IntegrationTests"
dotnet test tests/DynamicHttp.Tests.csproj --filter "FullyQualifiedName~Task_endpoint_returns_result"

# Sample app (dev auth requires the header)
dotnet run --project Sample
curl -H 'X-Dev-User: mario' http://localhost:5000/api/users/5

# Release artifact
dotnet pack --configuration Release --output ./artifacts
```

There is no separate lint/format task. CI runs `dotnet restore`, `dotnet build -c Release --no-restore`, then `dotnet test -c Release --no-build` on every push/PR.

## Build gotchas

- Put library source files **only in the repo root**. `DynamicHttp.csproj` explicitly removes `Sample/**/*.cs` and `tests/**/*.cs`; library code added under a subdirectory will silently not compile.
- `tests/DynamicHttp.Tests.Fixtures` is a separate assembly for deliberately invalid services. It is intentionally absent from `DynamicHttp.sln` but referenced by the test project. Because it sits inside `tests/`, `DynamicHttp.Tests.csproj` removes its `**` globs — keep those `Remove` lines.
- The root project enables nullable, implicit usings, XML docs, `TreatWarningsAsErrors`, and latest analyzers. Any analyzer/compiler warning breaks the library build. CS1591 is suppressed, so public members do not need XML comments.
- `.editorconfig`: 4 spaces, LF, UTF-8, System usings first. `var` is only a suggestion and is discouraged for built-in/apparent types.

## Architecture

- Startup flow: `AddDynamicHttp` scans configured assemblies (or the calling assembly if none) and registers discovered services; `MapDynamicHttp` lazily builds all endpoint definitions once through `DynamicHttpDiscovery` and expression-tree-compiled invokers. Do not add reflection to the request path.
- Routes combine the class `RoutePrefix` with the method template; a method may have exactly one HTTP verb attribute. Explicit `[FromRoute/Query/Header/Body/Services]` wins; otherwise simple types bind from query, complex types from body, and `CancellationToken` is injected. Reject multiple body parameters and `ref`/`out`/`in` parameters at discovery time.
- Return values: `IResult` is passed through; all other non-void values become `Results.Ok(value)`. `void`, `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` are handled specially in `Runtime.AwaitAsync` (boxed generic `ValueTask<T>` needs explicit handling).
- Authorization attributes are translated into native endpoint metadata. Class-level `[DynamicAuthorize]` plus method-level `[DynamicAllowAnonymous]` is valid; `[DynamicAuthorize]` and `[DynamicAllowAnonymous]` on the same scope, or method-level authorize with class-level anonymous, throw a configuration error.
- Controlled failures implement `IHttpException` (e.g. `BadRequestHttpException`, `NotFoundHttpException`) and should be thrown from endpoints. The endpoint logs only unexpected exceptions; `DynamicHttpExceptionHandler` logs controlled ones at Information and writes ProblemDetails. Do not log controlled exceptions in endpoint code — `LoggingTests` guards against double-logging.
- Test services defined in the main test assembly are scanned by every integration/logging test. Add services that intentionally fail discovery (bad routes, `ref` parameters) to the Fixtures assembly instead.

## Release

CI and CD are in `.github/workflows/`. CD triggers on a published GitHub release, reruns build/test, packs, and pushes to both GitHub Packages and NuGet. Bump `<Version>` in `DynamicHttp.csproj` and tag before publishing.
