# Remediation plan — DynamicHttp security review (2026-11-06)

> **Status: completed on 2026-11-06.**
> Release build without warnings/errors; test suite: **160 green tests** (they were 131).
> Static review of the entire repository at commit `ec7faaa` (clean working tree at the start).
> Goal: close the issues raised by the security review without changing the architectural
> model (startup discovery, native ASP.NET Core metadata, no unplanned reflection in the
> request path).

## Context

- Repository: `/home/fg/Repositories/DynamicHttp`
- Base: `ec7faaa` (clean working tree, no diff).
- Scope: root library, `Sample/`, `tests/`, CI/CD workflows.

## Findings and changes

### M1 — Nested model validation (Medium)

- Problem: `HasObjectValidation` only looks at first-level properties and
  `Validator.TryValidateObject` does not recurse into complex types/collections. A DTO with
  `List<OrderLine>` and `[Range]` on the element is not validated at all.
- Change:
  - New validation plan per type, built once and cached
    (`ObjectValidationPlan`).
  - Recursive detection of `IValidatableObject`, `ValidationAttribute`, and complex/collection
    properties with metadata.
  - Recursive validation of the object graph with cycle protection
    (`ReferenceEqualityComparer`).
  - Error paths such as `lines[0].price` without echoing raw values.
  - Plan built at startup for the body type; derived runtime types are
    planned on first use and cached.
- Test: invalid nested object → 400; collection with an invalid element → 400;
  valid object → 200; object cycle handled.

### M2 — Antiforgery for custom/unsafe verbs (Medium)

- Problem: `IsStateChangingMethod` only recognizes POST/PUT/PATCH/DELETE; the custom verbs
  supported by the library and state-changing GETs are not validated.
- Change: consider only GET/HEAD/OPTIONS/TRACE safe; all other verbs
  (including custom ones) require the token when `[DynamicAntiforgery]` is present.
- Test: GET endpoint with `[DynamicAntiforgery]` without a token → 400; custom verb with
  `[DynamicAntiforgery]` without a token → 400; valid token → 200.

### M3 — Over-posting and ignored `[BindNever]` (Medium)

- Problem: JSON binding populates all public properties; MVC's `[BindNever]` is not
  honored and is silently ignored.
- Change: reject body models at startup (including nested/collections) that use
  `[BindNever]`, with a message pointing to DTOs or `[JsonIgnore]`. Add the
  check also for `[BindNever]` on parameters.
- Test: model with a `[BindNever]` property → `DynamicHttpConfigurationException`;
  parameter with `[BindNever]` → error.

### L1 — Body limit after antiforgery parsing (Low)

- Problem: `ValidateAntiforgeryAsync` precedes `EnforceRequestBodyLimit`; a huge form
  is read before the 413 check.
- Change: apply the limit first, then antiforgery, then binding.
- Test: `[DynamicAntiforgery]` endpoint with a small limit and a form body over the limit →
  413 (not 400).

### L2 — Chunked body limit with a read-only feature (Low)

- Problem: if `IHttpMaxRequestBodySizeFeature` is read-only/absent and
  `Content-Length` is missing, the limit is not applied.
- Change: `Stream` wrapper that counts the bytes and throws `PayloadTooLargeHttpException`
  over the limit; used when the feature is not modifiable.
- Test: read-only stream over the limit → 413/exception; read within the limit ok.

### L3 — Authorization on interfaces (Low)

- Problem: `GetCustomAttributes(inherit: true)` does not traverse interfaces; an
  `[Authorize]`/`[DynamicAuthorize]` on the interface is ignored.
- Change: collect authorization and allow-anonymous metadata from
  `serviceType.GetInterfaces()` (type level) and from the
  `GetInterfaceMap` (method level), keeping conflicts fail-closed.
- Test: interface with type-level `[Authorize]` → 401/403/200; interface with
  method-level `[Authorize]` → 401/403/200; conflict with `[AllowAnonymous]` →
  configuration error.

### L4 — DevAuth loopback fail-open (Low)

- Problem: `remote is not null && ...` lets a null IP through; a local proxy or DNS
  rebinding can reach the loopback listener.
- Change: fail-closed on a null IP; require a loopback `Host` (`localhost`,
  `127.0.0.1`, `[::1]`) unless `DevAuth:AllowRemote=true`; update the documentation.
- Test: static verification/build; the sample is not referenced by the tests.

### L5 — Paths in logs (Low)

- Problem: errors log the full `Request.Path`, which can contain tokens/PII;
  `Method`/`TraceIdentifier` are not sanitized; `char.IsControl` does not cover
  Unicode/bidi separators.
- Change:
  - Log the route template (`RouteEndpoint.RoutePattern.RawText`) when available,
    with a fallback to the sanitized path.
  - Sanitize method and trace id.
  - Extend `SanitizeForLog` to `UnicodeCategory.Format`,
    `LineSeparator`, `ParagraphSeparator`.
- Test: log with a token in the path does not contain the token; sanitization of U+2028/U+202E.

### L6 — Flags enum with a negative sentinel (Low)

- Problem: `All = -1` makes all bits `allowed` and accepts undefined values.
- Change: mask of non-negative values only; accept exactly
  defined values via `Enum.IsDefined`; reject non-allowed bits.
- Test: enum with `All = -1`: `999` → 400, `-1` → 200, `Read` → 200.

### L7 — CI/CD supply chain (Low)

- Change:
  - Pin the actions to full commit SHAs.
  - Explicit `permissions: contents: read` in CI.
  - Comment/protected environment for the release job.
- Test: YAML/build verification; no executable test.

### Additional hardening

- New option `RequireAntiforgeryByDefault` (default `false`) to require the token
  on all unsafe methods, with merge/conflict handling like the other options.
- Document that `RequireAuthorizationByDefault` cannot detect the absence of
  `UseAuthorization()`.
- Document that `HttpException.Detail` is visible to the client and must not contain
  secrets.
- Document the use of DTOs/`[JsonIgnore]` to prevent over-posting.

## Planned files

- `Validation.cs` (new): plan and recursive validation.
- `Discovery.cs`: uses the plan, `[BindNever]`, interface metadata.
- `Binding.cs`: recursive validation, flags enum.
- `Runtime.cs`: body limit/antiforgery order, limiting stream,
  `RequireAntiforgeryByDefault`, path logging.
- `Exceptions.cs`: sanitization and route template in logs.
- `Configuration.cs`: new option.
- `Metadata.cs`: optional default antiforgery flag.
- `Sample/DevAuthHandler.cs`: loopback fail-closed + Host.
- `README.md`, `CLAUDE.md`: documentation.
- `tests/*`: regressions.
- `.github/workflows/ci.yml`, `cd.yml`: pinning and permissions.

## Verification

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

## Outcome

- Final verification:
  - `dotnet build --configuration Release`: 0 warnings, 0 errors.
  - `dotnet test --configuration Release --no-build`: 160 passed, 0 failed, 0 skipped.
- M1: introduced `Validation.cs` with `ObjectValidationPlan` per type, `ConcurrentDictionary`
  cache, recursive detection of `IValidatableObject`/`ValidationAttribute`,
  traversal of complex properties and collections, cycle protection through
  `ReferenceEqualityComparer`, and `Child.Price`/`Lines[0].Price` paths. The body
  model plans are warmed during discovery; derived runtime types are planned on
  first use and cached.
- M2: an explicit `[DynamicAntiforgery]` is always validated, even on GET and custom verbs.
  The new `RequireAntiforgeryByDefault` option considers only GET/HEAD/OPTIONS/TRACE safe
  and requires the token on all other methods.
- M3: `[BindNever]` is rejected both on the parameter and anywhere in the body
  model graph (nested models and collection elements), with a message pointing to DTOs and
  `[JsonIgnore]`.
- L1: the body limit is applied before antiforgery and binding; an oversized
  form returns 413 without being read.
- L2: when `IHttpMaxRequestBodySizeFeature` is absent/read-only and the body is chunked,
  `LimitedReadStream` with byte counting is used. The server limit is never raised:
  if the feature exposes a lower limit (even read-only), that limit is respected.
- L3: authorization and allow-anonymous are also collected from interfaces, at type level
  (`GetInterfaces`) and method level (`GetInterfaceMap`), keeping conflicts fail-closed.
  `DynamicAuthorizeAttribute`/`DynamicAllowAnonymousAttribute` now declare
  `AttributeTargets.Interface`. The framework `[Authorize]` cannot be applied at
  type level on an interface due to its own `AttributeUsage`; method-level framework
  attributes are still collected.
- L4: DevAuth fails closed when `RemoteIpAddress` is null and requires a loopback `Host`
  (`localhost`, `127.0.0.1`, `[::1]`) unless `DevAuth:AllowRemote=true`.
- L5: logs use the route template when available, with a fallback to the sanitized path;
  method and trace id are sanitized; `SanitizeForLog` covers control, `UnicodeCategory.Format`,
  `LineSeparator`, and `ParagraphSeparator`.
- L6: exactly defined enum values are accepted (including negative sentinels such as
  `All = -1`); undefined combinations are accepted only if all bits belong to
  declared non-negative flags.
- L7: actions pinned to full SHAs, `permissions: contents: read` in CI, protected `release`
  environment in CD.
- Hardening: added `RequireAntiforgeryByDefault` with merge/conflict handling; documented
  the body limit, authorization on interfaces, the caveat about `UseAuthorization()`,
  the antiforgery behavior, the DevAuth Host requirement, the route-template-based logs,
  and the fact that `HttpException.Detail` is visible to the client.
- No commit was created: the changes remain in the working tree.
