param([Parameter(Mandatory=$true)][string]$Origin)
$ErrorActionPreference = 'Stop'
$uri = [Uri]$Origin
if ($uri.Scheme -ne 'https') { throw 'Deployment verification requires HTTPS' }
$Origin = $Origin.TrimEnd('/')
Add-Type -AssemblyName System.Net.Http
$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)
function Get-VerifiedHtml([string]$Path) {
    $result = $client.GetAsync("$Origin$Path").GetAwaiter().GetResult()
    try {
        $status = [int]$result.StatusCode
        if ($status -ne 200) {
            # Omit query strings/fragments, which may contain authentication data.
            $location = $result.Headers.Location
            $destination = if ($location) { ($location.OriginalString -split '[?#]')[0] } else { '(none)' }
            throw "SPA check '$Path' returned HTTP $status; redirect destination: $destination. For HTTPS-to-HTTPS redirects, check trusted proxy configuration and the Transport proxy warning in App Service runtime logs."
        }
        return $result.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    } finally { $result.Dispose() }
}
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
    Write-Host 'PASS: database/migration readiness. Checking SPA responses without following redirects.'
    $html = Get-VerifiedHtml '/'
    if ($html -notmatch '<app-root') { throw 'Angular SPA missing from image' }
    $login = Get-VerifiedHtml '/account/login'
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
