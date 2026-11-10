# Deploying before SMTP is configured

Local validation: Release build passed with zero warnings/errors; all 21 SQL integration tests passed, including rejection of email-dependent requests without creating accounts. The Azure sitecontainer operation still requires validation in the actual deployment environment.

Set `Email__Enabled=false` in App settings on both production and staging and mark it as a deployment slot setting. This explicitly disables email and skips SMTP startup validation. The default remains enabled; missing SMTP configuration still fails startup when email has not been explicitly disabled.

While disabled, registration, forgot-password and resend-confirmation return HTTP 503 before looking up or creating an account. No confirmation/reset tokens are exposed or silently discarded. Email confirmation is still required to log in, and no development accounts are seeded in Production. Existing confirmed accounts can still log in. The public catalog and readiness endpoints remain available; a fresh deployment has no usable customer/admin account until a separate account-provisioning process or SMTP is configured.

To enable email later, configure validated `Smtp__Host`, `Smtp__Port`, `Smtp__From`, optional username/password (secret reference), then set `Email__Enabled=true` and apply the settings.

## Pipeline alignment with the resources created in the portal

The pipeline now deploys the main `orders` sitecontainer in staging using `az webapp sitecontainers create`, port 8080 and its system-assigned identity. It uses the commit-tagged image tested in CI. The installed Azure CLI must support the `sitecontainers` command group. This replaces the classic AzureWebAppContainer task because the portal-created app uses sidecar-enabled containers.

Only `orders-migration-connection` is imported by the migration step. Log-table provisioning uses the same database through the application's existing fallback, so no separate logging migration secret is required for this single-database setup. Runtime SQL and Seq sinks remain disabled until deliberately configured.

`deployAzure` remains false. Before enabling it, finish proxy/host settings, SQL network reachability, deployment identity permissions, the `orders-release-private` Linux agent pool, environment checks and app Key Vault references. Verify production supports the same sitecontainer configuration before swapping. No Azure resources were modified by these repository edits.

[Microsoft sitecontainers CLI reference](https://learn.microsoft.com/en-us/cli/azure/webapp/sitecontainers?view=azure-cli-latest)
