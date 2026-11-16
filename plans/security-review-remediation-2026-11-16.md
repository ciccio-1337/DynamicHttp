# Security review remediation — 2026-11-16

> **Status: implemented on 2026-11-16.**
> Scope: full repository at `main` (clean working tree, no diff), including the library,
> `Sample/`, `tests/` and CI/CD workflows.

## Context

- Repository: `/home/fg/Repositories/DynamicHttp`.
- Source of findings: full static security review (no diff available).
- Goal: close the review findings without changing the architectural model (startup discovery,
  native ASP.NET Core metadata, compiled invokers, no unplanned request-path reflection).
- Fail-closed is preferred whenever the library cannot prove that a configuration is safe.

### Acceptance criteria

```bash
dotnet build --configuration Release   # zero warnings (TreatWarningsAsErrors is on)
dotnet test  --configuration Release
```

Every finding gets regression tests. The existing sequence/authorization/validation behaviour
must remain green.

---

## Findings and changes

### H1 — Authorization depended on `UseAuthorization()` being present (Medium)

- **Problem:** `MapDynamicHttp` only attached `IAuthorizeData` metadata. If an application
  registered authorization services and attributes but forgot `app.UseAuthorization()`, every
  endpoint executed for anonymous callers.
- **Change:**
  - The mapped request delegate now enforces the endpoint's effective authorization itself,
    combining the endpoint's ordered `IAuthorizeData`/`AuthorizationPolicy` metadata (or the
    configured fallback policy) through `AuthorizationPolicy.CombineAsync`, then calling
    `IPolicyEvaluator.AuthenticateAsync`/`AuthorizeAsync` and the policy's authentication
    schemes when challenging/forbidding. `[AllowAnonymous]` metadata still opts out.
  - This mirrors `AuthorizationMiddleware`/`AuthorizationMiddlewareResultHandler`, so the
    result is identical when `UseAuthorization()` is present and fail-closed when it is absent.
  - `MapDynamicHttp` now requires `AddAuthorization()` when any non-anonymous endpoint carries
    authorization metadata (or `RequireAuthorizationByDefault` is enabled) and fails at startup
    instead of at request time.
- **Tests:** authorization without `UseAuthorization()` returns 401/403/200 as appropriate;
  mapping without authorization services throws; `[AllowAnonymous]` still opts out.

### H2 — `[BindNever]` protection was incomplete (Medium)

- **Problem:** Discovery only inspected public properties of the declared body type. It missed
  `[BindNever]` on the model type, nested type, constructor/record positional parameter and
  polymorphic derived type.
- **Change:** `FindBindNeverProperty` now uses a shared reachable-type traversal that includes
  collection elements, nested complex properties, `[JsonDerivedType]` targets and constructors.
  Type-level and constructor-parameter occurrences are rejected with the same guidance to use a
  dedicated request DTO and `[JsonIgnore]`.
- **Tests:** type-level, nested-type, record-parameter and derived-type cases.

### H3 — Polymorphic/runtime-derived body types could bypass validation (Medium)

- **Problem:** `RequiresObjectValidation` was computed from the declared type only. A declared
  base type without annotations skipped validation even when the runtime polymorphic type had
  `ValidationAttribute`s.
- **Change:**
  - Reachable-type traversal now follows `[JsonDerivedType]` targets.
  - Abstract/interface roots, attribute-declared polymorphic roots (`[JsonPolymorphic]`/
    `[JsonDerivedType]`) and every non-sealed reference type are treated as requiring validation,
    so the runtime plan (`value.GetType()`) is always applied. A custom JSON type resolver
    therefore cannot bypass validation by substituting an undecorated derived type.
  - `WarmPlans` warms derived-type plans at startup.
- **Tests:** polymorphic root and nested polymorphic property validate the runtime type.

### H4 — Type-level and constructor-parameter validation attributes were ignored (Low)

- **Problem:** `BuildPlan`/`ComputeRequires` only inspected properties, so type-level
  `[CustomValidation]` was never run and validation attributes on record positional parameters
  were silently dropped.
- **Change:**
  - Type-level `ValidationAttribute`s and `IValidatableObject` mark the plan as directly
    validated; `Validator.TryValidateObject` runs the type-level rules.
  - Discovery rejects a validation attribute placed only on a constructor parameter when the
    matching property does not carry the same attribute type, with a message explaining
    `[property: ...]` for records. This makes the unsupported placement a startup error rather
    than a silent bypass.
- **Tests:** type-level `[CustomValidation]` is enforced; bare record parameter attribute is
  rejected; `[property: ...]` is accepted.

### H5 — Antiforgery was opt-in even for cookie authentication (Medium, cookie auth only)

- **Problem:** `RequireAntiforgeryByDefault` defaults to `false`. Legacy cookie-authenticated
  state-changing endpoints without `[DynamicAntiforgery]` were vulnerable to CSRF.
- **Change:** When the application's **default** authentication scheme is a cookie handler and
  the option was not set explicitly, antiforgery validation is enabled automatically for unsafe
  methods and a startup warning explains how to register `AddAntiforgery()` or opt out
  explicitly with `RequireAntiforgeryByDefault = false`. Explicit configuration always wins.
- **Tests:** cookie default auto-enables protection for POST; explicit `false` opts out.

### H6 — Constrained and unconstrained overlapping routes could shadow authorization (Low)

- **Problem:** `NormalizeRoute` marked constrained parameters differently, so `/x/{id}` and
  `/x/{id:int}` were never compared even though both match `/x/42`. Route precedence selected
  the constrained endpoint, which could have weaker authorization.
- **Change:** All route parameters normalize to the same placeholder (including catch-all), and
  `ConstraintsMayOverlap` decides disjointness. Unconstrained parameters overlap everything, so
  the pair now fails at startup. Clearly disjoint constraints (`int` vs `guid`) remain allowed.
- **Tests:** `/x/{id}` vs `/x/{id:int}` and catch-all vs simple parameter now conflict; existing
  disjoint-constraint tests remain green.

### H7 — DevAuth could become an authentication bypass (Low, sample only)

- **Problem:** The sample handler authenticated any loopback request with `X-Dev-User`, and
  `AllowRemote` disabled the loopback/Host checks. A development/staging instance with the
  option enabled could be impersonated.
- **Change:** The handler now also requires `IHostEnvironment.IsDevelopment()` inside the
  handler (defense in depth), bounds the header length, rejects control characters, and trims
  the user name. `AllowRemote` remains an explicit local-testing escape hatch.
- **Tests:** handler is disabled outside Development; header validation rejects oversized /
  control-character values (sample project reference added to the test project).

### H8 — Logs could leak sensitive exception/path data (Low)

- **Problem:** Unexpected exceptions were logged with the full exception object, and the
  fallback path logger used the concrete request path when no route template existed.
- **Change:**
  - Error logs now contain the exception type only; the full exception is logged at Debug
    (behind `IsEnabled`). Controlled errors remain stable Information messages.
  - When no route template is available, the logger uses a fixed `(unmatched request)`
    placeholder instead of the concrete path, so tokens/PII in unmatched paths cannot leak.
- **Tests:** error record carries no exception object; unmatched request path tokens are not
  logged.

### H9 — No default body limit and unbounded validation errors (Low, DoS hardening)

- **Problem:** `MaxRequestBodyBytes` defaulted to `null`, and validation accumulated every
  failure into one very large ProblemDetails response.
- **Change:**
  - `MaxRequestBodyBytes` now defaults to 1 MiB. Setting it to `null` explicitly keeps the
    server limit (documented opt-out).
  - Validation stops after 10 results and the formatted message is capped at 1024 characters,
    with an “additional errors omitted” marker. Raw values are still never echoed.
- **Tests:** default limit value; validation result cap; bounded ProblemDetails response.

### H10 — CI/CD supply-chain hardening (Informational)

- **Change:**
  - `actions/checkout` uses `persist-credentials: false` in CI and CD.
  - CD splits build/test/pack (read-only) from publish (write permissions + protected
    `release` environment), so `packages: write`/`id-token: write` are scoped to the publish job.
  - NuGet keys are passed through environment variables, not command-line arguments.
  - CI runs `dotnet list package --vulnerable --include-transitive`.

---

## Documentation

`README.md` and `CLAUDE.md` were updated for: fail-closed authorization, polymorphic/type-level
validation and constructor-parameter rejection, the 1 MiB default body limit, capped validation
errors, cookie-auth antiforgery auto-enablement, route-conflict normalization, DevAuth
environment check and the new logging behaviour.

## Implementation notes

- No public API is removed. `MaxRequestBodyBytes` keeps its nullable type; the default value
  changes from `null` to 1 MiB and `null` is now the explicit opt-out.
- The authorization enforcement runs in addition to framework metadata, so applications that
  already call `UseAuthorization()` see no behavioural change (the check is idempotent).
- The test helpers register `AddAuthorization()` because the test assembly intentionally
  contains services with `[DynamicAuthorize]`; this mirrors a real application.
