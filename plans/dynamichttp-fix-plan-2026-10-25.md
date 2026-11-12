# Remediation plan — DynamicHttp code review (2026-10-25)

> **Status: completed on 2026-10-25.**
> Release build without warnings; test suite: **73 green tests**. Deviations/decisions
> relative to the plan are noted in the "Outcome" section at the bottom.

## Context

- Repository: `/home/fg/Repositories/DynamicHttp`
- Base: `main` @ `95e07ca`, clean working tree (no diff/staged changes, no unpushed commits).
- Type of analysis: static review of the repository code (the requested diff was empty).
- Build/test not run during the review, to avoid generating artifacts.

Plan goal: close the issues found in order of risk, adding the related regression
tests. Each phase is independent and can be released separately.

Global acceptance criteria for each phase:

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

They must pass without warnings (`TreatWarningsAsErrors` is enabled on the library project).

---

## Phase 1 — P0: authorization (bypass risk)

### 1.1 Do not lose `Roles` / `AuthenticationSchemes` when `Policy` is present

- File: `Runtime.cs:96-117` (branches at lines 98-101, 102-109, 110-116)
- Problem: the three branches are mutually exclusive. With `[DynamicAuthorize(Policy = "...", Roles = "...")]`
  the `Policy` branch creates an `AuthorizeAttribute` with only the policy: **`Roles` is ignored**
  (a user authenticated by the policy but without the role ⇒ access granted). Same loss for
  `AuthenticationSchemes` together with `Policy`.
- Change: build a single `AuthorizeAttribute` with all non-empty properties, or
  validate at discovery that `Policy` is exclusive with respect to `Roles`/`AuthenticationSchemes`
  (combination is preferable, consistent with the ASP.NET Core APIs).
- Test: integration with policy + role and with an explicit scheme; verify 401/403 and the positive case.
- Criterion: an attribute with multiple properties applies them all; no property is silently dropped.

### 1.2 End-to-end authorization tests

- File: `tests/IntegrationTests.cs` (new section) or new `tests/AuthorizationTests.cs`
- Minimum cases:
  - policy satisfied / not satisfied (200 / 403)
  - `Roles` required and missing (403)
  - explicit `AuthenticationSchemes`
  - method-level `[DynamicAllowAnonymous]` on an authorized class (200 anonymous)
  - class `[DynamicAuthorize]` + method `[DynamicAllowAnonymous]`: documented behavior
- Criterion: these tests fail with the current code (regression for point 1.1).

---

## Phase 2 — P1: binding and conversion correctness

### 2.1 `[FromBody]` must accept JSON `null` for nullable parameters

- File: `Runtime.cs:159-181` (check at lines 170-173)
- Problem: `ReadFromJsonAsync` returns `null` both for an empty body and for JSON `null`; the code
  responds 400 in both cases. `[FromBody] int? x` or `[FromBody] Dto? x` can never receive
  `null`, and the "non-empty request body" message is misleading.
- Change: distinguish a missing/empty body (e.g. `ContentLength == 0`, empty stream) from a deserialized
  null; return `null` when the type is nullable.
- Test: `null` body with `int?`/`Dto?` ⇒ 200; empty body ⇒ 400; malformed JSON ⇒ 400.

### 2.2 Overflow in conversions ⇒ 400, not 500

- File: `Runtime.cs:211-221` (enum) and `Runtime.cs:250-280` (TypeConverter)
- Problem: `Enum.Parse` throws `OverflowException` for out-of-range numbers (e.g. `?color=999...`);
  the catch handles only `ArgumentException`. The `TypeConverter` fallback does not catch
  `OverflowException` (e.g. out-of-range `decimal`/`TimeSpan`). Untrusted input produces 500.
- Change: include `OverflowException` in the filters and map it to `BadRequestHttpException`.
- Test: numeric enum overflow ⇒ 400; `decimal`/`TimeSpan` overflow ⇒ 400.

### 2.3 Route validation: support inline defaults

- File: `Discovery.cs:170-171`, `Discovery.cs:178-188`
- Problem: the regex `\{\*?([^}:?*\s]+)` does not exclude `=`, so `{id=5}` is read as the name
  `id=5` and `[FromRoute] int id` is rejected at startup.
- Change: prefer `RoutePatternFactory.Parse(route)` and `pattern.Parameters` to extract the names;
  alternatively exclude `=` in the character class.
- Test: route `/{id=5}` with `[FromRoute] int id` ⇒ discovery ok; `{id?}`, `{id:int}`, `{*path}` stay ok.

### 2.4 Multiple binding attributes on the same parameter

- File: `Discovery.cs:93-94`
- Problem: `FirstOrDefault` chooses based on reflection order; `[FromQuery]` + `[FromBody]`
  on the same parameter is ambiguous and silent.
- Change: count the binding attributes and throw `DynamicHttpConfigurationException` if > 1.
- Test: dedicated fixture (Fixtures assembly) that expects the configuration error.

### 2.5 Types not supported by the serializer

- File: `Runtime.cs:166-180`
- Problem: `ReadFromJsonAsync` can throw `NotSupportedException` (e.g. `[FromBody] IFormFile`,
  types without a converter) ⇒ 500.
- Change: catch `NotSupportedException` and return 400/a clear configuration error;
  consider rejecting non-serializable framework types at discovery.
- Test: `[FromBody] IFormFile` (or a non-serializable type) ⇒ controlled response, not 500.

---

## Phase 3 — P2: startup robustness and DI registration

### 3.1 Do not overwrite user registrations / double registrations

- File: `Configuration.cs:43-49`
- Problem: `services.Add(...)` always appends. If the user already registered the service (custom
  lifetime or factory), the library's descriptor wins; two calls to `AddDynamicHttp` duplicate
  everything and the last registry/options wins.
- Change: use `TryAdd` (or skip if `ServiceType` is already present); consider a guard for duplicate
  calls to `AddDynamicHttp`.
- Test: user singleton registration before `AddDynamicHttp` ⇒ lifetime preserved; double
  call ⇒ a single descriptor and the same assemblies.

### 3.2 Ambiguous route conflicts

- File: `Discovery.cs:203-213`
- Problem: the comparison is on the literal string; `/x/{id}` and `/x/{name}` are not detected and
  produce `AmbiguousMatchException` on the first request (500).
- Change: normalize the templates (parsing) before comparing and detect ambiguous pairs.
- Test: two endpoints `/x/{id}` and `/x/{name}` ⇒ configuration error at startup.

### 3.3 Methods/statics/generics and framework types

- File: `Discovery.cs:37`, `Discovery.cs:111-114`, `Invoker.cs:19`, `Configuration.cs:47`
- Problems: `static` methods with `[HttpGet]` silently ignored; generic methods or open generic
  services fail with opaque exceptions at startup; `HttpContext`/`HttpRequest` parameters treated
  as a JSON body.
- Change: validation at discovery with `DynamicHttpConfigurationException` and explicit messages
  (or dedicated support for framework types).
- Test: fixture with a static method, generic method, open generic service, `HttpContext` parameter.

### 3.4 `GetTaskResult` and non-generic subclasses of `Task<T>`

- File: `Runtime.cs:322-329`
- Problem: `type.IsGenericType` is false for `class MyTask : Task<string>` ⇒ result lost (null).
- Change: look for the `Result` property regardless of `IsGenericType`, or compile
  an extractor per endpoint at discovery.
- Test: endpoint returning a custom subclass of `Task<T>` ⇒ value serialized.

### 3.5 Reflection in the request path for `ValueTask<T>`

- File: `Runtime.cs:306-317` (conflict with `CLAUDE.md:34`)
- Problem: `GetMethod("AsTask")` / `GetProperty("Result")` on every request.
- Change: per-endpoint cache (delegates or `MethodInfo`/`PropertyInfo` resolved at discovery).
- Test: manual benchmark or functional test for `ValueTask<int>`/`ValueTask<IResult>`; no regression.

---

## Phase 4 — Logging

### 4.1 Double logging of unexpected errors

- File: `Runtime.cs:63-78`, `Exceptions.cs:49-74`
- Problem: non-`IHttpException` exceptions are logged at Error level both by the endpoint and by the
  middleware. The previous fix only covered handled exceptions.
- Change: choose a single owner (endpoint with service/method context, or middleware with
  traceId) and document it.
- Test: `LoggingTests` must verify the count (a single Error entry), not just presence.

### 4.2 Client disconnection logged as an error

- File: `Runtime.cs:69`
- Problem: `OperationCanceledException` from `RequestAborted` is logged at Error level by the endpoint,
  while the middleware intentionally excludes it (`Exceptions.cs:51-55`).
- Change: exclude the exception when `context.RequestAborted.IsCancellationRequested`.
- Test: aborted request ⇒ no Error log.

---

## Phase 5 — Missing tests (in addition to those already listed)

- [x] Metadata: tags, group name, `ProducesResponseType` applied by `MapDynamicHttp`.
- [x] `DefaultServiceLifetime` respected (scoped per request).
- [x] `ScanCallingAssembly()` (in addition to the `AddDynamicHttp` fallback).
- [x] Missing required header ⇒ 400; header with an explicit name.
- [x] `Task<IResult>`, `ValueTask<IResult>`, `ValueTask<int>`, non-generic `ValueTask`.
- [x] Faulted `Task`/`ValueTask` ⇒ 500 ProblemDetails.
- [x] The body of a 500 does not expose the exception message/stack.
- [x] Discovery: multiple HTTP attributes, `[FromRoute]` not present in the template, multiple body parameters.
- [x] Test leak fix: `IntegrationTests.cs:345-362` creates a `WebApplication` without `await using`;
      add it to stop the host and socket.

---

## Phase 6 — Cleanup and readability

- [x] Split `Runtime.cs` (endpoint mapping / binding-conversion / await) into separate files.
- [x] Extract a common base for `IHttpException` (`Exceptions.cs:19-39`) to avoid duplications
      of `Title`/`Type`.
- [x] Remove trailing whitespace: `Discovery.cs:13`, `Runtime.cs:273`; add
      `trim_trailing_whitespace = true` in `.editorconfig`.
- [x] `Runtime.cs:155`: `nameof(definition.Kind)` instead of `nameof(definition)`.
- [x] `Discovery.cs:18-21`: use `.OfType<Type>()` and evaluate logging `LoaderExceptions`.
- [x] `Configuration.cs:21`: add `[MethodImpl(MethodImplOptions.NoInlining)]` to
      `ScanCallingAssembly` for consistency with `AddDynamicHttp`.
- [x] Document in README: mandatory `UseDynamicHttpExceptionHandling`, binding rules,
      `[DynamicAllowAnonymous]` semantics on an authorized class, behavior for `null`/void.
- [x] Sample: register `DevAuthHandler` only in Development (`Sample/Program.cs:9-15`).
- [x] Tests: evaluate splitting `IntegrationTests.cs` by functional area; expose the metadata
      (e.g. `Definitions` or an inspection method) to test without an HTTP host, if useful.

## Outcome

- **Build/test**: `dotnet build --configuration Release` without warnings; `dotnet test --configuration
  Release`: 73 passed, 0 failed.
- **Phase 1**: authorization combines `Policy`, `Roles`, and `AuthenticationSchemes` in the same
  `AuthorizeAttribute`; added `tests/AuthorizationTests.cs` with policy, role, explicit scheme,
  class-level policy, and method-level `[DynamicAllowAnonymous]`.
- **Phase 2**: `null` body accepted only for nullable parameters (nullability-aware); empty body,
  malformed JSON, and non-serializable types ⇒ 400; enum/`decimal`/`TimeSpan` overflow ⇒ 400; routes
  validated with `RoutePatternFactory` (inline defaults supported); multiple binding attributes
  rejected at discovery.
- **Phase 3**: `TryAdd` + assembly merge on repeated calls; ambiguous routes normalized;
  static/generic methods and open generic services rejected; framework types (`HttpContext`,
  `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`) **supported** instead of rejected;
  `Task<T>` subclasses handled through compiled `Result` accessors and caching; no reflection in the
  request path.
- **Phase 4**: a single Error log per unexpected exception (the endpoint logs with
  service/method context and marks `HttpContext.Items`; the middleware avoids the duplicate). Client
  disconnections (`RequestAborted`) not logged; dedicated test with `HttpContext.Abort()`.
- **Phase 6 — loader exceptions**: `ReflectionTypeLoadException.Types.OfType<Type>()` is used to
  continue with the loadable types; no log was added because `DynamicHttpDiscovery` has no
  logger and introducing a dependency only for this case was not desired.
- **Phase 6 — test split**: added `AuthorizationTests`, `ConfigurationTests`, `MetadataTests`,
  `DiscoveryTests` (extended); `IntegrationTests` remains the file for binding/return shape. The metadata
  are inspected through `IEndpointRouteBuilder.DataSources` without exposing `Definitions`.

---

## Recommended order

1. Phase 1 (authorization) — security risk, must be released first.
2. Phase 2.1–2.2 (nullable body, overflow) — untrusted input and response correctness.
3. Phase 2.3–2.5 + Phase 3.1–3.2 — startup validation and DI.
4. Phase 4 (logging) — operational noise.
5. Phase 5–6 — coverage and maintenance, possibly across multiple PRs.

## Note on the plan's location

This plan lives in `plans/dynamichttp-fix-plan-2026-10-25.md` at the repository root and was
excluded from source control via the `plans/` entry in `.gitignore` (it is now tracked).
