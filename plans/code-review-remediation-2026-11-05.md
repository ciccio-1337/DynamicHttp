# Remediation plan — DynamicHttp code review (2026-11-05)

> **Status: completed on 2026-11-05.**
> Release build without warnings; test suite: **131 green tests**. The final decisions and
> deviations relative to the plan are noted in the "Outcome" section at the bottom.
>
> Static review of the entire repository (clean working tree, no diff). Goal: close
> the issues found, adding regression tests and keeping the documented
> already-correct behaviors unchanged.

## Context

- Repository: `/home/fg/Repositories/DynamicHttp`
- Base: `main` @ `e6e6e7b` (clean working tree).
- Review: static analysis of all tracked code; build/test not run during the review.
- Acceptance criteria: `dotnet build --configuration Release` and
  `dotnet test --configuration Release` without warnings (the library project has
  `TreatWarningsAsErrors`).

## Behavior decisions

1. **`ValidateModels = false` disables all DataAnnotations validation** (parameters and
   models), as already stated by `README.md`. Conversion/binding is not disabled.
2. **Route conflict detection** compares the pattern precedence, ignoring optional/default
   and the content of the constraints, then checks whether the constraints can overlap. Plainly
   disjoint constraints (e.g. `int` vs `guid`) remain allowed.
3. **`HttpMethodAttribute` becomes truly extensible**: it exposes `Method`, so custom verbs
   work instead of throwing "Unsupported HTTP attribute".
4. **Repeated options**: `AddDynamicHttp` keeps merging the assemblies, but a second call that
   tries to change already-set scalar options fails with a clear configuration error.
   Options not yet set can be valued by the subsequent call.
5. **`DynamicHttpOptions` registered with a factory/type** is no longer ignored: `AddDynamicHttp`
   fails with a message inviting the user to configure the options in the callback.
6. **Sample**: the launch profile sets `ASPNETCORE_ENVIRONMENT=Development`; authentication
   always has a default scheme (DevAuth, dormant when disabled) so authorized endpoints
   respond 401 and not 500 when DevAuth is off.

## Medium

### M1 — Conflict detection for overlapping routes with the same precedence

- File: `Discovery.cs:421-432`, `Discovery.cs:439-487`
- Problem: the conflict key uses the full normalized text, so it does not detect
  `/x/{id}` vs `/x/{id?}`, `/x/{id}` vs `/x/{id=5}`, `/x/{id:int}` vs `/x/{id:min(1)}`.
  At runtime these pairs can produce `AmbiguousMatchException` (500).
- Change:
  - `NormalizeRoute` produces a shape key that ignores name, optional/default, and
    constraint content, but distinguishes plain, constrained, and catch-all parameters.
  - `ValidateConflicts` compares routes with the same key pairwise and verifies
    constraint compatibility (numeric, guid, bool, datetime, alpha, neutral, unknown).
  - Routes with different precedence (e.g. `{id}` vs `{id:int}`) are not conflicts.
- Test: theory with overlapping optional/default/constraint; `int` vs `guid` non-conflicting case.

### M2 — `ValidateModels = false` does not disable ValidationAttribute on parameters

- File: `Binding.cs:39-54`, `Configuration.cs:22-26`, `README.md:109-112`
- Problem: `ValidationAttribute`s on parameters are applied before the `!validateModels`
  check; the documentation says the flag disables validation.
- Change: return immediately from `Validate` when `validateModels` is false; update the
  XML comments and the documentation.
- Test: with `ValidateModels = false` an invalid `Range` parameter and an invalid `Required`
  model must pass (200); with the default they must still give 400.

## Low

### L1 — Sample command does not enable Development

- File: `Sample/Program.cs:13-14`, `README.md:151-158`, `CLAUDE.md:16`
- Change: add `Sample/Properties/launchSettings.json` with
  `ASPNETCORE_ENVIRONMENT=Development` and update the documentation.

### L2 — `AddAuthentication()` fallback without a default scheme

- File: `Sample/Program.cs:23-28`
- Change: always register the DevAuth scheme as default, with a `DevAuthOptions.Enabled`
  that makes the handler dormant (NoResult) when DevAuth is off. This way the challenge
  produces 401 instead of the "No authenticationScheme was specified" exception (500).

### L3 — Empty query/header on nullable types

- File: `Binding.cs:22-24`, `Binding.cs:123-138`
- Change: treat `""` as absence for nullable value types (`int?`, `Guid?`, ...), so
  the default applies if any, or `null` is returned.
- Test: `?page=` on `int?` returns `null`; a present value still binds.

### L4 — `async void` accepted

- File: `Discovery.cs:135-147`, `Invoker.cs:21-24`
- Change: reject at discovery `void` methods with `AsyncStateMachineAttribute`.
- Test: `ValidateEndpointMethod` throws `DynamicHttpConfigurationException`.

### L5 — Null route prefix/template

- File: `Discovery.cs:370-377`, `Attributes.cs:5`, `Attributes.cs:13`
- Change: `CombineRoutes` accepts `string?` and throws a clear configuration error.
- Test: null prefix and null template.

### L6 — `MapDynamicHttp` without registration / duplicate calls

- File: `Runtime.cs:36-49`
- Change:
  - `GetService<DynamicHttpRegistry>()` + `DynamicHttpConfigurationException` error if
    `AddDynamicHttp` is missing.
  - marker in `IEndpointRouteBuilder.Properties` to make the second call idempotent.
- Test: mapping without registration throws; double mapping does not duplicate the endpoints.

### L7 — Scalar options ignored in subsequent calls

- File: `Configuration.cs:69-88`
- Change: internal tracking of explicitly set properties; merge adopting the values not yet set
  and error on conflicts. `DefaultServiceLifetime` cannot change after the services are
  registered.
- Test: conflict on `MaxRequestBodyBytes`; adoption of an option set only in the second call;
  the same value repeated is allowed.

### L8 — `DynamicHttpOptions` registered with a factory not detected

- File: `Configuration.cs:69-79`
- Change: inspect all descriptors; if a non-instance descriptor exists, throw a
  configuration error.
- Test: factory registration + `AddDynamicHttp` throws.

### L9 — Other framework binding attributes ignored

- File: `Discovery.cs:16-24`, `Discovery.cs:162-174`
- Change: also reject `Microsoft.AspNetCore.Http.AsParametersAttribute` and
  `Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute`.
- Test: both attributes produce a configuration error.

### L10 — Log injection through `Request.Path`

- File: `Exceptions.cs:96-100`, `111-115`, `128-132`
- Change: `SanitizeForLog` helper that replaces control characters; use it for the path.
- Test: raw CR/LF become `?` in the logged value.

### L11 — Diagnostics lost on `ReflectionTypeLoadException`

- File: `Discovery.cs:26-39`
- Change: `FindServices`/`Build` accept an `ILogger?`; the registry passes the logger and the
  non-loadable types are logged.
- Test: indirect coverage through the existing build; no dedicated test for an exception that is
  not easily reproducible.

### L12 — `HttpMethodAttribute` public but not extensible

- File: `Discovery.cs:379-389`, `Attributes.cs:12-17`
- Change: add `abstract string Method` to the base, implement it in the built-in verbs, and
  use `attribute.Method` in `GetVerb` (with non-empty validation and uppercase normalization).
- Test: custom `PROPFIND` attribute discovered and mapped.

### L13 — Route parsing errors not wrapped

- File: `Discovery.cs:396`, `Discovery.cs:441`
- Change: `ParseRoutePattern` helper that converts `RoutePatternException` into
  `DynamicHttpConfigurationException`.
- Test: malformed route.

### L14 — Release workflow

- File: `.github/workflows/cd.yml:23-37`
- Change: use `secrets.NUGET_USER` for `NuGet/login` (document the secret) and add
  `--username` to the push to GitHub Packages.
- Test: manual/YAML check; not executable in the tests.

### L15 — `artifacts/` not ignored

- File: `.gitignore`
- Change: add `artifacts/`.

## Nit

- De-duplicate `ProducesResponseType` between class and method (`Discovery.cs:90-92`).
- Fix the "already-built registry" comment in `Configuration.cs:82`.
- Add a constructor with `innerException` to `DynamicHttpException` (`Exceptions.cs:7`).
- Update the README auth snippet with a default scheme.

## Tests to add

- Route conflict: optional, default, overlapping constraints; disjoint constraints.
- `ValidateModels = false` on parameter and model.
- `?page=` on nullable; empty header.
- `async void`.
- Null route prefix/template and malformed route.
- `MapDynamicHttp` without registration and double mapping.
- Repeated options (conflict, adoption, same value) and options via factory.
- `AsParameters` / `FromKeyedServices`.
- Path sanitization.
- Custom verb.
- Duplicate `ProducesResponseType`.
- Chunked body limit through `IHttpMaxRequestBodySizeFeature` (internal helper).
- `BadHttpRequestException` 413 in the middleware.
- Antiforgery without `UseAntiforgery` and with a valid token.
- HEAD/OPTIONS, `NotFoundHttpException` 404, unregistered `[FromServices]`.
- Raw null result and `ValueTask<T>` null.
- Valid/invalid `[Flags]` enum.
- Multiple `[DynamicAuthorize]`.
- Framework `[AllowAnonymous]` at class level.

## Final verification

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

Also check `git status` to make sure no build artifacts or
`TestResults/` are committed.

## Outcome

All plan items implemented (M1-M2, L1-L15, nit) with the related regression tests.

- `dotnet build --configuration Release`: 0 warnings, 0 errors.
- `dotnet test --configuration Release`: **131 tests passed**, 0 failed.
- New test files: `tests/RuntimeTests.cs` (chunked/read body limit), `tests/ExceptionHandlerTests.cs`
  (413 without leaks, path sanitization). Additions to `ConfigurationTests`, `DiscoveryTests`,
  `IntegrationTests`, `AuthorizationTests`.
- Antiforgery: validation is performed directly through `IAntiforgery` in the runtime.
  When an endpoint exposes antiforgery metadata but `UseAntiforgery()` is missing, the framework
  throws `InvalidOperationException` (500) before executing the endpoint. Direct validation
  therefore makes the fail-closed effective even without middleware and returns 400 in both cases.
  Documentation (`README.md`, `CLAUDE.md`, XML doc) updated accordingly.
- `HttpMethodAttribute` exposes an abstract `Method`: the built-in verbs implement it and custom verbs
  are normalized to uppercase. This is a potentially breaking change only for pre-existing external
  subclasses, which previously had no supported way to work.
- Route conflict detection uses a precedence key (plain parameter,
  constrained, catch-all) and compares the constraints: optional/default and overlapping constraints are
  conflicts, `int` vs `guid` are not.
- Repeated `AddDynamicHttp`: assemblies are summed; already-set scalar options cannot
  change value (configuration error), while never-set ones are adopted.
  Any non-instance registration of `DynamicHttpOptions` is rejected.
- Sample: `launchSettings.json` sets `ASPNETCORE_ENVIRONMENT=Development`; DevAuth is always the
  default scheme and, when disabled, returns `NoResult` (401 instead of 500).
- CI/CD: `NuGet/login` uses `secrets.NUGET_USER`; the push to GitHub Packages passes `--username`.
  `artifacts/` is ignored by git.

No build artifacts or `TestResults/` are tracked; the changes remained in the working
tree (uncommitted) as requested.
