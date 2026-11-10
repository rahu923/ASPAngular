# Docker and Azure DevOps deployment guide

For click-by-click setup with Microsoft UI reference screenshots, see the [Azure UI walkthrough](azure-ui-walkthrough.md). For commands and YAML edits, see the [GitHub-to-Azure setup plan](github-to-azure-plan.md).

## Release status and framework gate

The backend and tests now target .NET 10 LTS, using SDK 10.0.403 and Microsoft packages/EF tool 10.0.14. Docker uses .NET 10 Ubuntu Noble images. See [upgrade notes](dotnet-10-upgrade.md) for migration and verification results. This is **not a verified production release**. The framework gate remains enabled; passing it does not replace container, vulnerability, database or Azure environment checks.

## Local complete stack

Prerequisites: Docker Desktop running Linux containers, Node, the pinned .NET SDK, and enough Docker memory for SQL Server (at least 4 GB overall; 6–8 GB recommended).

```powershell
powershell -NoProfile -File scripts/Initialize-Docker.ps1
docker compose config --quiet
docker compose up --build --detach --wait --wait-timeout 240
powershell -NoProfile -File scripts/Verify-Container.ps1
```

Open https://localhost:8443. Trust your own development certificate on the host using `dotnet dev-certs https --trust` if necessary; do not bypass a browser security warning. The smoke script pins the exact exported certificate and never accepts arbitrary certificates. On Linux, the non-root container must be able to read the password-encrypted .certs/localhost.pfx bind mount; make that file readable for UID 10001 or use an appropriate mount/ACL. CI grants read access only to the encrypted development PFX on its disposable agent.

Components:

- **app:** multi-stage image builds Angular with Node, publishes the API, copies Angular to wwwroot and runs as UID/GID 10001. One origin serves SPA routes and /api; no development Node server or wildcard CORS in the deployed image.
- **migrate:** runs the same image once with --migrate, applies EF migrations, creates the explicit logging table and seeds predefined roles. It exits before the app starts. Only app builds the shared image, avoiding concurrent image-export races.
- **sql:** SQL Server Developer, private Compose network only. This license is for local practice/CI, never production.
- **seq:** local Seq UI on loopback port 5341 and private-network ingestion, with its initial admin password generated in .env.

The initializer creates ignored .env values without overwriting existing ones. Local Customer/Manager/Admin account emails are the README accounts, but **container passwords come from DEVELOPMENT_PASSWORD in .env**, independently of host LocalDB's .local/dev-secrets.json. Local-only SQL SA credentials are also generated. Production never uses these credentials. The app uses the host-exported encrypted PFX; certificates/passwords/.env/.local are excluded from image context. Named volumes persist SQL, Seq, local key/mailbox data and rolling log files.

`docker compose stop` stops services. `docker compose down` removes containers/network while preserving volumes. Do not add --volumes unless deliberately deleting the local database/logs. Back up before any destructive reset. Images use supported .NET 10 Ubuntu Noble tags; pin reviewed digests in the release baseline. No image contains the SDK or Node runtime in its final layer.

## Application hardening provided

- Production SMTP uses mandatory STARTTLS with normal certificate validation and configurable credentials, not the development mailbox. Host/port/from are validated at startup. Provider sandbox/deliverability must still be checked.
- Production antiforgery/Identity Data Protection keys persist in SQL and are encrypted using a supplied private PFX certificate. Supply its base64 bytes and password through Key Vault. Use the same key ring/certificate/application name across replicas and production/staging to preserve tokens through slot swaps. Retain old certificates for recovery; seamless multi-certificate rotation is not implemented.
- No production schema migration or role creation on normal startup. A separate release identity performs DDL through --migrate. Runtime DB identities receive only required DML permissions. Production users/admin provisioning must use a separately authorized controlled process; development accounts are never seeded in Production.
- Forwarded scheme/IP headers are accepted only from explicit immediate proxy addresses/networks, with one hop. Verify the hosting proxy network from your environment rather than guessing a CIDR or trusting all addresses. It runs before redirection, authentication and antiforgery. Untrusted forwarded headers are ignored. Bicep requires these networks; there is no 0.0.0.0/0 fallback.
- Production requires encrypted, trusted SQL connections, HTTPS PublicOrigin/Seq, Secure HttpOnly authentication cookies, HSTS and response security headers. Same-origin CSP prevents arbitrary script/connect/frame sources; Angular style tags remain allowed. No permissive CORS is configured.
- Readiness checks SQL connectivity and pending migrations. Missing API routes remain JSON 404s, while client routes get the SPA document. Production per-process catalog caching is disabled, avoiding divergent caches across instances.
- Logging avoids authentication payloads and raw exceptions. See logging.md for all sinks and retention/permissions.

Still required: ongoing package/security review, edge throttling/WAF rules for auth/recovery and volumetric traffic, backup/PITR restoration drill, load/concurrency tests at intended scale, monitored email/log delivery, expiry/revocation behavior review, operational alerting, certificate/signing-key rotation procedures and cross-tab refresh coordination if needed. Strict reuse detection can invalidate a family when independent tabs race. Do not advertise production readiness until these environment-specific checks pass.

## Azure landing-zone prerequisites

infra/main.bicep intentionally consumes your existing network/data/security landing zone. It creates a paid Linux P1v3 App Service plan, ACR with admin credentials disabled, the web app and restricted staging slot. It configures system-assigned managed identities for both slots, AcrPull and Key Vault Secrets User roles, TLS/HTTPS, health/warm-up paths, VNet integration, and sticky secret/PublicOrigin settings. It disables FTP and basic publishing credentials. P1v3 and the other resources incur Azure charges; no resource has been created by this implementation.

Before deployment provide:

1. An Azure resource group and **RBAC-enabled Key Vault** in that group. Configure private endpoint/DNS or approved network access for the web app and release agent.
2. An existing delegated Microsoft.Web/serverFarms integration subnet with adequate addresses, plus reachable **Azure SQL** database(s), preferably private endpoints and DNS. Azure SQL is separate from the local Developer container. Apply SQL baseline/least-privilege users, backup/PITR and organizational policies. The template does not create SQL, its firewall or your landing-zone network.
3. A self-hosted release agent pool named **orders-release-private**, with Docker, PowerShell, Azure CLI, outbound access to ACR/Key Vault/SQL and allowed staging ingress CIDRs. Microsoft-hosted CI agents run tests/builds; they never receive production DB credentials.
4. A federated Azure Resource Manager service connection **orders-azure-wif**. Limit scope. Image push needs AcrPush; app/slot deployment needs appropriate web-app permissions. Bootstrap IaC role assignments require RBAC role-assignment rights; separate that bootstrap identity from the routine release identity.
5. Production SMTP credentials/from address, TLS Seq endpoint and ingestion API key, strong JWT signing key, and the Data Protection private PFX/certificate password. Do not put them in parameter files.

Versionless Key Vault secrets referenced by Bicep (URIs only in parameter files): orders-runtime-connection, orders-logs-connection, orders-jwt-key, orders-dp-certificate, orders-dp-password, orders-smtp-password and orders-seq-api-key. The release agent additionally reads **orders-migration-connection** and **orders-logs-migration-connection**, using separate DDL identities. If one database is used, the logging migration secret can point to that database too. Runtime permissions should not include schema modification. Keep access to the migration secrets narrower than access to app secrets.

Copy infra/main.parameters.example.json to ignored infra/main.parameters.json and replace every placeholder. Trusted proxy ranges must be verified for your App Service hosting environment. Staging allowed CIDRs must match the private release agent's egress as seen by App Service. Azure health-check access restrictions must also be validated. For custom domains, extend AllowedHosts, PublicOrigin and certificate configuration explicitly.

```powershell
az bicep build --file infra/main.bicep
az deployment group what-if --resource-group <resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json
az deployment group create --resource-group <resource-group> --template-file infra/main.bicep --parameters @infra/main.parameters.json
```

Review what-if and cost before the create command. The initial bootstrap image tag does not exist until an image is pushed/deployed; the app is not healthy during bootstrap. Allow RBAC/managed-identity propagation, populate secrets and perform the first supported-framework release. No Azure CLI/account was available here, so only local Bicep compilation is claimed; remote policy, capacity, networking and permissions must be validated in the target subscription.

## Azure Pipeline setup and flow

azure-pipelines.yml defaults **deployAzure=false** so normal CI runs without production service connections/secrets. Configure it as the pipeline and set Azure Repos branch policies for PR validation (the YAML pr trigger applies to GitHub-style repositories). Require successful CI and reviews on main.

When deployment prerequisites are ready, create variable group **orders-production**:

| Variable | Value |
|---|---|
| acrName / acrLoginServer | created registry / its login server |
| webAppName / resourceGroupName | target App Service / resource group |
| keyVaultName | vault containing migration secrets |
| stagingOrigin / productionOrigin | exact HTTPS slot origins |

No password/token/connection string belongs in this group in plaintext. AzureKeyVault@2 imports only the two migration secrets, and the migration docker run passes them through environment variables, not command-line arguments. Avoid shell tracing or environment dumps on the release agent.

Create Azure DevOps environment **orders-production**, configure its approvals, branch-control and exclusive-lock checks in the UI, and restrict pipeline/service-connection use. These checks cannot be reliably enforced solely by YAML. Select deployAzure=true only after the framework/readiness gate passes.

Flow:

1. Windows SQL Server LocalDB integration tests and Linux Angular tests/build run independently, publishing backend TRX results.
2. Linux CI builds the image, boots the whole Compose stack, runs a certificate-pinned HTTPS authentication/order smoke test and publishes a Trivy SARIF report. It cleans only its disposable CI project volumes.
3. Deployment additionally enforces the supported-framework gate and HIGH/CRITICAL fixable-vulnerability gate before pushing an immutable commit-tagged image to ACR. Pin scanner/image digests in your release baseline; review unfixed vulnerabilities separately.
4. After environment approval, the private release agent runs migrations once with DDL credentials from Key Vault. Use backward-compatible expand/contract migrations; both slots may run during rollout.
5. Deploy to staging, verify readiness, SPA deep links, 401/ProblemDetails and API 404. Swap only after success, then verify production.

Rollback: swap staging back through the Azure App Service slot-swap operation, then verify production. Do not automatically reverse database migrations. Compatible schema changes are mandatory for safe rollback. Retain the previous image/tag and back up databases before destructive schema changes. Runtime failure, unresolved Key Vault references, bad proxy trust or failed SMTP validation should stop rollout rather than weakening those controls.

References: [Azure Pipelines App Service containers](https://learn.microsoft.com/en-us/azure/devops/pipelines/apps/cd/deploy-docker-webapp), [Key Vault references](https://learn.microsoft.com/en-us/azure/app-service/app-service-key-vault-references), [forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-6.0).
