# GitHub to Azure: step-by-step UI walkthrough

Open this file in VS Code and press **Ctrl+Shift+V** to see the screenshots. Internet access is required to load them. If VS Code blocks remote images, open the linked Microsoft source page below the image.

These are **Microsoft Learn reference screenshots**, not screenshots of your account. Sample names and some layouts differ from your portal. Instructions below use this repository's actual pipeline names. No Azure resources or account settings were changed while preparing this guide.

## Before you start

You can complete steps 1–3 now and run CI. The repository has been upgraded to .NET 10; complete the verification in [upgrade notes](dotnet-10-upgrade.md) before deployment. The current infrastructure uses a paid P1v3 plan, Standard ACR, SQL, Key Vault, networking and a private release agent. Review cost before creating resources.

Keep these two tabs open:

- **Azure DevOps:** https://dev.azure.com — project, pipeline, variables and agents.
- **Azure Portal:** https://portal.azure.com — hosting, databases, registry and secrets.

## 1. Create the DevOps project

In Azure DevOps, open your organization and click **New project**.

![New project button in Azure DevOps](https://learn.microsoft.com/en-us/azure/devops/organizations/projects/media/create-project/select-new-project.png)

Enter **OrderManagement**, choose **Private**, and click **Create**. Your application code stays in GitHub; there is no need to import it into Azure Repos.

![Project creation form](https://learn.microsoft.com/en-us/azure/devops/organizations/projects/media/create-project/create-new-project-form.png)

[Screenshot source: Microsoft — Create a project](https://learn.microsoft.com/en-us/azure/devops/organizations/projects/create-project?view=azure-devops).

## 2. Connect the existing GitHub pipeline

In the project, click **Pipelines → Pipelines → New pipeline** (or **Create pipeline**).

![Pipelines page and New pipeline button](https://learn.microsoft.com/en-us/azure/devops/pipelines/get-started/media/pipelines-overview.png)

In the wizard:

1. **Connect:** select **GitHub**.
2. Sign in if prompted; allow the Azure Pipelines GitHub app to access the intended repository.
3. **Select:** choose the repository containing this project.
4. **Configure:** choose **Existing Azure Pipelines YAML file**. Do not replace your existing file with a starter template.
5. Set **Branch = main**, **Path = /azure-pipelines.yml**, then **Continue**.
6. Review the file. Save/run with **deployAzure = false** for the initial CI run.

[Screenshot source and wizard reference: Microsoft — First pipeline](https://learn.microsoft.com/en-us/azure/devops/pipelines/create-first-pipeline?view=azure-devops).

## 3. Verify CI before configuring deployment

Open the run and inspect its jobs. Your initial run should contain **Test** and **Container**; **Deploy** is absent while deployAzure=false.

![Example run summary](https://learn.microsoft.com/en-us/azure/devops/pipelines/get-started/media/pipeline-run-summary.png)

Click a job to inspect its log. Your backend tests use Windows/LocalDB; frontend tests and container smoke checks use Linux. If the UI reports no hosted parallelism, follow the [Microsoft capacity instructions](https://learn.microsoft.com/en-us/azure/devops/pipelines/licensing/concurrent-jobs?view=azure-devops). Subscription credits do not automatically supply Azure DevOps agent capacity.

[Screenshot source: Microsoft — Pipeline run details](https://learn.microsoft.com/en-us/azure/devops/pipelines/create-first-pipeline?view=azure-devops).

## 4. Prepare Azure hosting once

Switch to **Azure Portal**. This repository's deployment is based on `infra/main.bicep`; use that template so slot, identity, port and app settings remain consistent with the pipeline.

The following is a navigation checklist, not a complete replacement for the infrastructure setup. Complete the [landing-zone prerequisites](azure-devops.md#azure-landing-zone-prerequisites) and [provisioning commands](github-to-azure-plan.md#2-create-the-azure-resources-once) before step 5.

| Portal search | What to prepare/check |
| --- | --- |
| Resource groups | Target resource group, for example orders-rg |
| Virtual networks | Delegated App Service integration subnet and working private DNS/connectivity |
| SQL databases | Azure SQL application/log databases, migration user and runtime permissions |
| Key vaults | RBAC vault with required secret names listed in azure-devops.md |
| Virtual machines | Linux release-agent host with connectivity to SQL, vault, ACR and staging |
| Cost Management | Budget and alert for your subscription |

Fill the ignored `infra/main.parameters.json` using the example file. Run the Bicep what-if, review it, then deploy using the commands in the provisioning guide. Afterward, Portal search **App Services → your app → Deployment slots** should show **staging**. Search **Container registries** to find its ACR login server.

Do not configure App Service Deployment Center with GitHub Actions: Azure DevOps YAML is the deployment mechanism for this setup. The bootstrap app can be unhealthy until its first real image is released.

## 5. Create the Azure service connection

Return to Azure DevOps. Bottom-left **Project settings → Service connections → New service connection**. Select **Azure Resource Manager → Next**.

![Select Azure Resource Manager](https://learn.microsoft.com/en-us/azure/devops/pipelines/library/media/new-service-connection-azure-resource-manager.png)

Choose **App registration (automatic)** with **Workload identity federation**, select your subscription and target resource group, then name it **orders-azure-wif**. Save and authorize the intended pipeline individually.

The automatic setup requires suitable subscription and directory permissions. If your subscription is missing, check the signed-in tenant and access. Confirm the connection identity has the resource permissions listed in [the setup plan](github-to-azure-plan.md#3-connect-azure-devops-to-github-and-azure).

[Screenshot source and authentication options: Microsoft — Connect to Azure](https://learn.microsoft.com/en-us/azure/devops/pipelines/library/connect-to-azure?view=azure-devops).

## 6. Add deployment variables

Click **Pipelines → Library → + Variable group**.

![Add a variable group](https://learn.microsoft.com/en-us/azure/devops/pipelines/library/media/add-variable-group.png)

Name the group **orders-production**. Add these values from your deployed resources:

| Variable | Your value |
| --- | --- |
| acrName | Registry resource name |
| acrLoginServer | Registry login server, such as yourregistry.azurecr.io |
| webAppName | App Service name |
| resourceGroupName | Resource group containing the app |
| keyVaultName | Vault containing migration secrets |
| stagingOrigin | https://YOUR-APP-staging.azurewebsites.net |
| productionOrigin | https://YOUR-APP.azurewebsites.net |

Click **Save**. Use the group's **Pipeline permissions** controls where available to authorize your pipeline. These entries are nonsecret configuration; the app/migration credentials remain in Key Vault.

![Variable group editor with Save button](https://learn.microsoft.com/en-us/azure/devops/pipelines/library/media/save-variable-group.png)

[Screenshot source: Microsoft — Variable groups](https://learn.microsoft.com/en-us/azure/devops/pipelines/library/variable-groups?view=azure-devops).

## 7. Register the private release agent

Open **Project settings → Agent pools**. If creating the pool requires organization settings, open **Organization settings → Agent pools → Add pool** instead. Choose **Self-hosted**, name it **orders-release-private**, and make it available to this project.

Open the pool → **Agents → New agent → Linux**. Follow the download and registration instructions shown there on your release host. Install Docker, PowerShell and Azure CLI, and configure the agent as a service so it stays available.

Expected result: an **Online** agent in this pool. Validate SQL/Key Vault/ACR connectivity and access to the restricted staging origin before running deployment. This pool is required by the existing YAML; creating its name without registering a working agent will leave deployment queued.

## 8. Create the deployment environment

Open **Pipelines → Environments → New environment**.

1. Name: **orders-production**.
2. Resource: **None**; the pipeline deploys through Azure tasks.
3. Create, then open **Approvals and checks**.
4. Add **Branch control** for `refs/heads/main` and an **Exclusive lock**.
5. Authorize the intended pipeline when requested.

For unattended push deployment, leave manual approval checks unconfigured. Existing organization-required checks still apply. Add `lockBehavior: sequential` to the Deploy stage as described in the code plan.

Steps 7–8 are navigation instructions without reference screenshots; the images in this guide cover the screens verified in Microsoft's published documentation.

## 9. Enable automatic deployment in code

After the framework upgrade, Azure configuration and CI checks pass, apply **all** YAML edits in [Enable push deployment](github-to-azure-plan.md#4-enable-push-deployment-in-the-yaml-after-prerequisites-pass).

That includes setting deployAzure's default to true, limiting production variables/image pushes/deployment to main outside PR builds, and serializing releases. Selecting true in the Run dialog affects only that run; it does not change future push defaults.

Commit and push these verified changes to GitHub main. The trigger is already present:

```yaml
trigger:
  branches:
    include: [main]
```

## 10. Watch your first release

Open **Pipelines → your pipeline → latest run**. Expect **Test → Container → Deploy**. Click each stage/job to inspect progress.

![Example stages and jobs view](https://learn.microsoft.com/en-us/azure/devops/pipelines/get-started/media/pipeline-jobs-pane.png)

Your Deploy job performs migrations, staging deployment, staging verification, slot swap and production verification. Sample stage names in Microsoft's screenshot differ from yours.

In Azure Portal, open **App Services → your app → Overview → Browse**. Verify the UI, a direct Angular route, registration/email/login and an order flow. Open `https://YOUR-APP.azurewebsites.net/api/health/ready`; expect HTTP 200.

[Screenshot source: Microsoft — Jobs and stages](https://learn.microsoft.com/en-us/azure/devops/pipelines/create-first-pipeline?view=azure-devops).

## If a run stops

| What you see | Where to check |
| --- | --- |
| Production framework gate failed | Complete the coordinated .NET 10 upgrade |
| Agent waiting/offline | orders-release-private pool and agent service |
| Service connection not authorized | Pipeline resource authorization and connection permissions |
| Variable group missing | Exact group name orders-production and project |
| ACR push denied | Connection identity's AcrPush role and registry networking |
| Container cannot pull | App/slot managed identity AcrPull role and propagation |
| Key Vault reference unresolved | App/slot vault permissions, secret names, DNS and network |
| Readiness 503 | SQL connectivity, permissions and pending migrations |
| HTTPS redirect loop | Verified reverse-proxy configuration in Bicep parameters |
| Registration email missing | Production SMTP configuration and provider delivery |

Once configured, later deployments require only a push to main and successful automated checks.
