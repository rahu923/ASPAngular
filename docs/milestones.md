# Milestones 2–6: implementation and practice

## 2. Identity, cookie JWTs and CSRF

Implemented in `Security/AuthSupport.cs`, `Controllers/AuthController.cs`, `Program.cs`, refresh entities and the SecureSessions migration. Identity owns password hashing/validation, lockout, confirmation/reset token providers, security stamps and role membership. SignInManager.CheckPasswordSignInAsync checks passwords without creating an application login cookie. The JWT scheme reads only the designated access cookie.

Before implementation, the selected design was documented in authentication.md: Secure HttpOnly authentication cookies protect token confidentiality from JavaScript, while ASP.NET antiforgery protects unsafe requests from browser cookie attachment across sites. These controls address different threats. Angular reads only the antiforgery request token.

Refresh families have an absolute lifetime and revocation timestamp; each random token has a unique SHA-256 hash, expiry and consumption timestamp. `sp_getapplock` serializes transactions per family. A successful rotation consumes the old token and saves its successor atomically. A replay commits family revocation before failing. Strict reuse detection also revokes the family if a client accidentally retries a consumed token after losing its first response; sign in again in that situation. Logout uses the same family lock. Password/role/lock changes revoke families and change the Identity security stamp, which refresh checks.

Run: initialize local settings, apply migrations, start the API and Angular using README commands. Register, open the private development mailbox link, confirm and sign in. SQL tests cover cookies, CSRF, login/lockout, recovery, rotation races, expiry/replay and revocation.

Exercise: remove only the access cookie, make two protected API requests, and explain the sequence of CSRF → refresh → CSRF → current-user calls.

Interview: Why is HttpOnly insufficient for CSRF? Why must replay revocation be committed on a failed request? Why can an issued JWT remain usable after logout?

## 3. Product administration and authorization

Implemented public/admin query endpoints, create/update/deactivate, price/stock constraints, stale-write checks, permission claims, custom policy handlers, Admin role endpoints and a permission directive. Roles map to permissions when a JWT is issued. A 403 is an authorization failure and cannot be fixed by refreshing an unchanged session. Admin role/lock changes revoke future refreshes; existing access claims last at most five minutes.

Catalog cache entries have bounded size and 30-second expiration. A version counter invalidates queries after product or stock mutations, preventing an older in-flight read from repopulating the current generation. This cache is per process; distributed invalidation would be needed for multiple API instances.

Run: sign in as Manager and use Products admin. Use a Customer account to verify the route guard and backend 403. Change a product after loading it in two sessions to produce a rowversion conflict.

Exercise: add a new allowlisted public sort option consistently in API validation, SQL projection and UI.

Interview: What does EF tracking add on a read-only page? Why is a claim trusted only after JWT validation? Why doesn't hiding the Users link enforce Admin access?

## 4. Orders and nested items

Implemented application service transaction boundaries, SQL update locks, server pricing, unit-price snapshots, stock reservation, Draft-only item edits, transitions and idempotent cancellation. Ownership is enforced both by a resource authorization handler and by application-service checks on writes. Listing filters the user before pagination. Non-owned resource requests return 404 to avoid disclosing another customer's order.

Use decimal server totals and UTC timestamps. A relational foreign key restricts product deletion; deactivation never removes historical items. Serializable transactions roll back all work if any product lacks stock. Locks reduce read-to-write deadlocks, while SQL Server rowversions prevent overwriting newer persisted state. A remaining deadlock returns 409 rather than replaying an unknown order creation automatically.

Run: create a Draft as Customer, edit quantities, add/remove a line and cancel. Sign in as Manager to confirm, ship and complete an order. Run the SQL suite for last-unit races and simultaneous cancellation.

Exercise: use two clients to order the last stock unit; verify one success, one conflict and stock zero.

Interview: Why must cancellation lock the parent as well as products? Why keep a unit price on OrderItem? What differs between optimistic concurrency and transaction isolation?

## 5. Angular workflows

Implemented lazy standalone account/admin/order components, route guards, reusable feedback/pager components, a permission directive, typed reactive forms, a custom password validator, and typed product/order/auth contracts. Signals/computed values represent local state; RxJS switchMap cancels superseded search results, debounceTime and distinctUntilChanged reduce requests, and takeUntilDestroyed cleans up subscriptions.

App initialization fetches current user before guarded routing. The trusted-relative-API interceptor attaches credentials and CSRF only to `/api/` requests. Refresh is shared with `shareReplay` and continues if an individual subscriber navigates away. Each failed request retries once; a session epoch prevents a delayed response from the old access token causing a second refresh. Auth endpoints are excluded, 403 never refreshes, and failure clears user state. Retry reconstructs the antiforgery header after the identity changes. This implementation does not support cross-tab refresh coordination; concurrent tabs can trigger strict family reuse detection and require sign-in again.

Run: `npm ci`, `npm start`; use all account, catalog, order and admin routes. Run `npm test` for auth restoration, guard permissions, shared refresh, retry limit, late 401, trusted origin scope and the custom validator.

Exercise: add a component using the pager's output and explain why its page state belongs to the parent.

Interview: What happens if refCount cancels a refresh halfway through rotation? Why is a guard not a backend authorization boundary? What does switchMap unsubscribe from?

## 6. Focused verification

The xUnit WebApplicationFactory suite uses the actual SQL Server provider and a unique temporary LocalDB database; it never uses EF InMemory. HTTPS cookie behavior is represented with a CookieContainer. Tests cover authentication, CSRF, JWT validation, refresh races/replay/revocation, role/policy/resource checks, Identity recovery/lockout, totals/snapshots, item changes, rollback, stale versions, last-unit races and repeated/concurrent cancellation. Vitest runs through Angular's unit-test builder with HTTP testing providers.

Run `dotnet test OrderManagement.sln`, `npm test`, `npm run build`, and the two PowerShell smoke scripts from README. `Verify-ProxySession.ps1` also checks actual HTTPS cookies through Angular's development proxy and creates/cancels one practice order. Generated test databases are deleted only after matching the fixture's unique name. The practice database remains intact.

Exercise: deliberately remove a stock or ownership check, run the tests and identify which behavior fails; restore the check afterward.

Interview: Why can't InMemory prove rollback or SQL Server rowversion correctness? What remains unproven by WebApplicationFactory? Which browser and deployment checks are still needed?

See verification.md for executed results. Browser UI automation was blocked before any interaction, so layout and manual click-through remain explicit verification limits.
