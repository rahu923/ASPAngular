# .NET 10 upgrade

Upgraded November 10, 2026. All four backend projects and the integration test project target `net10.0`.

## Coordinated changes

- `global.json`: SDK 10.0.403, with patch roll-forward within that feature band.
- ASP.NET JWT, Identity, Data Protection, EF SQL Server, EF Design, MVC Testing and the local `dotnet-ef` tool: 10.0.14.
- Serilog.AspNetCore: 10.0.0; console/file/SQL/Seq sinks updated to compatible versions. MailKit and test dependencies updated. The transitive PKCS dependency is explicitly patched to 10.0.14; NuGet audit reported no known vulnerable dependencies at verification time.
- Docker SDK and ASP.NET base images: `10.0-noble` (Ubuntu). The runtime retains UID/GID 10001 under an `orders` user/group to avoid collision with the built-in `app` user in newer .NET images.
- Forwarded headers use `KnownIPNetworks` / `System.Net.IPNetwork`; external `ReverseProxy:KnownNetworks` configuration keys and restricted proxy trust remain unchanged.
- Certificate loading uses `X509CertificateLoader`; HTTPS certificate pinning remains in place.
- EF check constraints use the supported table-builder API. Existing constraint names, seed data and migration files remain intact.
- Authentication cookies are explicitly HttpOnly. The local source had HttpOnly disabled, which the regression suite detected; this was restored to match the intended security contract. The cookie test parses attributes and checks Secure, HttpOnly, SameSite and exact paths.
- Nullable annotations/handling updated for .NET 10 API contracts, including missing user claims and SMTP password configuration.

## Database compatibility

EF 10's `migrations has-pending-model-changes` reports **no model changes since the last migration**. No new migration or database reset is needed for this upgrade. Historical migration snapshots retain their original EF 6 version annotations deliberately. Do not rewrite applied migrations solely to change their version labels.

The integration tests create and remove their own uniquely named `OrderManagementTests_*` databases. The existing development database was not migrated or reset. Back up real data before a framework/database rollout.

## Verified locally

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings/errors |
| SQL LocalDB integration tests | 20 passed, zero failed/skipped |
| EF pending-model check | No changes |
| NuGet audit, including transitive dependencies | No known vulnerable packages reported |
| API Release publish | Passed |
| Production framework gate | Passed |
| Docker image and Compose smoke checks | Not run: Docker Desktop Linux engine unavailable |

```powershell
dotnet restore OrderManagement.sln
dotnet tool restore
dotnet build OrderManagement.sln -c Release --no-restore
SqlLocalDB start MSSQLLocalDB
dotnet test OrderManagement.sln -c Release --no-build --no-restore
$env:ASPNETCORE_ENVIRONMENT = 'Testing'
dotnet ef migrations has-pending-model-changes --project src/OrderManagement.Infrastructure --startup-project src/OrderManagement.Api --configuration Release --no-build
Remove-Item Env:ASPNETCORE_ENVIRONMENT
dotnet list OrderManagement.sln package --vulnerable --include-transitive --no-restore
powershell -NoProfile -File scripts/Check-ProductionReadiness.ps1
```

The upgraded container image, Compose startup and HTTPS/order smoke flow must pass Azure Pipelines before deployment. No Azure deployment was performed. The `deployAzure=false` default remains unchanged.

## Push to GitHub and continue Azure setup

Commit all upgrade changes together: project files, `global.json`, `.config/dotnet-tools.json`, `Dockerfile`, API/infrastructure compatibility changes, tests and documentation. Review the staged diff; exclude `.env`, `.local`, certificates, credentials and temporary verification output. Push to `master` to run Test and Container stages.

Passing the framework gate alone does not make deployment ready. The manually created Azure app uses sidecar container configuration; the deployment task still needs to be reconciled with that interface. SQL, SMTP, Data Protection secrets, proxy trust, release agent and runtime settings also need completion. Keep deployment disabled while finishing those prerequisites.

## References

- [ASP.NET Core 10 migration guidance](https://learn.microsoft.com/en-us/aspnet/core/migration/90-to-100?view=aspnetcore-10.0)
- [KnownIPNetworks migration](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/10/ipnetwork-knownnetworks-obsolete?view=aspnetcore-10.0)
- [EF Core 10 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/breaking-changes)
