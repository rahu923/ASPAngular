$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/OrderManagement.Api/OrderManagement.Api.csproj')
$framework = [string]$project.Project.PropertyGroup.TargetFramework
if ($framework -ne 'net10.0') {
    throw "Production deployment blocked: $framework is retained from the practice brief. Upgrade and verify the app on .NET 10 LTS before enabling Azure production deployment."
}
Write-Host 'Framework production gate passed. Environment approvals and deployment checks remain required.'
