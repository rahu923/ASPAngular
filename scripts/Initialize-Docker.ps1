$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
node (Join-Path $PSScriptRoot 'Initialize-Docker.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Secret initialization failed' }
$settings = @{}
Get-Content -LiteralPath (Join-Path $root '.env') | ForEach-Object {
    if ($_ -match '^([A-Z_]+)=(.+)$') { $settings[$matches[1]] = $matches[2] }
}
New-Item -ItemType Directory -Force (Join-Path $root '.certs') | Out-Null
dotnet dev-certs https --export-path (Join-Path $root '.certs/localhost.pfx') --password $settings.PFX_PASSWORD
if ($LASTEXITCODE -ne 0) { throw 'Certificate export failed' }
Write-Host 'Docker configuration ready. Trust the development certificate on your host with dotnet dev-certs https --trust if needed.'
