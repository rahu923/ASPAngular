param([Parameter(Mandatory=$true)][string]$Origin)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$Origin
if ($uri.Scheme -ne 'https') { throw 'Deployment verification requires HTTPS' }
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
try {
    $ready = $false
    for ($attempt=0; $attempt -lt 24; $attempt++) {
        try {
            $response = $client.GetAsync("$Origin/api/health/ready").GetAwaiter().GetResult()
            $ready = [int]$response.StatusCode -eq 200
            $response.Dispose()
            if ($ready) { break }
        } catch { }
        Start-Sleep -Seconds 5
    }
    if (!$ready) { throw 'Deployment did not become database/migration ready' }
    $html = $client.GetStringAsync("$Origin/").GetAwaiter().GetResult()
    if ($html -notmatch '<app-root') { throw 'Angular SPA missing from image' }
    $login = $client.GetStringAsync("$Origin/account/login").GetAwaiter().GetResult()
    if ($login -notmatch '<app-root') { throw 'SPA route fallback failed' }
    $unauthorized = $client.GetAsync("$Origin/api/auth/me").GetAwaiter().GetResult()
    if ([int]$unauthorized.StatusCode -ne 401) { throw 'Protected API must return 401 for anonymous caller' }
    if ($unauthorized.Content.Headers.ContentType.MediaType -ne 'application/problem+json') { throw 'Expected ProblemDetails response' }
    $unauthorized.Dispose()
    $missing = $client.GetAsync("$Origin/api/does-not-exist").GetAwaiter().GetResult()
    if ([int]$missing.StatusCode -ne 404) { throw 'Unknown API route must not return the SPA' }
    $missing.Dispose()
    Write-Host 'PASS: HTTPS readiness, SPA, deep route, anonymous 401 and API 404.'
} finally { $client.Dispose(); $handler.Dispose() }
