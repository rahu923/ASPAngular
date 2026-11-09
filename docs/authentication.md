# Cookie JWT authentication and CSRF

Implemented in Security/AuthSupport.cs, Controllers/AuthController.cs, the SecureSessions migration and Angular core/auth.ts / core/http.ts. Milestone 1 documented this design before authentication implementation.

Identity's UserManager, RoleManager and SignInManager own passwords, roles, lockout and recovery. CheckPasswordSignInAsync checks credentials without issuing an Identity application cookie. There is exactly one authentication scheme: JWT bearer, reading only the designated access cookie.

## Browser flow

1. GET /api/auth/csrf obtains the ASP.NET antiforgery cookie and a readable request token in XSRF-TOKEN.
2. Angular sends X-XSRF-TOKEN on unsafe relative /api/ requests. Backend antiforgery validation includes login, register, refresh, logout and recovery. All GET routes are read-only.
3. Login sets two Secure HttpOnly host-only cookies. JWT signature, issuer, audience and expiry are validated. Authentication tokens never appear in JSON, localStorage, sessionStorage or JavaScript-readable cookies.
4. Startup calls /api/auth/me. If access has expired, the shared sequence is csrf → refresh → csrf → me. Reissue the CSRF request token after identity changes because antiforgery tokens are identity-bound.
5. The functional interceptor shares refresh among concurrent 401s, retries each request once and reconstructs its CSRF header. An epoch prevents delayed old-token failures starting another refresh. Auth endpoints are excluded; 403 never refreshes. Failure clears client user state.
6. Logout revokes the family, deletes both authentication cookies with matching paths/flags and obtains an anonymous CSRF token.

| Cookie | Secure | HttpOnly | SameSite | Path | Lifetime |
|---|---|---|---|---|---|
| oms-access | Yes | Yes | Lax | /api | 5 minutes (configurable 1–15) |
| oms-refresh | Yes | Yes | Lax | /api/auth | 7 days absolute |
| oms-antiforgery | Yes | Yes | Lax | / | browser session |
| XSRF-TOKEN | Yes | No | Lax | / | browser session; reissued after identity changes |

HttpOnly reduces JavaScript token disclosure; it does not prevent CSRF. XSS can still perform requests in the victim browser, so Angular escaping and avoiding unsafe HTML remain essential.

## Rotation and revocation

Tokens use 48 cryptographically random bytes; only unique indexed SHA-256 hashes are persisted. RefreshFamily stores user, original security stamp, expiry and revocation. RefreshSession stores hash, expiry and consumption. A SQL transaction acquires exclusive sp_getapplock per family before inspection. Rotation consumes one token and inserts its successor atomically. Reuse, expiry, revocation, lockout, missing/unconfirmed user or changed stamp invalidates the family. Revocation commits before the failure response.

Strict reuse detection intentionally revokes a family on duplicate requests, including retries after a lost response. Coordination is within one Angular instance; cross-tab coordination is not implemented. Independent tabs racing the same refresh token may require another sign-in. Consumed hashes must be retained until family expiry for replay detection; no cleanup job is implemented.

Logout locks the same family. Password and Admin role/lock changes revoke families and update the security stamp. Already issued access JWTs, including signed permission claims, remain valid until expiry. No per-request security-stamp or revocation lookup is implemented; a test explicitly verifies this logout limit.

## HTTPS and deployment

Angular and API both use local HTTPS. Relative /api requests use the Angular development proxy, preserving browser same-origin cookie/XSRF behavior. The custom interceptor attaches credentials/CSRF only to relative /api/ URLs, never arbitrary third-party URLs. The local proxy's secure:false is only for the development backend certificate.

Prefer a single deployment HTTPS origin for SPA and /api, requiring no CORS. Configure a cryptographically random signing key of at least 64 characters, trusted issuer/audience, HTTPS PublicOrigin and a real email sender. Persist/protect Data Protection keys. Ignored development signing, password, mailbox and Data Protection files must remain private. Recovery responses never contain tokens; links use fragments to avoid HTTP request-log exposure. Identity recovery tokens expire after two hours.

Separate-origin deployment is not configured. If added, allowlist exact origins, credentials and the CSRF header, and implement trusted-origin XSRF handling because Angular's built-in interceptor skips absolute cross-origin URLs. Cross-site cookies require SameSite=None;Secure and may still be blocked by third-party-cookie policies. Never combine wildcard origins and credentials.

Current hosting changes add SMTP with STARTTLS, certificate-encrypted SQL Data Protection keys and production caching disabled by default. See azure-devops.md. Remaining operational limits include signing-key rotation, no cross-tab refresh coordination or durable email queue, edge throttling, and unverified production deployment. .NET 6 is unsupported.

References: [ASP.NET antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery), [Angular interceptors](https://angular.dev/guide/http/interceptors).
