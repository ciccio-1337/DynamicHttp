# DynamicHttp remediation plan — 2026-11-14

> **Status: implemented on 2026-11-16.**
> Release build without warnings; test suite: **193 green tests** (they were 160).
> Deviations from the plan are listed under "Implementation notes" at the bottom.

## Context

- Repository: `/home/fg/Repositories/DynamicHttp`, baseline `main` @ `b1b9b7e`, clean working tree.
- Source of findings: full-repository code review (no diff available).
- Scope: all phases 1–5, including security/data-integrity bugs, startup contracts, request-path
  robustness, route-conflict accuracy, build config and docs.
- Decision taken with the user: **route conflicts fail closed at startup** when disjointness
  cannot be proven.

### Acceptance criteria (per phase)

```bash
dotnet build --configuration Release   # zero warnings (TreatWarningsAsErrors is on)
dotnet test  --configuration Release
```

Every phase adds regression tests before/with the fix and must leave the suite green.

### Guardrails

- Keep the architecture: discovery at startup, compiled invokers, native ASP.NET Core metadata,
  no reflection in the request path.
- Do not change existing documented behavior that is already correct.
- No edits under `plans/` (historical documents). This plan lives outside the repo.

---

## Phase 1 — Silent security and data-integrity gaps (High)

### 1.1 Root-level collection bodies bypass validation

**Files:** `Validation.cs:23-24`, `:29-52`, `:59-66`, `:73-127`, `:188-243`, `:245-319`;
`Discovery.cs:269-291`.

**Problem.** `ValidateNode` only recurses through *properties* of an object and through
*properties that are enumerable*. The value passed to `Validate` is never enumerated, so
`[FromBody] List<Foo>`, `Foo[]`, `List<List<Foo>>` are returned unvalidated. `ComputeRequires`
and `WarmPlans` likewise only look at the root object's properties (and never unwrap an
enumerable root), so `RequiresObjectValidation(typeof(List<Foo>))` is false and validation is
skipped before `Validate` is even called.

**Change.**

1. Add a private helper that expands a type to its validation roots, recursing through
   enumerables:

   ```csharp
   private static IEnumerable<Type> ValidationRoots(Type type)
   {
       Queue<Type> queue = new();
       HashSet<Type> seen = [];
       queue.Enqueue(Unwrap(type));

       while (queue.TryDequeue(out Type? current))
       {
           current = Unwrap(current);
           if (current == typeof(string) || IsSimple(current)) continue;

           if (TryGetEnumerableElementType(current, out Type element))
           {
               queue.Enqueue(element);   // handles List<List<T>>
               continue;
           }

           if (seen.Add(current)) yield return current;
       }
   }
   ```

2. `ComputeRequires`: seed the BFS with `ValidationRoots(root)` instead of `Unwrap(root)`.
3. `WarmPlans`: seed with `ValidationRoots(type)`.
4. `Validate`: if the runtime value is enumerable, enumerate it and call `ValidateNode` per
   non-null element with path `"[i]"`; otherwise keep the current single-node call. Reuse the
   existing `visited` set so shared/cyclic graphs are still validated once.

**Notes.**
- `TryGetEnumerableElementType` already excludes `string` and returns `typeof(object)` for
  non-generic `IEnumerable`; runtime enumeration in `ValidateNode` uses runtime types.
- `Dictionary<K,V>` will validate through `KeyValuePair<K,V>.Value`; acceptable.
- `IEnumerable<Foo>` as a body type is not deserializable by `ReadFromJsonAsync` and already
  yields a 400 (`Binding.cs:113-117`); do not add startup rejection in this phase.

**Tests** (`tests/ValidationTests.cs`, `tests/IntegrationTests.cs`):
- `List<Foo>`, `Foo[]`, `List<List<Foo>>` with `[Range]` on `Foo` → `Validate` returns errors;
  integration POST returns 400.
- `RequiresObjectValidation(typeof(List<Foo>))` is true; false for `List<int>`.
- Existing nested-DTO tests still pass.

### 1.2 Root-level `[BindNever]` not rejected

**Files:** `Validation.cs:73-127`; `Discovery.cs:273-281`.

**Problem.** Same root cause: `FindBindNeverProperty` starts at the object type and never
unwraps a collection root, so `[FromBody] List<DtoWithBindNever>` is accepted.

**Change.**
- Seed the `FindBindNeverProperty` queue from `ValidationRoots(type)`, each with an empty path
  prefix (root elements report bare property names, e.g. `Hidden`; nested collections keep the
  existing `Lines[].Hidden` shape).
- Improve the discovery message in `Discovery.cs:277-280` to name the element type when the
  body type is a collection (e.g. `Body model 'List<Dto>' uses [BindNever] on 'Hidden'`).

**Tests** (`tests/DiscoveryTests.cs`):
- `[FromBody] List<Dto>` where `Dto.Hidden` has `[BindNever]` → `DynamicHttpConfigurationException`.
- Array and nested-list variants.
- `List<PlainDto>` stays accepted.

### 1.3 Antiforgery on interface methods is ignored (fail-open)

**Files:** `Discovery.cs:473-475`; compare `:441-455` and `:457-471`.
`Attributes.cs:102-103` allows `AttributeTargets.Method`, so interface methods are legal.

**Problem.** `IsAntiforgeryRequired` checks only the class and the concrete method.
Authorization and allow-anonymous deliberately scan interface methods; antiforgery does not,
so moving `[DynamicAntiforgery]` to an interface silently removes CSRF protection.

**Change.** Mirror the allow-anonymous/authorization shape:

```csharp
private static bool IsAntiforgeryRequired(Type serviceType, MethodInfo method) =>
    IsAntiforgeryOnType(serviceType) ||
    serviceType.GetInterfaces().Any(IsAntiforgeryOnType) ||
    IsAntiforgeryOnMethod(method) ||
    GetInterfaceMethods(serviceType, method).Any(IsAntiforgeryOnMethod);
```

(`AttributeTargets.Class` does not cover interface *types*, so only the method scan matters,
but including the interface-type scan is harmless and future-proof.)

**Tests** (`tests/DiscoveryTests.cs`, `tests/AuthorizationTests.cs` / antiforgery tests):
- Interface method with `[DynamicAntiforgery]`, implementing `[HttpPost]` → definition has
  `RequireAntiforgery == true`.
- Integration: POST without token → 400 even though the concrete method has no attribute.
- Class-level attribute on the interface method's declaring class still works.

### 1.4 Whitespace-only authorization values are silently weakened

**Files:** `Discovery.cs:388-407`; call chain `GetAuthorization → ToAuthorizeAttribute →
CreateAuthorization → Whitelist`.

**Problem.** `Whitelist` maps whitespace to `null`. `[DynamicAuthorize(Roles = " ")]` therefore
becomes "no role requirement" instead of a startup error — a typo turns a restrictive endpoint
into an open one.

**Change.**
- Thread `serviceType`/`method` into `ToAuthorizeAttribute` and `CreateAuthorization`.
- Replace `Whitelist` with a validation helper:

  ```csharp
  private static string? NormalizeAuthorizationValue(
      string? value, string field, Type service, MethodInfo method) =>
      value is null ? null
      : string.IsNullOrWhiteSpace(value)
          ? throw Configuration(service, method,
              $"Authorization attribute value '{field}' is whitespace-only; remove it or specify a value.")
          : value;
  ```

- Apply to `Policy`, `Roles`, `AuthenticationSchemes` for both `IAuthorizeData` and
  `DynamicAuthorizeAttribute`. Null stays null (plain `[Authorize]` unaffected).

**Tests** (`tests/DiscoveryTests.cs`):
- `[DynamicAuthorize(Roles = " ")]`, `Policy = " "`, `AuthenticationSchemes = " "` → configuration
  exception naming service and method.
- Framework `[Authorize(Policy = " ")]` on class and on method → same.
- `[Authorize]` with no values still maps to a null-policy requirement.

---

## Phase 2 — Fail-fast startup contracts (Medium)

### 2.1 Non-public / unconstructible services

**Files:** `Discovery.cs:54-63`, `:65-72`.

**Problem.** Non-public `[HttpService]` classes are discovered, registered and mapped, then DI
fails at request time with a 500.

**Change.**
- In `ValidateServiceType`: require at least one public instance constructor → otherwise throw
  `DynamicHttpConfigurationException` naming the type. (See implementation notes: requiring the
  type itself to be `IsVisible` was deliberately not done.)

**Tests** (`tests/DiscoveryTests.cs` + fixture project):
- Internal service with `[HttpService]` → startup configuration exception.
- Public service with only an internal constructor → startup configuration exception.
- Public service with a public constructor → accepted.

### 2.2 Unsupported parameter and return shapes

**Files:** `Discovery.cs:158-177` (`ValidateEndpointMethod`), `:506-534`
(`ValidateParameters`), `Invoker.cs:8-27`.

**Problem.** `ref`/`out`/`in` parameters are already rejected
(`Discovery.cs:523-526`), but:
- pointer parameters/returns,
- byref-like (`ref struct`, e.g. `Span<T>`) parameters/returns,
- `ref` returns,

fall through to `CompiledInvokerFactory.Create` and surface as raw
`ArgumentException`/`InvalidOperationException` from expression-tree construction.

**Change.**
- `ValidateEndpointMethod`: reject `ReturnType.IsByRef`, `ReturnType.IsPointer`,
  `ReturnType.IsByRefLike` with a clear message ("return type ... is not supported; return a
  value, Task or ValueTask").
- `ValidateParameters`: reject `ParameterType.IsPointer` and `ParameterType.IsByRefLike` next to
  the existing by-ref check.
- `CompiledInvokerFactory.Create`: wrap expression construction/compilation in
  `try/catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
  NotSupportedException)` and rethrow as `DynamicHttpConfigurationException` including
  `serviceType.FullName` and `method.Name`, as a defense-in-depth net.

**Tests** (`tests/DiscoveryTests.cs`, `tests/DynamicHttp.Tests.Fixtures/`):
- `public ref int Get()` → configuration exception.
- `public int Get(Span<int> value)` → configuration exception.
- `public Span<int> Get()` → configuration exception.
- Tighten the existing `RefParameterService` assertion to the specific exception type/message.
- Pointer fixtures are optional (would require `AllowUnsafeBlocks`); skip unless cheap.

---

## Phase 3 — Request-path robustness (Medium/Low)

### 3.1 Client disconnects are logged as errors

**Files:** `Runtime.cs:116-133`; README claim about client aborts.

**Problem.** Only `OperationCanceledException` with `RequestAborted` cancelled is suppressed;
`IOException`/`ConnectionResetException` from a dropped client are logged at Error.

**Change.**
- Extract an internal predicate for testability:

  ```csharp
  internal static bool IsClientDisconnect(Exception exception, CancellationToken aborted) =>
      aborted.IsCancellationRequested &&
      exception is OperationCanceledException or IOException;
  ```

  (`ConnectionResetException` derives from `IOException`.)
- Use it in the `catch` and keep setting `LoggedExceptionItemKey` when it is false.

**Tests** (`tests/RuntimeTests.cs`):
- Predicate table: `OperationCanceledException` + cancelled → true; `IOException` + cancelled →
  true; `IOException` + not cancelled → false; `InvalidOperationException` + cancelled → false.
- Optional integration: endpoint throws `IOException` after cancelling `RequestAborted`;
  assert no Error-level log.

### 3.2 `LimitedReadStream.Clamp` overflow at `long.MaxValue`

**Files:** `Runtime.cs:363-380`.

**Problem.** `remaining + 1` overflows to `long.MinValue` when the limit is `long.MaxValue`,
`Clamp` returns 0 and the body reads as empty/EOF.

**Change.** Avoid the addition entirely:

```csharp
long allowed = remaining >= requested ? requested : remaining + 1;
return (int)Math.Min(requested, allowed);
```

`remaining + 1` is only evaluated when `remaining < requested <= int.MaxValue`, so it cannot
overflow.

**Tests** (`tests/RuntimeTests.cs`):
- Limit `long.MaxValue`, chunked body (`ContentLength = null`, read-only max-size feature) with
  a small payload → full payload is readable.
- Limit exactly equal to the payload length → readable; one byte over → `PayloadTooLarge`.

### 3.3 Binder conversion edge cases

**Files:** `Binding.cs:129-210`, `:272-303`; `Binding.cs:22-24`.

**Changes.**
- `TryConvertWithTypeConverter`: treat a `null` conversion for a non-nullable value type as a
  failure (return `false`, leading to 400), instead of returning `true` and unboxing `null`.
- Broaden the converter catch to include `InvalidCastException` (keep the existing list; do not
  catch all exceptions so genuine converter bugs are not silently turned into 400s).
- Query/header multi-value support: change `BindingKind.Query`/`Header` to pass the full
  `StringValues` to the converter. Support target types that are arrays, `List<T>` or
  `IEnumerable<T>` by converting each value; for scalar targets keep "first value wins" and
  document it. Do not change behavior for empty/missing values or nullable handling.
- `char`: already correct (`Convert.ChangeType` → `Convert.ToChar(string)` throws
  `FormatException` for length != 1, mapped to 400). No change; add a regression test only if
  cheap.

**Tests** (`tests/IntegrationTests.cs` / `Binding`-focused tests):
- `[FromQuery] int[] ids` with `?ids=1&ids=2` → `[1,2]`; `[FromQuery] List<int>` likewise.
- Scalar `int` with two values → first value (documented).
- Query parameter with a custom `TypeConverter` returning `null` for a `struct` → 400, not 500.
- Converter throwing `InvalidCastException` → 400.

### 3.4 `Task` result detection by property name

**Files:** `Awaiting.cs:57-81`.

**Problem.** Any `Result` property on a non-generic `Task` subclass is treated as the result.

**Change.** Resolve the result property from `Task<T>` structurally:

```csharp
Type? genericTask = taskType;
while (genericTask is not null &&
       !(genericTask.IsGenericType && genericTask.GetGenericTypeDefinition() == typeof(Task<>)))
{
    genericTask = genericTask.BaseType;
}

PropertyInfo? result = genericTask?.GetProperty("Result");
```

Keep the existing cache and `DeclaringType` cast. Non-`Task<T>` returns `static _ => null`.

**Tests** (`tests/IntegrationTests.cs` or a small `AwaitingTests.cs`):
- `Task<int>` still returns the value; `Task` returns null; `ValueTask<int>` still works.
- If subclassing `Task` proves practical, add a non-generic subclass with an unrelated `Result`
  property and assert it is ignored; otherwise document the gap and rely on the structural walk.

---

## Phase 4 — Route-conflict classifier (Medium)

**Files:** `Discovery.cs:536-560`, `:562-610`, `:612-703`.

**Decision (confirmed with user): fail closed.** When disjointness cannot be proven, startup
throws. Keep conservative false positives; document them.

**Bug to fix.** `ClassifyConstraints` treats `datetime` and numeric constraints as different
families, so `{id:int}` vs `{id:datetime}` is considered disjoint — but
`DateTime.TryParse("5")` succeeds, so both can match the same request.

**Change.** In `PoliciesMayOverlap`, define overlap families:

```csharp
static bool NumericLike(ConstraintKind k) => k is ConstraintKind.Numeric or ConstraintKind.DateTime;
static bool AlphaLike(ConstraintKind k)   => k is ConstraintKind.Alpha or ConstraintKind.Boolean or ConstraintKind.DateTime;

return leftKind == rightKind ||
       (NumericLike(leftKind) && NumericLike(rightKind)) ||
       (AlphaLike(leftKind)   && AlphaLike(rightKind));
```

`None`/`Unknown` keep returning `true` (fail closed). `Neutral` constraints (`required`,
`length`, `minlength`, `maxlength`) continue to be ignored — conservative overlap.

**Tests** (`tests/DiscoveryTests.cs`):
- `{id:int}` vs `{id:datetime}` → conflict.
- `{id:alpha}` vs `{id:datetime}` → conflict.
- `{id:int}` vs `{id:guid}`, `{id:bool}` vs `{id:int}` → no conflict.
- `{id:bool}` vs `{id:alpha}` → conflict (existing).
- `{id:regex(...)}` vs `{id:int}` → conflict (documented conservative behavior).

**Docs.** README section explaining that unprovable constraints fail startup and that the check
can reject routes which are in fact disjoint.

---

## Phase 5 — Build config and documentation (Low)

### 5.1 Solution Release configuration

**File:** `DynamicHttp.sln:24`.

**Change:** `{22222222-...}.Release|Any CPU.Build.0 = Release|Any CPU` (currently `Debug`).

**Verification:** `dotnet build --configuration Release` builds the Sample with the Release
output path; no Debug artifacts under `Sample/bin/Release` mixed with `Debug`.

### 5.2 README corrections

**File:** `README.md`.

- Body parameters do **not** honor C# default parameter values: only route/query/header do
  (`Binding.cs:140-147`). State this explicitly.
- The loopback `Host` check does not protect against a local reverse proxy that rewrites the
  `Host` header; soften the DevAuth security claim.
- Document multi-value query/header binding rules (Phase 3.3).
- Document startup failure for whitespace authorization values (Phase 1.4), non-public services
  and unsupported shapes (Phase 2), and conservative route-conflict detection (Phase 4).
- Note the version/behavior-change section.

---

## Cross-cutting test matrix (summary)

| Area | Test file |
| --- | --- |
| Root collection validation, BindNever | `ValidationTests.cs`, `DiscoveryTests.cs`, `IntegrationTests.cs` |
| Interface antiforgery | `DiscoveryTests.cs`, antiforgery tests |
| Whitespace authorization | `DiscoveryTests.cs` |
| Service visibility, unsupported shapes | `DiscoveryTests.cs`, `DynamicHttp.Tests.Fixtures/` |
| Disconnect predicate, body limit, clamp | `RuntimeTests.cs` |
| Binder conversions, query arrays | `IntegrationTests.cs` |
| Task result detection | `IntegrationTests.cs` (or new `AwaitingTests.cs`) |
| Route conflicts | `DiscoveryTests.cs` |

Additional gaps worth covering while touching these areas:
- `RequireAuthorizationByDefault` / `ValidateModels` / `RequireAntiforgeryByDefault` conflicts
  across repeated `AddDynamicHttp` calls.
- `RequireAntiforgeryByDefault` on HEAD/OPTIONS/TRACE and custom verbs.
- `MapDynamicHttp` idempotency across separate `IEndpointRouteBuilder`s.
- Exception handler when `Response.HasStarted`.
- Polymorphic body whose runtime derived type carries validation attributes (document as a
  known limitation if not fixed).
- Sample `DevAuthHandler` loopback/remote branches (sample is not referenced by tests; decide
  whether to add a test project reference or leave it documented as demo-only).

---

## Versioning and release

Current `DynamicHttp.csproj` version is `1.1.1`. This plan changes observable behavior:

- invalid bodies inside root collections now return 400 (intended fix);
- whitespace authorization values now fail startup;
- non-public services / unsupported shapes now fail startup;
- some previously-starting apps with ambiguous routes now fail startup;
- query/header arrays are newly supported.

Recommendation: release as **1.2.0** with explicit behavior-change notes. If strict semver is
required for the new startup exceptions, use **2.0.0**. Decide before implementing.

## Risks / open points

1. **Root-collection validation** may surface invalid clients that previously passed; this is the
   intended contract but should be called out in release notes.
2. **Fail-closed route conflicts** can reject valid apps (e.g. custom constraints); mitigated by
   clear error messages and README guidance.
3. **Non-public service rejection** may break tests or samples that rely on internal services;
   the fixture project currently has none, but verify.
4. **Query array support** must not change the "first value" behavior for scalars; document.
5. **`Awaiting` subclass test** may be impractical if `Task` cannot be subclassed in the test
   assembly; fall back to code review plus structural-walk unit coverage.

## Out of scope (optional follow-up)

- Splitting `Discovery.cs` (737 lines) into discovery / authorization / binding / route-conflict
  modules for auditability.
- Runtime-derived-type (polymorphic) validation — requires a runtime plan lookup; currently a
  documented limitation.

## Implementation notes (2026-11-16)

All phases were implemented and covered by tests. Deviations and additional findings:

1. **Service visibility (2.1).** Only the public-constructor requirement was added. Requiring
   `IsVisible` would reject the private nested service fixtures used throughout the test suite
   (and internal services are constructible by DI when they expose a public constructor), so the
   check targets the actual failure: a type with no constructor the default activator can use.
2. **`VoidTaskResult` leak (3.4).** While adding the structural `Task<T>` walk it turned out that
   non-generic `Task` / `async Task` complete as the internal `Task<VoidTaskResult>`, so the old
   (and naive new) accessor returned a boxed `VoidTaskResult` as the endpoint result. The accessor
   now returns null for that marker, so `Task` endpoints produce an empty 200 as documented.
3. **Route conflicts (Phase 4).** `datetime` now overlaps both the numeric and alpha families
   (`DateTime.TryParse("5")` and `DateTime.TryParse("May")` both succeed). Unknown/custom/regex
   constraints still fail closed as planned.
4. **Query/header collections (3.3).** Only types that `List<T>` can satisfy are bound as
   collections (arrays, `List<T>` and the standard generic collection interfaces). Other
   concrete collections such as `HashSet<T>` keep the scalar path and return 400 instead of
   failing later with an `InvalidCastException`.
5. **Fixtures build configuration.** The `tests/DynamicHttp.Tests.Fixtures` project is now part of
   `DynamicHttp.sln` with explicit Debug/Release mappings, so a solution-level Release build emits
   it under `bin/Release`. Previously it was only reachable through a project reference and the
   solution build emitted it under `bin/Debug`.

