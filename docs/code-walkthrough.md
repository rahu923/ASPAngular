# Commented code walkthrough

The source now contains explanations at the technical boundaries. Comments explain what a construct guarantees, why this application uses it, and where that guarantee ends. Routine assignments are intentionally left readable rather than narrated line by line. Generated EF migrations are unchanged.

Use this index alongside [the practice guide](learning-guide.md). Pick one flow, set the listed breakpoints and make one request. Predict each value before stepping into the next method. Never add password/token/cookie values to debugger screenshots or logs.

## 1. Product browsing: Angular to SQL

Read in this order:

1. [Catalog HTML](../frontend/src/app/features/products/products.component.html) and [component](../frontend/src/app/features/products/products.ts).
2. [Typed product API](../frontend/src/app/features/products/product-api.ts) and [HTTP interceptor](../frontend/src/app/core/http.ts).
3. [ProductsController.List](../src/OrderManagement.Api/Controllers/ProductsController.cs).
4. [ProductService.ListAsync](../src/OrderManagement.Application/Products.cs).
5. [ProductRepository.ListAsync](../src/OrderManagement.Infrastructure/ProductRepository.cs).

Breakpoints: controller List and repository ListAsync. Inspect search/sort/page, the server-controlled IncludeInactive value, cache generation, count and projected items. A cache hit legitimately skips the repository; change the query to inspect an uncached read. Production caching is disabled by default.

Concepts explained inline: combining form events, cancelling superseded queries, keeping a search stream alive after errors, IQueryable execution, projections, no tracking, stable pagination and bounded cache invalidation. Count and page do not form a consistent database snapshot.

## 2. Product administration: your currently open component

Start with [products.ts](../frontend/src/app/features/admin/products.ts) and [product-admin.component.html](../frontend/src/app/features/admin/product-admin.component.html). Follow:

```text
HTML edit click -> edit(product) -> typed form.patchValue
HTML submit -> save() -> PUT with original rowVersion
ProductsController.Update -> ProductAdminService.SaveAsync
tracked product -> UnitOfWork.SaveChangesAsync -> cache invalidation
```

Breakpoints: Angular save, API Update and ProductAdminService.SaveAsync. Compare the loaded DTO version with submitted version; observe the new database version after saving. Reload before retrying 409.

Inline comments explain standalone imports, signals, non-nullable controls, control validation, cold HTTP Observables, subscription cleanup, patchValue versus getRawValue, stable @for tracking, child input/output bindings and secondary button types. Disabling a button prevents a UI action, not an unauthorized API call.

For role management, read [users.component.html](../frontend/src/app/features/admin/users.component.html), [users.ts](../frontend/src/app/features/admin/users.ts) and [AdminController](../src/OrderManagement.Api/Controllers/AdminController.cs). Notice backend rejection of self-management, batched membership lookup and the transaction around Identity's internally saving role operations.

## 3. Login and reload restoration

Read [app.config.ts](../frontend/src/app/app.config.ts), [account component](../frontend/src/app/features/account/account.ts), [Auth service](../frontend/src/app/core/auth.ts), [AuthController](../src/OrderManagement.Api/Controllers/AuthController.cs) and [TokenService](../src/OrderManagement.Api/Security/AuthSupport.cs).

Breakpoints: Auth.login, AuthController.Login, TokenService.IssueAsync and WriteAccessAsync. Inspect role names/expiry/cookie options, not raw token values. The browser stores tokens; the Angular signal stores only user details. Reload runs the app initializer's restore path before guards decide initial navigation.

Distinctions to explain: password hashing versus refresh-token hashing; signed JWT versus encrypted data; authentication versus authorization; HttpOnly versus CSRF; the internal antiforgery cookie versus its readable request token; Identity password verification versus issuing a second application login cookie.

## 4. Refresh and authorization failure

Read [interceptor](../frontend/src/app/core/http.ts) -> [Auth.refresh](../frontend/src/app/core/auth.ts) -> [TokenService.RotateAsync](../src/OrderManagement.Api/Security/AuthSupport.cs).

Breakpoints: the interceptor's 401 branch, RotateAsync before/after LockAsync, and its consume/revoke branches. Run the existing simultaneous-refresh tests to inspect a controlled race. Never replay actual user tokens by pasting them into committed examples.

Observe why the first untracked lookup only discovers the family ID, why the token is read again after acquiring the transaction-owned application lock, and why reuse revocation commits before returning false. A successful rotation consumes and inserts in one transaction. Consumed hashes retain replay evidence.

On the client, follow the one shared Observable, finalize placement, shareReplay lifetime, session epoch and one retry through next(). A 403 never refreshes. Coordination is within one tab; independent tabs can still trigger strict reuse detection.

Read [guards](../frontend/src/app/core/guards.ts), [permission directive](../frontend/src/app/shared/ui.ts), [custom handlers](../src/OrderManagement.Api/Security/AuthSupport.cs) and [order controller](../src/OrderManagement.Api/Controllers/OrdersController.cs). UI checks only guide navigation; policies/resource checks determine data access on the backend.

## 5. Order creation, item editing and cancellation

Read [create component](../frontend/src/app/features/orders/create.ts) -> [OrderApi](../frontend/src/app/features/orders/order-api.ts) -> [OrdersController](../src/OrderManagement.Api/Controllers/OrdersController.cs) -> [OrderService](../src/OrderManagement.Application/OrderService.cs) -> [repositories](../src/OrderManagement.Infrastructure/OrderRepository.cs).

Breakpoints: CreateAsync, ReserveAsync, SaveChangesAsync and CommitAsync. Inspect tracked product stock before saving, item price snapshots and server total. Throwing before commit must leave no partial order/stock reservation. Do not make external email/HTTP calls while these locks are held.

For edits, inspect delta = newQuantity - oldQuantity: increasing reserves the difference, decreasing returns it. Existing lines keep their unit price. The response replaces Angular's DTO so the next edit submits the current parent rowversion.

For cancellation, inspect the parent update lock, already-Cancelled early return and stock restoration. Repeating cancellation returns before rejecting the caller's original version; it does not add stock again. This idempotent cancellation does not make order creation idempotent.

Important distinction in the repository comments: SQL update locks protect an explicit transaction; they do not extend a standalone SELECT's protection until some later save. ROWLOCK is a hint, not a promise that escalation or deadlocks are impossible. EF's rowversion condition remains an optimistic concurrency safeguard.

## 6. Startup, logging and release

Read [Program.cs](../src/OrderManagement.Api/Program.cs) top to bottom: configuration -> DI/options -> optional migration mode -> ordered request pipeline -> endpoints. Inspect [EF mappings](../src/OrderManagement.Infrastructure/OrderDbContext.cs) for Identity setup, precision, constraints, indexes and historical relationships.

Inline comments distinguish scoped repositories from stateless singletons, Data Protection from JWT signing, authentication from antiforgery, liveness from readiness, static SPA fallback from API 404s, and safe exception classification from raw database error disclosure.

Then read [Serilog configuration](../src/OrderManagement.Api/Observability/LoggingConfiguration.cs), [Dockerfile](../Dockerfile), [Compose](../compose.yaml), [pipeline](../azure-pipelines.yml) and [Bicep](../infra/main.bicep). Comments explain structured fields, retention/batching, runtime DDL separation, image stages, non-root execution, persistent volumes, private release secrets, immutable images and slot settings. These are operational logs, not a transactional audit system.

## 7. Tests that verify the boundaries

[ApiFixture](../tests/OrderManagement.Tests/ApiFixture.cs) explains real SQL isolation, in-process HTTP, fake outbound email and explicit cookie/redirect handling. [LoggingTests](../tests/OrderManagement.Tests/LoggingTests.cs) explains asynchronous ingestion completion and the real HTTP receiver. [DataProtectionTests](../tests/OrderManagement.Tests/DataProtectionTests.cs) explains simulating a new replica/key cache.

Use the tests to demonstrate both success and failure. A passing CookieContainer or jsdom test does not prove every browser SameSite/rendering behavior; a compiled Bicep file does not prove a deployed Azure environment works.

Recommended first walkthrough: product administration, then product query, login, refresh, order creation/cancellation, and finally hosting/deployment. Keep this file as a map; the explanations themselves live beside the source code you are stepping through.
