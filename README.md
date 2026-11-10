# Order Management practice lab

A modular monolith for hands-on .NET and Angular practice. Angular templates are now external `.component.html` files. See [Docker and Azure DevOps](docs/azure-devops.md) and [Serilog logging](docs/logging.md) for the container stack, four sinks, deployment pipeline and production gates. **Milestones 1–6 are implemented**, with focused SQL Server integration tests and Angular authentication tests. This is a learning application, not a production-ready system. Browser automation was blocked before interaction; the HTTPS development proxy workflow was verified by a repeatable HTTP script.

## Start locally

For an ordered learning path with 24 hands-on sessions, exercises, expected results, troubleshooting and interview practice, start with the [complete learning guide](docs/learning-guide.md).

Prerequisites: .NET SDK **10.0.403**, ASP.NET runtime **10.0.14**, Node **22.18.0** (or another version in Angular 21's supported range), npm, and SQL Server LocalDB. `global.json` selects the .NET 10 SDK feature band with patch roll-forward. All backend/test projects target .NET 10; EF Core, Identity, JWT packages and the local EF tool use 10.0.14. See [upgrade notes](docs/dotnet-10-upgrade.md) before updating an existing database.

Run in PowerShell from `D:\Angular ASP`:

```powershell
powershell -NoProfile -File scripts/Initialize-Development.ps1
dotnet restore OrderManagement.sln
dotnet tool restore
dotnet dev-certs https --trust
New-Item -ItemType Directory -Force .certs
dotnet dev-certs https --export-path .certs/localhost.pem --format Pem --no-password
dotnet ef database update --project src/OrderManagement.Infrastructure --startup-project src/OrderManagement.Api
dotnet run --project src/OrderManagement.Api
```

In a second terminal:

```powershell
Set-Location 'D:\Angular ASP\frontend'
npm ci
npm start
```

Open **https://localhost:4200**. Swagger is **https://localhost:7043/swagger**. Both browser and API use HTTPS. The ignored `.certs/localhost.pem` and `.certs/localhost.key` contain local development certificate material only. Do not distribute them. Trusting the development certificate can require an OS consent prompt.

Development accounts (email already confirmed):

| Account | Role |
|---|---|
| admin@orders.local | Admin |
| manager@orders.local | Manager |
| customer@orders.local | Customer |

Open the ignored **`.local/dev-secrets.json` locally** for the generated development password. It is not printed by the initializer and is never committed. The same generated password is used for these three local practice accounts. Seeding creates missing accounts; rerunning the initializer does not reset existing passwords. Account seeding is Development-only. The API also generates an ignored development signing key at `.local/jwt-signing-key` when none is configured. Development Data Protection keys persist under `.local/keys`; protect these files as credentials. None of these local defaults are used for production account seeding.

The default SQL connection uses Windows authentication: `(localdb)\MSSQLLocalDB`, database **OrderManagementPractice**. Start LocalDB with `SqlLocalDB start MSSQLLocalDB` if needed. For another SQL Server:

```powershell
$env:ConnectionStrings__Orders = 'Server=localhost;Database=OrderManagementPractice;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
```

Set it before migrations and API startup. Alternatively copy `docs/appsettings.Local.example.json` to `src/OrderManagement.Api/appsettings.Local.json`, which is ignored. That local file is loaded after normal configuration and takes precedence, so use one override method. `TrustServerCertificate=True` is a development convenience; use properly trusted server certificates in deployment.

## What is implemented

- **Identity:** registration (always Customer), email confirmation/resend, login, lockout, password change, forgot/reset password, current user, logout, Admin user search, role assignment, account lock/unlock, role listing.
- **Cookie JWT authentication:** Secure HttpOnly access/refresh cookies, issuer/audience/signature/expiry validation, random refresh tokens stored only as hashes, atomic rotation, expiry/revocation/reuse detection, and logout cookie deletion with matching paths.
- **CSRF:** ASP.NET antiforgery cookie/request token and header validation for every unsafe endpoint, including registration, login, refresh, recovery and logout.
- **Authorization:** role-based Admin endpoints; permission claims and policies (`ManageProducts`, `ViewAllOrders`, `ManageOrderStatus`); custom requirement handlers; resource-based order ownership. Cross-customer order access returns 404.
- **Products:** create/update/deactivate, active public listing/detail, admin listing including inactive products, pagination/search/allowlisted sorting, DTO projections, rowversion conflicts, bounded 30-second public query caching and invalidation on catalog/stock writes.
- **Orders and nested items:** create/list/detail, item list/add/update/remove, cancellation and controlled transitions, ownership, server prices and totals, immutable item unit-price snapshots, atomic stock reservation/restoration, SQL update locks and rowversion checks.
- **Angular:** standalone components, lazy features, startup auth restoration, functional guards/interceptor, one shared refresh request, at-most-one retry, trusted `/api/` credential/CSRF scope, typed APIs/forms, password validator, signals/computed state, RxJS search and cleanup, reusable feedback/pager components and a permission directive.

## Development email

Confirmation and reset links are written as private JSON files under ignored **`.local/mailbox`** in Development. Open the most recent file locally and follow its `link`. Recovery tokens are in the URL fragment rather than the HTTP request path; Angular captures and removes that fragment from history, then submits the token in the CSRF-protected request body. Tokens are never returned by normal API responses or logged. The API does not expose a mailbox endpoint.

Outside Development, SMTP uses mandatory STARTTLS and validated settings (`Smtp__Host`, `Smtp__Port`, `Smtp__From`, `Smtp__Username`, secret `Smtp__Password`). Configure and verify delivery with your provider. Production Data Protection keys persist in SQL and require a private encryption certificate supplied securely. Identity confirmation/reset tokens expire after two hours. See [Azure deployment](docs/azure-devops.md).

## Order rules

Creation reserves stock and creates a **Draft**. Only Draft orders allow item changes. Existing lines keep their original unit price when quantity changes; newly added lines snapshot the current price. Quantities are positive integers. Duplicate products are rejected. A Draft cannot have its final line removed; cancel it instead.

Transitions: **Draft → Confirmed → Shipped → Completed** require `ManageOrderStatus`. Draft or Confirmed can become Cancelled by the owner or permitted staff. Cancelled and Completed are terminal. Repeating cancellation is idempotent, even with the original rowversion, and never restores stock twice. Customers cannot confirm/ship/complete an order.

Every order mutation runs in a SQL Server Serializable transaction. Update locks protect the parent order and affected products; rowversion checks reject stale clients, and SQL constraints prevent negative stock. Product locks are acquired in ID order during creation/cancellation. Remaining deadlocks or concurrency conflicts return 409, with rollback; reload before retrying. No automatic transaction replay or order-creation idempotency key is implemented: retry a successful/unknown creation carefully. Historical orders remain readable after product deactivation.

## Architecture and lifetimes

```text
src/
  OrderManagement.Domain/          Products, Orders, OrderItems; no EF dependency
  OrderManagement.Application/     DTOs, validation, contracts, product/order services
  OrderManagement.Infrastructure/  SQL Server context, Identity schema, repositories, migrations, email
  OrderManagement.Api/             HTTP endpoints, security orchestration, policies, middleware, DI
frontend/src/app/
  core/                            auth, guards, functional interceptor and tests
  shared/                          feedback, pager, permission directive
  features/account/                login/registration/recovery/password pages
  features/products/               public catalog and typed product API
  features/orders/                 creation, history, detail and nested item operations
  features/admin/                  product/user/role administration
```

Application depends on Domain; Infrastructure depends on Application; API composes them. Auth orchestration lives in the API security layer and delegates password/token-provider operations to Identity. Business controllers delegate rules to application services. `DbSet` already behaves like a repository; `DbContext.SaveChanges` already behaves like Unit of Work. Explicit contracts here are for practice, without a generic repository or duplicate contexts. Repositories do not save; business services control the scoped Unit of Work and transactions. Authentication uses its own context transaction boundaries.

DbContext, repositories, UoW, services and TokenService are scoped. Stateless authorization handlers and the bounded catalog cache are singletons. Singleton services never capture a scoped DbContext. Read projections use `AsNoTracking`; order items are projected with the parent query, and admin role memberships are batched rather than fetched once per user. Count and page reads may observe concurrent updates; this is acceptable for browse screens. Currency uses decimal(18,2); timestamps are UTC and rendered in the browser's local time.

Middleware order: forwarded headers → exception handler → response headers → development Swagger/production HSTS → HTTPS → routing → authentication → authorization → antiforgery validation → endpoints. An anonymous protected GET receives 401; insufficient privileges receive 403; a hidden/not-owned order receives 404; stale writes or invalid transitions receive 409; validation/CSRF errors receive 400. JWT auth has no HTML login redirects. Swagger documents the API but its Try it out writes also require the antiforgery cookie/header; use the UI or verification scripts for authenticated flows.

## Permission matrix

| Capability | Customer | Manager | Admin |
|---|---|---|---|
| Browse products and create/view own orders | Yes | Yes | Yes |
| Edit own Draft items | Yes | Yes | Yes |
| Manage products | No | Yes | Yes |
| View all orders and manage status | No | Yes | Yes |
| Edit another customer's Draft items | No | No | Yes |
| Assign predefined roles and lock/unlock users | No | No | Yes |

The three predefined roles are managed through RoleManager; arbitrary role creation/deletion is intentionally excluded because policy behavior is defined for these roles. Users register through the public registration flow; admin management lists users, assigns a role, and locks/unlocks accounts. Admins cannot lock themselves or change their own role through this API. Backend authorization is authoritative; Angular guards and hidden controls only guide navigation.

## Verification commands

```powershell
dotnet test OrderManagement.sln
powershell -NoProfile -File scripts/Smoke-Test.ps1
powershell -NoProfile -File scripts/Verify-ProxySession.ps1
Set-Location frontend
npm test
npm run build
```

The integration fixture creates a unique **OrderManagementTests_<guid>** LocalDB database, migrates it and deletes only that database afterward. No EF InMemory provider is used. Tests require LocalDB access and permission to create/drop a dedicated test database. The proxy script needs both servers and seeded development accounts; it creates then cancels one practice order. See [verification](docs/verification.md) for actual results and limitations.

## Browser walkthrough

1. Sign in as Customer; reload and verify your account and order navigation restore without reading the JWT in JavaScript.
2. Create an order, change Draft quantities, add/remove another product, inspect totals, and cancel. Check that stock returns once.
3. Register another Customer, follow the development mailbox confirmation link, then try accessing the first customer's order URL. Expect a hidden order.
4. Sign in as Manager, manage products, and advance a Draft to Confirmed and Shipped. Customer item editing must no longer work. Users admin is denied.
5. Sign in as Admin, search users, assign a predefined role and lock/unlock an account. Existing access cookies remain valid until expiry; refresh sessions are revoked.
6. Use forgot/reset password links and change-password. Sign in again after changing the password.
7. Inspect browser Network: unsafe `/api` requests include `X-XSRF-TOKEN`; access/refresh cookies are HttpOnly/Secure. With an expired or removed access cookie, concurrent protected requests should produce one refresh sequence. A 403 must never trigger refresh.

## Deployment boundaries

Prefer one HTTPS origin: serve the built `frontend/dist/orders/browser` assets with SPA fallback for client routes and proxy `/api` to ASP.NET. Preserve scheme/host correctly and configure forwarded headers only for known reverse proxies. The local Angular proxy's `secure:false` applies only to its development backend certificate; never use that setting in a deployment proxy. No wildcard credentialed CORS is configured. Separate-origin deployment requires an explicit origin allowlist, credentials, CSRF header allowance, a trusted-origin interceptor (Angular's built-in XSRF skips absolute cross-origin URLs), and appropriate cookies; see [authentication details](docs/authentication.md).

Supply `Jwt__SigningKey` (cryptographically random, at least 64 characters), `Jwt__Issuer`, `Jwt__Audience`, `PublicOrigin` (trusted frontend HTTPS origin), SQL credentials, SMTP settings and a private Data Protection encryption certificate. Protect configuration via environment/secrets, not source. Access cookie lifetime defaults to five minutes, refresh families to seven days absolute. Logout, password changes and role/lock changes revoke refresh sessions; already issued access tokens retain their signed permissions until expiry. Additional per-request revocation checks are not implemented. Add edge throttling, durable email delivery, operational monitoring, signing-key rotation, multi-instance cache invalidation before real use. The in-process catalog cache is enabled for Development and disabled by default in Production. See the deployment guide for the exact Azure configuration and framework gate.

## Learning milestones

See [milestone guide](docs/milestones.md) for each milestone's changes, run/check commands, exercise and interview questions. [API examples](docs/api-examples.http) list the routes and bodies. All migrations are included; do not replace them with EnsureCreated.

Compatibility: [Angular matrix](https://angular.dev/reference/versions), [.NET support](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [EF Core SQL Server 10.0.14](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.14). Pinned Angular/CLI/build 21.0.0, TypeScript 5.9.3, RxJS 7.8.2, EF/Identity/JWT/EF tool 10.0.14. Vitest 4.0.8 matches Angular 21 build's peer requirement. NuGet and npm installation commands are reproducible via restore/npm ci with the project manifests and lockfile.
"# ASPAngular" 
