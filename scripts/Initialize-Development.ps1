$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force (Join-Path $root '.local') | Out-Null
$destination = Join-Path $root '.local/dev-secrets.json'
if (!(Test-Path -LiteralPath $destination)) {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $password = 'Practice!7aA' + [Convert]::ToBase64String($bytes)
    @{ DevelopmentAccountsPassword = $password } | ConvertTo-Json | Set-Content -LiteralPath $destination
}
Write-Host 'Development settings ready in ignored .local/dev-secrets.json. Open it locally for the account password; do not commit or share it.'
