# Verifies real HTTPS cookie transport and CSRF through the Angular development proxy.
# Creates and cancels one practice order, preserving stock. Does not print credentials or tokens.
param([string]$Origin = 'https://localhost:4200')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$root = Split-Path $PSScriptRoot -Parent
$settings = Get-Content -LiteralPath (Join-Path $root '.local/dev-secrets.json') -Raw | ConvertFrom-Json
$cookies = [System.Net.CookieContainer]::new()
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.CookieContainer = $cookies
$client = [System.Net.Http.HttpClient]::new($handler)
function Request([string]$Method, [string]$Path, $Body, [int]$Status = 200) {
    $uri = [Uri]::new("$Origin$Path")
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), $uri)
    $token = $cookies.GetCookies($uri)['XSRF-TOKEN']
    if ($token) { $request.Headers.Add('X-XSRF-TOKEN', [Uri]::UnescapeDataString($token.Value)) }
    if ($null -ne $Body) { $request.Content = [System.Net.Http.StringContent]::new(($Body | ConvertTo-Json -Depth 10 -Compress), [Text.Encoding]::UTF8, 'application/json') }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    try {
        if ([int]$response.StatusCode -ne $Status) { throw "$Method $Path expected $Status, received $([int]$response.StatusCode)" }
        $bodyText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($bodyText -and $response.Content.Headers.ContentType.MediaType -like '*json*') { return $bodyText | ConvertFrom-Json }
    } finally { $response.Dispose(); $request.Dispose() }
}
try {
    Request 'GET' '/api/auth/csrf' $null 204
    $user = Request 'POST' '/api/auth/login' @{email='customer@orders.local';password=$settings.DevelopmentAccountsPassword}
    if ($user.roles -notcontains 'Customer') { throw 'Expected seeded Customer role' }
    Request 'GET' '/api/auth/csrf' $null 204
    $catalog = Request 'GET' '/api/products' $null
    $product = $catalog.items | Where-Object stockQuantity -GT 0 | Select-Object -First 1
    if (!$product) { throw 'Need an active in-stock product for proxy order check' }
    $order = Request 'POST' '/api/orders' @{items=@(@{productId=$product.id;quantity=1})} 201
    if ($order.totalAmount -ne $product.price) { throw 'Server order total mismatch' }
    $cancelled = Request 'POST' "/api/orders/$($order.id)/cancel" @{rowVersion=$order.rowVersion}
    if ($cancelled.status -ne 'Cancelled') { throw 'Cancellation failed' }
    Request 'POST' '/api/auth/refresh' @{} 204
    Request 'GET' '/api/auth/csrf' $null 204
    Request 'POST' '/api/auth/logout' @{} 204
    Request 'GET' '/api/auth/me' $null 401
    Write-Host 'PASS: HTTPS Angular proxy, CSRF, login, server-priced order, cancellation, refresh, logout and 401.'
} finally { $client.Dispose(); $handler.Dispose(); $settings = $null }
