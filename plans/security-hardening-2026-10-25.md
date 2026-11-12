# Security hardening plan — DynamicHttp

Date: 2026-10-25
Reference: security review of commit `45f5e59` (clean working tree).

## Goal

Close the 10 points raised by the security review without changing the library's
architectural model (discovery at startup, native ASP.NET Core metadata, no
reflection in the request path).

## Changes

### 1. Framework authorization attributes (Medium)

- Recognize and honor `Microsoft.AspNetCore.Authorization.AuthorizeAttribute`
  (and more generally `IAuthorizeData`) on class and method, not just
  `[DynamicAuthorize]`.
- Recognize `Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute`
  in addition to `[DynamicAllowAnonymous]`.
- Keep the existing conflict rules (authorize + anonymous in the same scope =
  startup error; anonymous on the method that overrides class-level authorize
  = allowed).
- `EndpointDefinition.Authorization` becomes `IReadOnlyList<AuthorizeAttribute>`
  with already-normalized values (whitespace → null).
- Detect MVC binding attributes (`Microsoft.AspNetCore.Mvc.From*`) and fail at
  startup: otherwise they would be ignored and the parameter would fall back to
  the default source (e.g. a secret from a header being read from the query string).

### 2. Opt-in authorization / pipeline dependency (Low, high impact)

- New option `DynamicHttpOptions.RequireAuthorizationByDefault` (default
  `false` for compatibility): when enabled, every endpoint without an explicit
  authorize and without allow-anonymous requires an authenticated user.
- If the option is enabled and `IAuthorizationPolicyProvider` is not registered,
  `MapDynamicHttp` fails with `DynamicHttpConfigurationException` and an
  explicit message (`AddAuthorization...`).
- Document that `UseAuthorization()` remains mandatory and test the 401.

### 3. Raw values in ProblemDetails and logs (Medium)

- `InvalidValue` no longer interpolates the received value: only the parameter
  name and type.
- The middleware logs handled exceptions (`IHttpException`) with a stable
  message (method, path, status) and **without** the exception object, to avoid
  log injection (CRLF/ANSI) and leakage of tokens/PII.
- `BadHttpRequestException` (e.g. body too large) is also treated as a client
  error: Information log without details and ProblemDetails without an internal
  message.

### 4. Undefined enums (Low)

- After `Enum.Parse`, verify `Enum.IsDefined` for regular enums; for `[Flags]`,
  verify that every set bit is defined.
- Otherwise 400 (same generic message as point 3).

### 5. Unknown nullability (Low)

- `NullabilityState.Unknown` is no longer treated as nullable: a required
  reference-type parameter stays required even if the service assembly was
  compiled without nullable annotations.
- C# defaults still take precedence.

### 6. CSRF / antiforgery (Low, Medium with cookie auth)

- New `[DynamicAntiforgery]` attribute (class or method) that adds the
  `RequireAntiforgeryTokenAttribute` metadata to the endpoint.
- `Runtime.cs` also checks `IAntiforgeryValidationFeature` for the
  `POST`/`PUT`/`PATCH`/`DELETE` methods and rejects with 400 if the token is
  invalid or if `UseAntiforgery()` is not in the pipeline (fail-closed).
- Document `AddAntiforgery()` / `app.UseAntiforgery()` and the recommended use
  of bearer tokens for APIs.

### 7. Request body size limit (Low)

- New option `DynamicHttpOptions.MaxRequestBodyBytes` (`long?`).
- Applied before binding: check on `Content-Length` (413) and setting of
  `IHttpMaxRequestBodySizeFeature` for chunked bodies.
- New `PayloadTooLargeHttpException` (413).

### 8. Sample dev auth (Low)

- Register `DevAuthHandler` only with `IsDevelopment()` **and**
  `DevAuth:Enabled=true`.
- Reject non-loopback requests unless `DevAuth:AllowRemote=true`.
- `Admin` granted only with `DevAuth:GrantAdmin=true`.
- Warning at startup when dev auth is enabled.
- Update the commands in README/CLAUDE.

### 9. Model validation (Informational)

- Validate the DataAnnotations attributes declared on parameters
  (`[Required]`, `[Range]`, `[StringLength]`, ...).
- New option `DynamicHttpOptions.ValidateModels` (default `true`): validate with
  `Validator.TryValidateObject` the bodies/complex parameters that have
  validation attributes or implement `IValidatableObject`. The presence of
  metadata is computed and cached at startup; types without attributes pay no
  reflection cost.
- Error messages without raw values.

### 10. CI (Informational)

- Remove `dotnet nuget add source --store-password-in-clear-text`: push
  directly to GitHub Packages with `--source <url> --api-key ${{ secrets.GITHUB_TOKEN }}`,
  so the token is not written to `NuGet.Config`.

## Files touched

- `Attributes.cs` — `DynamicAntiforgeryAttribute`
- `Configuration.cs` — new options + validation
- `Metadata.cs` — typed `Authorization`, antiforgery flag, validation metadata
- `Discovery.cs` — framework auth, conflicts, MVC binding, nullability, antiforgery
- `Binding.cs` — messages without values, enums, DataAnnotations
- `Runtime.cs` — auth metadata, default policy, antiforgery, body limit, validation
- `Exceptions.cs` — safe logging, `PayloadTooLargeHttpException`, `BadHttpRequestException`
- `Sample/Program.cs`, `Sample/DevAuthHandler.cs` — dev auth opt-in/loopback/roles
- `.github/workflows/cd.yml` — no token on disk
- `README.md`, `CLAUDE.md` — security documentation
- tests: `AuthorizationTests`, `DiscoveryTests`, `IntegrationTests`, `LoggingTests`,
  `ConfigurationTests`

## Tests to add

- Framework `[Authorize]` applied (401/403/200) and framework `[AllowAnonymous]`.
- Framework authorize/anonymous conflict at startup.
- MVC binding attribute → configuration error.
- `RequireAuthorizationByDefault`: endpoints without attributes → 401; allow-anonymous → 200;
  without `AddAuthorization` → startup error.
- Undefined numeric enum → 400.
- Invalid value not present in the ProblemDetails response or in the logs.
- Body over `MaxRequestBodyBytes` → 413.
- `[Range]` on a parameter → 400; `[Required]` on a body → 400.
- Nullability disabled (`#nullable disable`) → missing parameter 400.
- `[DynamicAntiforgery]` produces the expected metadata.

## Verification

```bash
dotnet build --configuration Release
dotnet test --configuration Release
```

## Outcome

All 10 changes were implemented. The solution and sample build
without warnings (`TreatWarningsAsErrors` enabled) and the test suite is green:
93 tests passed.
