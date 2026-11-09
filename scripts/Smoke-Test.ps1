param([string]$BaseUrl = 'https://localhost:7043')
$ErrorActionPreference = 'Stop'
# Requires the API, migrated SQL Server database and a trusted development cert.
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
function Get-Response([string]$Path, [int]$ExpectedStatus) {
    $response = $client.GetAsync("$BaseUrl$Path").GetAwaiter().GetResult()
    try {
        if ([int]$response.StatusCode -ne $ExpectedStatus) { throw "$Path expected $ExpectedStatus, received $([int]$response.StatusCode)" }
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($response.Content.Headers.ContentType.MediaType -like '*json*') { return $body | ConvertFrom-Json }
        return $body
    } finally { $response.Dispose() }
}
try {
    $null = Get-Response '/api/health/live' 200
    $ready = Get-Response '/api/health/ready' 200
    if ($ready.status -ne 'ready') { throw 'Database not ready' }
    $page = Get-Response '/api/products?search=KEY-001&pageSize=1' 200
    if ($page.totalCount -ne 1 -or $page.items[0].sku -ne 'KEY-001' -or $page.items[0].price -ne 3499) { throw 'Seeded product projection/search failed' }
    $empty = Get-Response '/api/products?search=unknown-sku-does-not-exist' 200
    if ($empty.totalCount -ne 0) { throw 'Empty search failed' }
    $null = Get-Response '/api/products?pageSize=0' 400
    $null = Get-Response '/api/products?sort=untrusted-column' 400
    $null = Get-Response '/api/products/999999' 404
    $paged = Get-Response '/api/products?pageSize=1&pageNumber=2&sort=price' 200
    if ($paged.items.Count -ne 1 -or $paged.pageNumber -ne 2) { throw 'Pagination failed' }
    Write-Host 'PASS: liveness, SQL readiness, seeded projection, search, empty state, validation, sort allowlist, 404, pagination.'
} finally { $client.Dispose() }
