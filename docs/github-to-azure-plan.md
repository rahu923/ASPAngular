# GitHub push to Azure with Azure DevOps

## What you will get

```text
git push origin main
  -> Azure Pipelines: backend + Angular tests
  -> Docker build + complete-stack smoke test + vulnerability scan
  -> Azure Container Registry: orders:<Git commit SHA>
  -> Private release agent: database migrations
  -> App Service staging slot: deploy + verify
  -> Swap staging into production + verify
```

Your GitHub repository stays on GitHub. Azure DevOps runs the pipeline; Azure hosts the application. Dockerfile already builds Angular into ASP.NET's wwwroot, so one HTTPS hostname serves both UI and /api. There is no need to deploy Angular separately or run npm start in Azure.

The implementation already exists in `azure-pipelines.yml`, `Dockerfile`, `infra/main.bicep`, and `scripts/Verify-Deployment.ps1`. This guide explains the one-time setup and the code changes required to turn on push deployment. It does not provision Azure resources.

## 1. Complete the framework upgrade first

The repository now targets .NET 10 with SDK 10.0.403, Microsoft packages/EF tool 10.0.14 and .NET 10 Docker images. Review [upgrade notes](dotnet-10-upgrade.md), including the database migration and verification results. The framework gate remains; do not bypass failing tests or security checks.

Run backend tests, Angular tests/build, and the Compose smoke test after the upgrade. See [the existing deployment guide](azure-devops.md) for verification commands and runtime configuration requirements.

You can connect the pipeline now with deployAzure=false to verify CI while completing this work.

## 2. Create the Azure resources once

The existing architecture uses a paid Linux P1v3 App Service plan with a staging slot, Standard ACR, Azure SQL, Key Vault, VNet/subnet, and a private release agent. SMTP and Seq endpoints are also required by the current template. Free subscription credits are a payment allowance, not a free hosting tier. Set a budget alert and check regional estimates before provisioning. Budget alerts do not stop resources automatically.

This is a production-oriented architecture with more setup and expense than a small practice deployment. A cheaper single-app deployment would need a separate infrastructure/pipeline design without slot swaps and the private landing-zone assumptions; simply changing P1v3 to a free/basic SKU will not preserve this design.

Follow the landing-zone requirements in [azure-devops.md](azure-devops.md): SQL databases/users, RBAC-enabled vault and secrets, delegated subnet, private connectivity/DNS, verified proxy ranges and release-agent egress. The Bicep template does not create those dependencies.

From PowerShell with Azure CLI installed:

```powershell
az login
az account set --subscription '<your-subscription-id>'
az group create --name orders-rg --location centralindia
Copy-Item infra/main.parameters.example.json infra/main.parameters.json
# Edit the ignored parameter file: replace every placeholder with your actual resources.
# Keep secret values in Key Vault; parameters contain secret URIs only.
az bicep build --file infra/main.bicep
az deployment group what-if --resource-group orders-rg --template-file infra/main.bicep --parameters '@infra/main.parameters.json'
# After reviewing the proposed resources and cost:
az deployment group create --resource-group orders-rg --template-file infra/main.bicep --parameters '@infra/main.parameters.json'
```

Choose a region available to your subscription. The bootstrap image does not exist yet, so the initial app is unhealthy until the first successful release. The deployment identity used for bootstrap needs role-assignment rights; keep those broader rights separate from routine release permissions.

## 3. Connect Azure DevOps to GitHub and Azure

1. Create an Azure DevOps organization/project at https://dev.azure.com.
2. Project settings -> Service connections -> New -> Azure Resource Manager. Use workload identity federation, select your subscription/resource group, and name it `orders-azure-wif`. Authorize only the intended pipeline. This avoids storing an Azure client secret in GitHub/YAML.
3. Give the service connection identity AcrPush on the registry, permissions to update/swap the target web app and slots, and permission to read the two migration secrets in Key Vault. Confirm vault network access and secret-list/get requirements for AzureKeyVault@2. The web app identities use AcrPull and Key Vault secret access configured by Bicep.
4. Project settings -> Agent pools: create `orders-release-private`. Register a Linux self-hosted agent with Docker, PowerShell and Azure CLI, on a network that can reach SQL, ACR, Key Vault and the restricted staging endpoint. Keep it running for automatic deployment. Public CI jobs still use Microsoft-hosted agents.
5. Pipelines -> Library: create variable group `orders-production` and authorize this pipeline. Add these nonsecret variables:

| Name | Example; replace with your actual value |
| --- | --- |
| acrName | your unique registry name |
| acrLoginServer | yourregistry.azurecr.io |
| webAppName | your unique App Service name |
| resourceGroupName | orders-rg |
| keyVaultName | your existing vault name |
| stagingOrigin | https://yourapp-staging.azurewebsites.net |
| productionOrigin | https://yourapp.azurewebsites.net |

6. Pipelines -> Environments: create `orders-production`. Add branch control for main and an exclusive-lock check. For deployment without a human click, do not add a manual approval check. Restrict who can change these settings and the release YAML.
7. Pipelines -> New pipeline -> GitHub -> authorize the Azure Pipelines GitHub app for this repository -> Existing Azure Pipelines YAML file -> branch main -> `/azure-pipelines.yml` -> Run. Keep deployAzure=false for the first CI verification.

If the organization reports no hosted parallelism, request the hosted-agent grant or purchase capacity. Azure subscription credits and Azure DevOps hosted-agent capacity are separate. With one hosted job available, the two test jobs can queue and run sequentially.

## 4. Enable push deployment in the YAML after prerequisites pass

The main trigger already exists. Automatic runs use parameter defaults, so choosing deployAzure=true for one manual run does not enable it for future pushes. Change its default in `azure-pipelines.yml`:

```yaml
parameters:
  - name: deployAzure
    type: boolean
    default: true
  - name: azureServiceConnection
    type: string
    default: orders-azure-wif
```

Before enabling this default, make these two additional changes so PR validation does not load the production variable group or push images. Leave the Test and normal container smoke-test steps intact.

Replace the conditional variable-group entry with:

```yaml
  - ${{ if and(eq(parameters.deployAzure, true), eq(variables['Build.SourceBranch'], 'refs/heads/main'), ne(variables['Build.Reason'], 'PullRequest')) }}:
    - group: orders-production
```

Use the same condition for the existing conditional step block containing Check-ProductionReadiness, the production vulnerability scan, and AzureCLI image push:

```yaml
          - ${{ if and(eq(parameters.deployAzure, true), eq(variables['Build.SourceBranch'], 'refs/heads/main'), ne(variables['Build.Reason'], 'PullRequest')) }}:
            # Keep the existing readiness check, scan and AzureCLI push tasks here.
```

Use that same compile-time expression on the conditional block wrapping the Deploy stage. Inside that stage retain the existing main-branch check and explicitly exclude PRs:

```yaml
      condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'), ne(variables['Build.Reason'], 'PullRequest'))
      lockBehavior: sequential
```

`lockBehavior` is a stage-level property, alongside `condition` and `jobs`; it works with the environment's exclusive-lock check. This serializes releases so migrations and slot swaps cannot overlap. The snippets above are targeted edits to the existing pipeline, not a replacement pipeline. Validate one main run and one PR run before relying on unattended releases. Do not grant production access to untrusted PR pipelines.

The existing Deploy stage already retrieves migration credentials from Key Vault, runs `--migrate`, deploys the tested commit-tagged image with AzureWebAppContainer@1, checks staging, swaps slots and checks production. Avoid duplicating those tasks in a second pipeline.

## 5. Push and verify

Commit the framework upgrade and YAML changes from a machine with Git installed:

```powershell
git add global.json .config/dotnet-tools.json src tests Dockerfile azure-pipelines.yml
git commit -m "Enable verified Azure deployment on main pushes"
git push origin main
```

Review the staged files before committing, including all upgrade changes. Never stage `.env`, `.local`, `.certs`, connection strings or the ignored real parameter file.

Watch Pipelines -> Runs. A push should run Test -> Container -> Deploy without manually selecting parameters. The deployed UI is `productionOrigin`; `/api/health/ready` must return HTTP 200, and Angular deep links must load. Verify registration, email confirmation, login and an order flow. Local seeded accounts do not exist in Production; use the registration/email workflow and a separately controlled admin-provisioning process.

If the first run requests resource authorization, authorize the intended pipeline once. Later runs remain automatic unless an environment check requires input. For rollback, swap the previous slot back and verify it; do not automatically reverse database migrations. Use backward-compatible schema changes because both app versions can run during a release.

## Microsoft references

- [App Service deployment through Azure Pipelines](https://learn.microsoft.com/en-us/azure/app-service/deploy-azure-pipelines)
- [Workload identity service connections](https://learn.microsoft.com/en-us/azure/devops/pipelines/release/configure-workload-identity?view=azure-devops)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
- [Hosted parallel jobs](https://learn.microsoft.com/en-us/azure/devops/pipelines/licensing/concurrent-jobs?view=azure-devops)
