$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$settings = @{}
Get-Content -LiteralPath (Join-Path $root '.env') | ForEach-Object {
    if ($_ -match '^([A-Z_]+)=(.+)$') { $settings[$matches[1]] = $matches[2] }
}
# Trust only this generated local certificate in this process; never disable TLS validation globally.
Add-Type -AssemblyName System.Net.Http
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $root '.certs/localhost.pfx'), $settings.PFX_PASSWORD)
. (Join-Path $PSScriptRoot 'Test-HttpsHandler.ps1')
$handler = [OrderManagement.TestHttpsHandler]::Create($certificate.Thumbprint)
$cookies = [System.Net.CookieContainer]::new()
$handler.CookieContainer = $cookies
$client = [System.Net.Http.HttpClient]::new($handler)
$origin = 'https://localhost:8443'
function Request([string]$method, [string]$path, $body, [int]$expected = 200) {
    $uri = [Uri]::new("$origin$path")
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $uri)
    $token = $cookies.GetCookies($uri)['XSRF-TOKEN']
    if ($token) { $request.Headers.Add('X-XSRF-TOKEN', [Uri]::UnescapeDataString($token.Value)) }
    if ($null -ne $body) { $request.Content = [System.Net.Http.StringContent]::new(($body|ConvertTo-Json -Depth 10 -Compress),[Text.Encoding]::UTF8,'application/json') }
    $response=$client.SendAsync($request).GetAwaiter().GetResult()
    try {
        if ([int]$response.StatusCode -ne $expected) { throw "$method $path expected $expected, got $([int]$response.StatusCode)" }
        $text=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($response.Content.Headers.ContentType.MediaType -like '*json*' -and $text) { return $text|ConvertFrom-Json }
        return $text
    } finally { $response.Dispose();$request.Dispose() }
}
try {
    $html=Request GET '/' $null
    if ($html -notmatch '<app-root') { throw 'Angular document missing' }
    $null=Request GET '/api/health/ready' $null
    $null=Request GET '/api/not-a-route' $null 404
    $null=Request GET '/api/auth/csrf' $null 204
    $null=Request POST '/api/auth/login' @{email='customer@orders.local';password=$settings.DEVELOPMENT_PASSWORD}
    $null=Request GET '/api/auth/csrf' $null 204
    $catalog=Request GET '/api/products' $null
    $product=$catalog.items|Where-Object stockQuantity -GT 0|Select-Object -First 1
    if (!$product) { throw 'No stock available for container smoke order' }
    $order=Request POST '/api/orders' @{items=@(@{productId=$product.id;quantity=1})} 201
    if ($order.totalAmount -ne $product.price) { throw 'Server price mismatch' }
    $cancelled=Request POST "/api/orders/$($order.id)/cancel" @{rowVersion=$order.rowVersion}
    if ($cancelled.status -ne 'Cancelled') { throw 'Cancellation failed' }
    $null=Request POST '/api/auth/refresh' @{} 204
    $null=Request GET '/api/auth/csrf' $null 204
    $null=Request POST '/api/auth/logout' @{} 204
    $null=Request GET '/api/auth/me' $null 401
    Write-Host 'PASS: container HTTPS, SPA, SQL, CSRF, login, orders, cancellation, refresh and logout.'
} finally { $client.Dispose();$handler.Dispose();$certificate.Dispose() }
