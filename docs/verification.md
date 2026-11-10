# Verification report — milestones 1–6

## Current: .NET 10 upgrade (November 10, 2026)

Release build passed with zero warnings/errors; all 20 SQL integration tests passed. EF reports no pending model changes, NuGet audit reports no known vulnerable dependencies, and API publish/framework gate passed. Docker verification remains pending because the Linux engine is unavailable. See [upgrade notes](dotnet-10-upgrade.md).

## Historical .NET 6 baseline

The results below describe the original November 8 baseline and are superseded by the upgrade results above.


Executed on November 8, 2026 in the supplied Windows workspace.

## Latest: external templates, logging, Docker and Azure assets

Implemented 12 external `.component.html` templates and `templateUrl` references. Angular schematics now default to external templates/styles with the component suffix. No inline `template:` declarations remain in application source.

The updated backend suite passes **20 tests**, including console/file/SQL logging, Seq-compatible HTTP delivery, raw-exception exclusion, and certificate-encrypted SQL Data Protection keys surviving a new service provider. All **9 Angular tests** and the production build pass after template extraction.

The complete Docker image built successfully and Compose started SQL Server, Seq, the one-shot migration service and HTTPS app. The app and SQL report healthy. The container flow script passed SPA serving, SQL readiness, CSRF, seeded login, server-priced order creation/cancellation, refresh/logout and 401. The app runs as UID **10001**, a rolling JSON file exists, and a direct SQL check found **37** persisted log events at verification time. Generated credentials and private certificate files are ignored and excluded from the image.

The official Bicep compiler compiled infra/main.bicep without diagnostics. Compose and Azure Pipeline YAML parsed successfully. The framework production gate was executed and correctly rejected net6.0. Azure resources/service connections/secrets are not configured here; no cloud deployment, actual Azure Pipeline run, remote what-if, SMTP delivery or production proxy/network validation is claimed. Docker development uses its own SQL/volumes; the existing LocalDB database was preserved.

The original browser-tool limitation remains. See [Azure runbook](azure-devops.md) and [logging guide](logging.md) for setup, remaining production requirements and reproducible checks.

## Passed checks

- .NET SDK 6.0.428 / ASP.NET runtime 6.0.36, Node 22.18.0 and npm 10.9.3 checked. Angular compatibility was verified before installation.
- Four backend projects and the test project compile against net6.0. The clean backend build passed with zero warnings/errors; the integration-test command also rebuilds the final source.
- InitialFoundation and SecureSessions migrations generated and applied successfully to real SQL Server LocalDB, dedicated database OrderManagementPractice.
- SQL Server integration suite: **18 tests passed**, zero failures/skips. WebApplicationFactory uses an isolated uniquely named LocalDB database, applies actual migrations and removes only that generated database. No EF InMemory provider.
- Backend coverage: login success/failure and lockout; cookie flags and 401 behavior; valid/missing CSRF; JWT signature/issuer/audience/expiry; refresh hashing, rotation, concurrent reuse, family revocation, expiry and logout; role/policy/ownership checks; registration forced to Customer, confirmation and reset; Admin role/lock changes and password change; server totals/snapshots; nested item add/update/remove; stock rollback; stale rowversions; invalid transitions; concurrent last-unit reservations; repeated and concurrent cancellation; history after product deactivation.
- Angular tests: **9 passed**, covering current-user restoration, expired-session refresh failure, shared simultaneous refresh, late 401 handling, at-most-once retry, 403/auth exclusions, third-party credential/CSRF isolation, guards, and password validation.
- Final Angular production build passed with strict TypeScript/templates and all lazy features; initial bundle approximately 281.5 KB before compression.
- Original SQL-backed catalog smoke script passed for health/readiness, projections/search, empty results, validation, allowlisted sorting, 404 and pagination.
- HTTPS Angular proxy session script passed against the running app: CSRF, seeded Customer login, server-priced order creation, cancellation, rotation, logout and final 401. It created and cancelled one practice order, restoring reserved stock. No password or authentication token was printed.
- Existing development HTTPS certificate exported only to ignored .certs for Angular. Curl checks validated certificates normally; no browser security warning was bypassed.

## Commands

```powershell
dotnet test OrderManagement.sln
powershell -NoProfile -File scripts/Smoke-Test.ps1
powershell -NoProfile -File scripts/Verify-ProxySession.ps1
Set-Location frontend
npm test
npm run build
```

The smoke scripts require a running API; the proxy script also requires Angular and initialized development accounts. The integration suite requires LocalDB database creation/deletion permission. Stop the local API before rebuilding changed backend assemblies if Windows reports locked files.

## Explicit limits

Browser automation stopped before interacting because Computer Use could not determine Chrome's current URL reliably enough to enforce its policy. No visual layout check, manual browser click-through, cross-browser run, or mobile viewport verification is claimed. Angular's jsdom tests and the real HTTPS proxy test do not replace those checks. Follow the README's browser walkthrough manually.

No production deployment/load test, actual SMTP delivery, durable email queue, edge throttling, signing-key rotation, refresh-retention cleanup or cross-tab refresh coordination has been verified. SMTP and production SQL Data Protection persistence have since been implemented; production query caching is disabled by default. Existing access JWTs remain valid until expiry after logout/role/password/lock changes; refresh families are revoked. .NET 6 is unsupported and production deployment is gated. These are documented boundaries, not a production-readiness claim.

During development, tests exposed early JWT configuration capture and a wrapped SQL deadlock; both were fixed, and the regression suite passed afterward. Windows sandbox escalation was used for normal package caches/downloads, LocalDB and local servers. No existing source work was removed and the practice database was preserved.
