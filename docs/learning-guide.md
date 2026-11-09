# Order Management: complete step-by-step learning and practice guide

This guide uses the code already in this repository. Work through it in order: run the app, trace one request, change a small feature, test it, then explain the result without looking at the code. You do not need to rebuild the whole application from scratch first.

For source-level reading, use the [commented code walkthrough](code-walkthrough.md). It links the annotated files in request order and suggests breakpoints and values to inspect.

For your ASP.NET experience level, spend less time on C# syntax and more time on boundaries, failure cases, browser security, database concurrency and deployment behavior. Angular and DevOps work can take longer initially; that is expected.

Use approximately 60–90 minutes per session: 15 minutes reading, 30–45 minutes doing the lab, 15 minutes debugging/testing, and 10 minutes explaining what you learned. Sessions are flexible units, not a promise that every topic fits one evening.

## Your learning route

| Stage | Sessions | What you should be able to demonstrate |
|---|---|---|
| Run and orient | 1–2 | Run the app and explain its four backend projects |
| API and persistence | 3–5 | Trace a query, validate a request, explain EF tracking and migrations |
| Identity and security | 6–9 | Explain cookies, CSRF, refresh rotation, policies and ownership |
| Business correctness | 10–12 | Demonstrate totals, stock rollback, rowversion and cancellation |
| Angular | 13–16 | Explain templates/forms/signals/RxJS and coordinated refresh |
| Observability and tests | 17–18 | Follow a trace across sinks and explain what tests prove |
| Containers and delivery | 19–22 | Run the full Docker stack and understand the Azure release path |
| Independent practice | 23–24 | Implement a small vertical feature and present the architecture |

If studying five evenings per week, plan roughly five to six weeks, then repeat the weaker labs. Move on when you can meet a session's checkpoint, not merely when you have read its files.

## First: understand the two run modes

| Item | Source/debug mode | Docker mode |
|---|---|---|
| Browser URL | https://localhost:4200 | https://localhost:8443 |
| API | https://localhost:7043 through Angular's proxy | /api on the same container origin |
| SQL | LocalDB: OrderManagementPractice | SQL container: OrderManagement |
| Account password location | .local/dev-secrets.json | .env: DEVELOPMENT_PASSWORD |
| Frontend updates | Angular development server reloads changes | Rebuild the image |
| Development mailbox | .local/mailbox | app-local Docker volume, /app/local/mailbox |
| Seq | Optional standalone service, http://localhost:5341 | Included, http://localhost:5341 |

**Use one app mode at a time while learning authentication. Cookies are scoped by host/path, not port.** Both modes use localhost and the same cookie names, so running both browser apps can overwrite their cookies even though the databases and signing keys differ. When switching, stop the other app, clear this application's localhost cookies in browser DevTools and sign in again.

The three seeded emails are admin@orders.local, manager@orders.local and customer@orders.local. Read the appropriate ignored password file locally; do not paste it into notes, screenshots, logs or Git. Existing account passwords are not reset by rerunning initialization.

## Session 1 — Run the application from source

Goal: establish a working baseline and learn which process does what.

From PowerShell:

```powershell
Set-Location 'D:\Angular ASP'
dotnet --version
node --version
dotnet tool restore
dotnet restore OrderManagement.sln
powershell -NoProfile -File scripts/Initialize-Development.ps1
dotnet dev-certs https --trust
New-Item -ItemType Directory -Force .certs
dotnet dev-certs https --export-path .certs/localhost.pem --format Pem --no-password
dotnet ef database update --project src/OrderManagement.Infrastructure --startup-project src/OrderManagement.Api
dotnet run --project src/OrderManagement.Api
```

If the Docker app is currently running, `docker compose stop app` stops that app without deleting its database. If LocalDB needs starting, use `SqlLocalDB start MSSQLLocalDB`.

In a second terminal:

```powershell
Set-Location 'D:\Angular ASP\frontend'
npm ci
npm start
```

Visit https://localhost:4200 and https://localhost:7043/swagger. Sign in with the seeded Customer account. Search the catalog and open Orders.

Expected: the catalog loads, authentication succeeds, and no browser security warning needs bypassing. The pinned .NET SDK is 6.0.428; the existing Node version used for verification was 22.18.0. The requested .NET 6 target is for practice and blocks production deployment; upgrading it is a later explicit task.

Write down: browser origin, API port, SQL instance/database and password-file location.

Checkpoint: you can start both processes without guessing a command or using the wrong environment's password.

## Session 2 — Read the architecture and dependency injection

Read in this order:

1. src/OrderManagement.Domain/Product.cs and Order.cs
2. src/OrderManagement.Application/Products.cs and Contracts.cs
3. src/OrderManagement.Infrastructure/OrderDbContext.cs and ProductRepository.cs
4. src/OrderManagement.Api/Controllers/ProductsController.cs
5. src/OrderManagement.Api/Program.cs

Build this request map in your notes:

```text
Browser → Angular HTTP client → /api/products controller
        → ProductService → IProductRepository → EF Core → SQL Server
        ← ProductDto / Page<ProductDto> ← projected SQL result
```

Explain each boundary: Domain describes business data; Application defines use cases/contracts; Infrastructure implements database access; API composes services and handles HTTP. The same scoped DbContext is injected into repositories and the Unit of Work within a request.

Lab: set breakpoints in ProductsController.List, ProductService.ListAsync and ProductRepository.ListAsync. Run the API under the IDE debugger, then search for a product. On an uncached request the breakpoints should follow that path. If a cache hit skips the repository, change the search term and explain why.

Checkpoint: explain why a singleton cannot safely hold this scoped repository. Also explain that EF already supplies repository-like DbSet behavior and Unit of Work through DbContext; these explicit patterns are deliberately small practice abstractions.

## Session 3 — API contracts, validation and HTTP responses

Read ProductQuery, ProductWrite, CreateOrderRequest and BusinessException, then the exception/status-code middleware in Program.cs.

Use these public reads:

```powershell
Invoke-RestMethod 'https://localhost:7043/api/products?pageNumber=1&pageSize=1&sort=price'
Invoke-RestMethod 'https://localhost:7043/api/health/ready'
```

In Swagger, request pageSize=0 and sort=untrusted-column. Request a product ID that does not exist. Inspect status and ProblemDetails rather than treating every failure as a generic exception.

| Response | Meaning in this project |
|---|---|
| 200 / 201 / 204 | Successful read/update, resource created, success without response body |
| 400 | Invalid model input or CSRF validation failure |
| 401 | No usable authenticated session |
| 403 | Authenticated caller lacks permission |
| 404 | Missing resource, or another customer's hidden order |
| 409 | Stale rowversion, insufficient stock or invalid transition |
| 500 / 503 | Unexpected failure / readiness unavailable |

Lab: add a `sku` sort to ProductQuery's allowlist, ProductRepository's switch and the catalog select options. Verify a valid request works and an arbitrary column is still rejected. Do not accept raw client-provided SQL/order-by text.

Checkpoint: explain why validation belongs on both frontend and backend, and why persistence entities should not be API request DTOs.

## Session 4 — EF Core mapping and read performance

Inspect OrderDbContext.OnModelCreating. Find the SKU unique index, decimal precision, rowversion columns, order/customer relationship, order-item uniqueness and stock/quantity constraints.

In SSMS, connect to `(localdb)\MSSQLLocalDB`, select OrderManagementPractice and run read-only queries:

```sql
SELECT Id, Name, Sku, Price, StockQuantity, IsActive FROM dbo.Products;
SELECT Id, CustomerId, Status, TotalAmount, CreatedAtUtc FROM dbo.Orders;
SELECT OrderId, ProductId, Quantity, UnitPrice FROM dbo.OrderItems;
```

Read ProductRepository.ListAsync. Identify filtering, CountAsync, stable sorting, Skip/Take, DTO projection, AsNoTracking and cancellation propagation. Read OrderRepository to see nested item projection without a separate request/query for every item. Admin role memberships are fetched in a batch.

Lab: compare generated SQL for a product query using EF's query inspection/debugger. Do not enable sensitive-data logging or log entire user/authentication objects.

Explain: no tracking reduces change-tracker work for read-only DTOs; projection limits selected data; stable sorting makes pagination predictable. Count and page are separate reads and may see concurrent changes.

Checkpoint: distinguish read-query efficiency from the deliberate per-product locking operations inside an order write transaction.

## Session 5 — Migrations and schema evolution

Read the InitialFoundation, SecureSessions and PersistDataProtection migrations plus the model snapshot.

Generate a SQL script for review without changing the database:

```powershell
dotnet ef migrations script --idempotent --project src/OrderManagement.Infrastructure --startup-project src/OrderManagement.Api --output .local/schema.sql
```

Open the script locally. Locate indexes, constraints, refresh tables and the DataProtectionKeys table.

Lab: in a separate practice copy or Git branch, add a nullable Product field named Manufacturer with a length limit. Update its mapping, create a migration and inspect it before applying. Decide deliberately whether it belongs in each DTO/UI. Restore the branch if you do not want to keep the exercise. Never delete migration history or use EnsureCreated as a migration substitute.

Checkpoint: explain expand/contract changes and why a destructive migration can prevent a slot-swap rollback.

## Session 6 — Identity registration, confirmation and lockout

Read AuthController, SeedData and the Identity registrations in Program.cs.

Lab:

1. Register a new practice email through the UI with a policy-compliant password.
2. Attempt sign-in before confirmation; expect a safe generic failure.
3. Open its confirmation message in the private development mailbox and follow the link.
4. Confirm and sign in; inspect /api/auth/me in browser Network.
5. As Admin, find the user and verify public registration assigned only Customer.

Identity supplies password hashing, password policy, lockout, roles and recovery token providers. SignInManager.CheckPasswordSignInAsync verifies the password without issuing a competing Identity application login cookie.

Use the isolated lockout test to inspect repeated-failure behavior instead of locking your main practice account unintentionally:

```powershell
dotnet test tests/OrderManagement.Tests --filter 'FullyQualifiedName~Repeated_password_failures'
```

Checkpoint: explain the difference between an Identity user record, a role membership and a signed JWT claim.

## Session 7 — Cookie JWTs and CSRF

Read docs/authentication.md, AuthController.Csrf and TokenService's cookie/token methods.

In browser DevTools, observe the sign-in sequence and cookie flags. Authentication tokens are HttpOnly/Secure. XSRF-TOKEN is intentionally readable, while the internal antiforgery cookie is HttpOnly. Do not copy raw token values into notes.

Draw the flow:

```text
GET csrf → readable CSRF request cookie
POST login + X-XSRF-TOKEN → HttpOnly access and refresh cookies
GET csrf after login → token bound to the authenticated identity
GET me → user details and permissions, without exposing JWTs
```

Run the focused check:

```powershell
dotnet test tests/OrderManagement.Tests --filter 'FullyQualifiedName~Login_cookie_flags'
```

Explain the expected missing-CSRF 400 and normal anonymous-me 401. Swagger writes also need the cookie/header combination; use the Angular UI or supplied scripts for the first authenticated labs.

Checkpoint: explain why HttpOnly is not CSRF protection, why Secure requires HTTPS, and why logout deletes cookies with matching paths. Do not replace these controls with localStorage tokens to simplify debugging.

## Session 8 — Refresh rotation and token-family reuse

Read TokenService.RotateAsync, RefreshFamily/RefreshSession and the tests containing Rotation, Concurrent_refresh and Logout.

Follow the exact transaction: hash the random cookie token, identify its family, lock the family, re-read the token, validate state, consume it, insert its successor, save and commit. A replay commits family revocation before returning failure.

```powershell
dotnet test tests/OrderManagement.Tests --filter 'FullyQualifiedName~refresh|FullyQualifiedName~Rotation|FullyQualifiedName~Logout'
```

Browser lab: sign in, remove only oms-access in DevTools, then load Orders. Observe the shared refresh sequence. Use tests for simultaneous/replayed tokens rather than pasting live tokens into a request file.

Read-only SQL exercise: inspect metadata without displaying hashes or raw tokens:

```sql
SELECT FamilyId, ExpiresAtUtc, ConsumedAtUtc, LEN(TokenHash) AS HashLength
FROM dbo.RefreshSessions;
SELECT Id, ExpiresAtUtc, RevokedAtUtc FROM dbo.RefreshFamilies;
```

Checkpoint: explain why refresh hashes must be retained for reuse detection, why a lost response can make a strict retry revoke the family, and why an already issued access token can remain valid until expiry after logout. This app does not coordinate refresh across independent browser tabs.

## Session 9 — Roles, policies and ownership

Read Permissions.For, PermissionHandler, OrderAccessHandler, AdminController and OrdersController.Accessible.

| Operation | Customer | Manager | Admin |
|---|---|---|---|
| Browse/create own orders | Yes | Yes | Yes |
| Edit own Draft items | Yes | Yes | Yes |
| Manage products/status, view all orders | No | Yes | Yes |
| Edit another customer's Draft items | No | No | Yes |
| Assign roles/lock users | No | No | Yes |

Lab: use separate browser profiles for Customer and Admin/Manager so their cookies do not overwrite each other. Register and confirm a second Customer, create an order for the first, then try the first order's URL as the second. Expect 404. A Manager can read all orders but cannot open Users management or edit another customer's items. A Customer cannot create a product or advance status.

Role changes/locks revoke refresh sessions. Existing access claims can last until token expiry; sign out/in when checking changed privileges. UI visibility alone is never evidence of backend enforcement.

Checkpoint: explain role-based authorization, permission claims, policy requirements and resource ownership as separate decisions.

## Session 10 — Product administration and concurrency

Read ProductAdminService, product-admin.component.html and its products.ts class.

Create two products in the UI as Manager/Admin (use unique SKUs if repeating):

| Product | SKU | Price | Available stock |
|---|---|---|---|
| Lab Keyboard | LAB-KEY-001 | 100.00 | 10 |
| Lab Hub | LAB-HUB-001 | 50.00 | 20 |

Verify negative stock, invalid SKU and excessive currency decimal places are rejected. SQL uniqueness/constraints provide a final guard after request validation.

Concurrency lab: load the same product for editing in two independent sessions. Save a change in A. Save B with its old rowVersion. Expect 409, then reload before retrying. Rowversion is a database-generated binary concurrency value, not a timestamp or date.

Checkpoint: explain why checking the client version and allowing EF's rowversion condition on SaveChanges address different moments in the race.

## Session 11 — Server pricing, stock and cancellation

Read OrderService and both repositories. Use the two lab products above with the known prices/stock.

| Action | Expected order total | Keyboard stock | Hub stock |
|---|---|---|---|
| Create Draft: 2 keyboards + 3 hubs | 350.00 | 8 | 17 |
| Change keyboards to quantity 3 | 450.00 | 7 | 17 |
| Remove the hub line | 300.00 | 7 | 20 |
| Cancel | 300.00 historical total | 10 | 20 |
| Repeat cancellation in the test | unchanged | 10 | 20 |

Cancellation preserves historical items/total; it changes status and restores reserved stock. The final line cannot be removed: cancel the order instead. Quantities cannot be zero/negative. Draft creation reserves stock immediately.

Separate price-snapshot lab: create another Draft with a keyboard at 100. Change the product's current price to 120 as Manager. Increasing the existing order line to quantity 3 must still total 300, because its snapshot price remains 100. A new order should use 120.

Cancellation is allowed from Draft/Confirmed; only staff can advance Draft → Confirmed → Shipped → Completed. Item editing stops when the order leaves Draft. After recording a historical order, deactivate its product and verify that the order is still readable.

Checkpoint: explain the transaction boundary and why the client submits product IDs/quantities rather than trusted unit prices/totals.

## Session 12 — Rollback, locking and races

Read the tests before running them:

```powershell
dotnet test tests/OrderManagement.Tests --filter 'FullyQualifiedName~Invalid_quantities|FullyQualifiedName~Concurrent_last_stock|FullyQualifiedName~Nested_add_remove|FullyQualifiedName~Stale_rowversion'
```

For rollback, the suite attempts an order containing one available and one unavailable product. Expected: no order/items committed and the available product's stock unchanged. For the last unit, two callers compete: exactly one creation, one conflict and final stock zero. Concurrent cancellation must restore stock once.

Draw two transaction timelines. Mark the parent/product update locks, rowversion comparison, SaveChanges and commit. Serializable isolation protects the transaction's read/write assumptions; optimistic concurrency rejects stale persisted versions. A remaining deadlock is surfaced as 409, not automatically replayed as a possibly duplicate order.

Checkpoint: explain why EF InMemory cannot prove these SQL Server behaviors. Also explain why making every transaction longer or serializable by default is not a general performance solution.

## Session 13 — Angular standalone components and external templates

Read app.config.ts, app.ts/app.component.html, then the catalog products.ts/products.component.html pair.

Identify imports, injected services, signals/computed values, event handlers and template bindings. The component class handles state/behavior; its templateUrl points to separate HTML. Shared feedback and pager components also have separate templates. Global styling is in src/styles.css.

Practice generation without creating files:

```powershell
Set-Location 'D:\Angular ASP\frontend'
npx ng generate component features/practice/status-badge --dry-run --skip-tests
```

Expected: component TS, external HTML and stylesheet names, using the configured component suffix. When ready, repeat without --dry-run in your practice branch and display an order status through a typed input. Do not add unrelated dependencies.

Lab: change a catalog empty-state message in the HTML only. Add an accessible stock-status badge; its logic belongs in TS/computed state when it becomes more than a simple presentation expression.

Checkpoint: explain interpolation, property binding, event binding, @if/@for and why stable tracking keys matter.

## Session 14 — Typed reactive forms and validation

Read account.ts/account.component.html and admin/products.ts/product-admin.component.html. Find typed FormControl/FormGroup declarations, Validators and strongPassword.

Lab: submit blank fields, invalid email, weak new password, malformed SKU, invalid currency and negative stock. Check useful validation feedback and disabled/busy states. Compare frontend validation with server validation; a modified browser request must still be rejected by the server.

Add an optional confirmation-password control to registration/reset/change-password in a practice branch. Write a group validator that requires equality. Do not send the confirmation field as a business/security requirement to the API; the backend validates the actual new password independently.

Checkpoint: explain why login should not apply a newly changed password-strength rule to an existing password, while registration/password changes should enforce the server's current policy.

## Session 15 — Signals, RxJS, search and component communication

Read the catalog search pipeline, Orders list and shared/ui.ts.

| Construct | Purpose here |
|---|---|
| signal / computed | Local state and derived UI values |
| debounceTime | Wait for a short pause in search input |
| distinctUntilChanged | Avoid repeating the same input |
| switchMap | Replace a superseded asynchronous query |
| catchError | Show a recoverable request failure |
| takeUntilDestroyed | Release component subscriptions |
| input / output | Pass pager state in and selected page out |

Lab: open browser Network, type a product search quickly, change sort, then page. Explain each request parameter and which pending result should be ignored/replaced. Stop the API, search again, observe the error state, restart and retry.

Add the allowlisted SKU sort from Session 3 to both backend and UI if not already done. Confirm search/filter changes reset pagination rather than remaining on an invalid later page.

Checkpoint: explain when signals are appropriate and why an asynchronous HTTP workflow still benefits from RxJS.

## Session 16 — Authentication restoration, guards and interceptor coordination

Read core/auth.ts, core/http.ts, core/guards.ts, core/auth.spec.ts and provideAppInitializer in app.config.ts.

Trace startup: current-user restoration finishes before protected route activation. Angular learns user details from /me, never by reading the HttpOnly JWT. Guards guide navigation; backend authorization still decides access.

Trace expired access: eligible 401 → shared refresh → update user/session epoch → retry original request once. Auth endpoints are excluded, 403 never refreshes, and failure clears client auth state. A late 401 from an older request must not start a second rotation. Retried unsafe requests need the current CSRF header.

```powershell
Set-Location 'D:\Angular ASP\frontend'
npm test
```

Read each assertion in the nine current tests. Change the test input before changing implementation; predict what will fail. The trusted-relative /api rule must not send credentials/CSRF to arbitrary external URLs.

Checkpoint: explain shareReplay with refCount=false in this workflow, the session epoch, and why retrying every 401 indefinitely would be dangerous.

## Session 17 — Serilog and operational debugging

Read docs/logging.md, LoggingConfiguration.cs, Program.cs request logging and scripts/sql/Logging.sql.

Start with default source-mode console/file sinks. Cause a harmless validation or stale-version error, then correlate its trace ID with the request log. Observe structured properties, not just rendered text. No request body, cookies, password, token or recovery link should be logged.

To practice all sinks in source mode, start just Seq and provision the LocalDB logging table:

```powershell
Set-Location 'D:\Angular ASP'
powershell -NoProfile -File scripts/Initialize-Docker.ps1
docker compose up --detach seq
dotnet run --project src/OrderManagement.Api -- --migrate
$env:Observability__Seq__Enabled = 'true'
$env:Observability__SqlServer__Enabled = 'true'
$env:ConnectionStrings__Logs = 'Server=(localdb)\MSSQLLocalDB;Database=OrderManagementPractice;Trusted_Connection=True;TrustServerCertificate=True'
dotnet run --project src/OrderManagement.Api
```

Stop the normal API process before the migration command/restart. Open http://localhost:5341 with admin and the generated SEQ_PASSWORD from .env. Make API requests and wait for batched sinks. In LocalDB:

```sql
SELECT TOP (20) Id, TimeStamp, Level, Message FROM dbo.ApplicationLogs ORDER BY Id DESC;
```

File output normally lives under the API process's logs directory. Docker uses /app/logs; Azure uses its configured /home/LogFiles path. Retention limits files, while SQL/Seq need their own policies. Raw exception-bearing events are excluded; the API logs safe type/trace summaries. The logger is not a universal secret redactor.

After the exercise, close that terminal or remove the environment overrides before unrelated runs. Do not copy .env values into appsettings.json.

Checkpoint: explain batching, log loss/outage tradeoffs, and why these sinks do not constitute a transactional audit system.

## Session 18 — Read the test suite as a specification

Study ApiFixture.cs, BrowserClient, SecurityAndOrdersTests.cs, LoggingTests.cs and DataProtectionTests.cs.

```powershell
Set-Location 'D:\Angular ASP'
dotnet test OrderManagement.sln
```

The current baseline is 20 backend tests and 9 frontend tests. Future feature work may increase those counts; the assertions and outcomes matter more than the number. Backend fixtures use uniquely named real LocalDB databases and remove only those databases. They preserve your practice database.

Explain arrange/act/assert, isolated fixtures, cookie flags/path behavior, CSRF setup and why HTTP 200 alone is not enough for a correctness test. The logging test checks real SQL insertion and a Seq-compatible receiver; the Data Protection test verifies encrypted SQL key persistence across service providers.

Lab: add a focused behavior test before adding a rule such as “a product SKU cannot change after it is referenced by an order.” Decide whether you actually want that rule before changing implementation. Prefer behavior tests over tests that merely repeat method internals.

Checkpoint: distinguish SQL provider tests, simulated browser cookie behavior, jsdom tests, actual browser UI checks and deployed environment checks. None substitutes for all the others.

## Session 19 — Dockerfile and the same-origin image

Stop the source-mode API/browser app to avoid cookie confusion. Read Dockerfile and .dockerignore.

Explain the three stages: Node builds Angular; .NET SDK publishes the API; the final ASP.NET runtime serves both outputs as UID 10001. The final image contains neither the SDK nor a Node development server. Understand why credentials, certificates, node_modules, bin/obj and local overrides are excluded from build context.

```powershell
Set-Location 'D:\Angular ASP'
powershell -NoProfile -File scripts/Initialize-Docker.ps1
docker compose config --quiet
docker compose up --build --detach --wait --wait-timeout 240
docker compose ps
powershell -NoProfile -File scripts/Verify-Container.ps1
```

Open https://localhost:8443 and use the .env DEVELOPMENT_PASSWORD. This is a separate database from LocalDB. Change a template, rebuild and verify that containerized output changes; a source edit does not hot-reload this image.

Checkpoint: explain image versus container, build-time versus runtime configuration, port mapping and why the static frontend contains no JWT signing secret or SQL connection string.

## Session 20 — Compose, migrations, volumes and restart behavior

Read compose.yaml and the --migrate/--health-check paths in Program.cs and Deployment/ContainerHealthCheck.cs.

Draw the startup dependencies: SQL healthy → migration completes → app starts; Seq also starts for ingestion. The one-shot migration service exiting successfully is expected. Readiness checks database reachability and migration state, not merely that a process exists.

Lab:

1. Create and cancel a practice order in Docker.
2. Run `docker compose restart app`; sign in again only if your session requires it, then verify order history persists.
3. Run `docker compose stop`, then `docker compose start`; verify volumes still contain your data.
4. Inspect UID and file output with `docker compose exec app id` and the Docker Desktop volume/file view.

Do not add --volumes to cleanup unless deliberately discarding data. SQL Server Developer in Compose is for development/CI, not a production license/deployment design. The container's Development key files/mailbox persist in app-local. Production instead uses certificate-encrypted SQL Data Protection keys.

Checkpoint: explain why every replica should not independently migrate the database during startup and why a container filesystem alone is insufficient persistence.

## Session 21 — Read and practice CI before release

Read azure-pipelines.yml with deployAzure=false. Identify:

1. Windows LocalDB backend tests and TRX publication.
2. Linux Angular install/tests/build.
3. Complete Docker build/start/HTTPS smoke test.
4. Vulnerability-report artifact.
5. Disposal of only the CI project's temporary volumes.

Reproduce the test/build commands locally. Then connect this repository to your Azure DevOps pipeline when your organization/project is available, leaving deployment disabled. Configure branch policies to require validation/review. A locally parsed YAML file does not prove service connections, agent capabilities or a real pipeline run.

Read the production-only portion: supported-framework gate, enforced vulnerability check, immutable commit tag, ACR push, private release agent, Key Vault migration secrets, single migration execution, staging verification, swap and production verification.

Checkpoint: explain artifact promotion, secret boundaries and why the image tested in CI should be the one deployed.

## Session 22 — Azure architecture, Bicep and production boundaries

Read docs/azure-devops.md, infra/main.bicep, its example parameters, ProductionConfiguration.cs and ProxyConfiguration.cs.

The template prepares App Service/ACR/staging/identities and consumes an existing landing-zone network, Key Vault and Azure SQL. It does not create your SQL/private DNS/firewall design. The runbook lists exact prerequisites, roles, secrets and variable-group names. Use the example as a template, not deployable production values.

First lab is local review only:

```powershell
az bicep build --file infra/main.bicep
powershell -NoProfile -File scripts/Check-ProductionReadiness.ps1
```

Azure CLI is needed for the first command. The readiness command currently **must fail** because net6.0 is intentionally retained. A failure here is the expected safeguard. The framework upgrade must update SDK, packages, EF tool and image versions together, with regression/security verification.

With an authorized Azure subscription and completed parameters, do a what-if review before provisioning. The App Service plan/resources cost money; do not use a deployment command merely to read the template. Keep deployAzure=false until the framework and environment checks pass.

Explain: managed-identity ACR pulls, Key Vault references for both slots, separate runtime/DDL identities, trusted proxy configuration, SMTP STARTTLS, encrypted shared Data Protection keys and why PublicOrigin/secret settings are slot-specific. Explain why trusting all forwarded headers would let a caller influence security decisions.

Checkpoint: describe a staging failure and rollback without disabling TLS, bypassing readiness or reversing an incompatible migration automatically. No actual Azure deployment or production readiness is claimed by the current local verification.

## Session 23 — Build one feature independently

Choose one vertical slice. Recommended: **customer order note**.

Define the rule first: optional note, maximum 500 characters; owner may change it only in Draft; permitted Admin behavior must be explicit. Notes must not appear in normal logs. The server remains authoritative.

Implement incrementally:

1. Domain field and EF mapping/migration.
2. Dedicated request DTO and validation.
3. Repository/service operation using the current parent rowversion and transaction/ownership rules.
4. Protected endpoint with meaningful statuses.
5. Typed Angular API method, form control and external template.
6. Tests: own Draft success, non-owner rejection, non-Draft rejection, oversized input and stale version.
7. Update API examples/run instructions, build and run affected tests.

Keep the change focused. Do not introduce microservices, a message broker or a generic abstraction for one field. If you prefer a simpler first slice, add a SKU sort or product manufacturer as practiced earlier.

Checkpoint: implement and explain the whole flow without copying a controller/repository blindly.

## Session 24 — Architecture interview and demonstration

Prepare a 15-minute demonstration:

- 3 minutes: architecture/dependencies and one projected query.
- 3 minutes: Identity, cookie transport, CSRF and refresh rotation.
- 3 minutes: server totals, stock transaction, stale writes and cancellation.
- 3 minutes: Angular restoration, forms, RxJS and reusable components.
- 3 minutes: logs, SQL tests, Docker, deployment gates and remaining production work.

Answer aloud:

1. Why a modular monolith for this system?
2. What do explicit repositories/UoW add here, and what does EF already provide?
3. Why must all repositories share one scoped DbContext?
4. What happens when the third product in an order has insufficient stock?
5. Why are prices calculated by the server and snapshotted on items?
6. How do rowversion checks, update locks and isolation differ?
7. Why is cancellation idempotent, while order creation lacks an idempotency key?
8. Why are HttpOnly and CSRF separate concerns?
9. Why can a logged-out or demoted user's issued JWT remain valid temporarily?
10. Why must a 403 not trigger refresh?
11. What does shareReplay protect here, and what cross-tab problem remains?
12. Why do Angular guards not enforce data access?
13. Which tests require actual SQL Server, and which browser behaviors remain unproven?
14. What happens if Seq/SQL logging is unavailable?
15. How do shared encrypted Data Protection keys help replicas/slot swaps?
16. How do you roll back the application when the database has already migrated?
17. What specifically prevents calling the current .NET 6 build production-ready?

## Troubleshooting quick reference

| Symptom | First checks |
|---|---|
| Port already in use or DLL locked | Identify your source API/dev-server process, stop it normally, then rebuild; do not kill unrelated processes |
| Login succeeds in one mode but not another | Check LocalDB versus Docker password file/database; localhost cookies are shared across ports |
| Repeated CSRF 400 after switching mode | Stop the other app, clear application cookies, sign in again; check HTTPS and fresh identity-bound CSRF token |
| Login 401 | Correct password source, confirmed email, lockout and account state; inspect safe error/result rather than expecting token JSON |
| API 403 | Check backend roles/policy, not just hidden buttons; sign in again after privilege changes |
| Order 404 | Missing resource or ownership restriction; verify with the correct Customer/staff account |
| Product/order 409 | Reload rowversion, stock and current status; do not keep replaying stale input |
| Database/readiness failure | Correct instance/database, running LocalDB/SQL container, credentials and applied migrations |
| Docker app cannot read PFX | Check path, password, host file readability for UID 10001 and whether .env was regenerated without re-exporting |
| SQL/Seq logs appear late | Allow batching delay; verify enabled flags, endpoint, table provisioning and runtime permissions |
| Docker source edits have no effect | Rebuild with docker compose up --build; this image has no development hot reload |
| Pipeline production gate fails | Expected on net6.0; do the supported-framework upgrade instead of bypassing it |
| Azure redirect loop or antiforgery HTTPS failure | Verify forwarded scheme and only the legitimate immediate proxy ranges; never trust all proxies to make it pass |

## Keep a practice journal

For every session record: objective, files read, one change/request, expected outcome, actual outcome, test command/result, one failure investigated and a two-sentence explanation in your own words. Keep credentials/token values out of the journal.

Progress checklist:

- [ ] Sessions 1–5: run, trace, validate and inspect persistence.
- [ ] Sessions 6–9: explain Identity, cookie security, refresh and authorization.
- [ ] Sessions 10–12: demonstrate business correctness under failure/concurrency.
- [ ] Sessions 13–16: modify Angular confidently and explain request coordination.
- [ ] Sessions 17–18: correlate logs and justify test coverage.
- [ ] Sessions 19–22: run Docker and explain the release path and gates.
- [ ] Sessions 23–24: implement a vertical slice and present tradeoffs independently.

Reference order: README.md for starting, this guide for practice, authentication.md for security details, logging.md for sinks, azure-devops.md for deployment, api-examples.http for contracts, and verification.md for checks actually executed.
