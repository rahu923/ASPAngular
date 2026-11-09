using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderManagement.Application;
using OrderManagement.Infrastructure;

namespace OrderManagement.Api.Security;
public class JwtOptions
{
    // Bound and validated at startup in Program.cs. Length is a minimum requirement;
    // operators must supply a cryptographically random key, not a memorable password.
    [Required, MinLength(64)] public string SigningKey { get; set; } = "";
    [Required] public string Issuer { get; set; } = "OrderManagement";
    [Required] public string Audience { get; set; } = "OrderManagement.Browser";
    [Range(1, 15)] public int AccessMinutes { get; set; } = 5;
}
public record UserDto(string Id, string Email, IList<string> Roles, string[] Permissions);
public static class Permissions
{
    // Roles describe membership; permission claims describe allowed operations.
    // Claims are a snapshot when a JWT is issued, so role changes do not rewrite issued JWTs.
    public static string[] For(IEnumerable<string> roles) => roles.Any(r => r is "Admin" or "Manager")
        ? new[] { "ManageProducts", "ViewAllOrders", "ManageOrderStatus" } : Array.Empty<string>();
}
public record PermissionRequirement(string Permission) : IAuthorizationRequirement;
public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    // Authorization reads claims only AFTER the authentication middleware validates the JWT.
    // This handler is stateless, so DI can safely register it as a singleton.
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasClaim("permission", requirement.Permission)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
public record OrderAccessRequirement(bool Edit) : IAuthorizationRequirement;
public class OrderAccessHandler : AuthorizationHandler<OrderAccessRequirement, OrderDto>
{
    // Resource authorization needs both the user and this particular order.
    // Managers may read all orders; that permission does not grant editing another owner's items.
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OrderAccessRequirement requirement, OrderDto order)
    {
        if (context.User.FindFirstValue(ClaimTypes.NameIdentifier) == order.CustomerId || context.User.IsInRole("Admin")
            || (!requirement.Edit && context.User.HasClaim("permission", "ViewAllOrders"))) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
public class TokenService
{
    public const string AccessCookie = "oms-access";
    public const string RefreshCookie = "oms-refresh";
    private readonly OrderDbContext db;
    private readonly UserManager<ApplicationUser> users;
    private readonly JwtOptions options;
    public TokenService(OrderDbContext db, UserManager<ApplicationUser> users, IOptions<JwtOptions> options)
        => (this.db, this.users, this.options) = (db, users, options.Value);
    public async Task<UserDto> DescribeAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);
        return new(user.Id, user.Email!, roles, Permissions.For(roles));
    }
    // An opaque refresh token has high entropy, so a fast cryptographic hash is suitable
    // for lookup. PASSWORDS use Identity's slow password hasher, never this SHA-256 helper.
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string RandomToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    // HttpOnly prevents JS reading auth tokens; Secure restricts transport to HTTPS.
    // Neither replaces CSRF validation. Cookies are host/path scoped, not port scoped.
    private static CookieOptions Cookie(string path, DateTime? expires = null) => new()
    { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = path, Expires = expires, IsEssential = true };
    private async Task WriteAccessAsync(HttpContext context, ApplicationUser user)
    {
        var dto = await DescribeAsync(user);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.Email!), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) };
        claims.AddRange(dto.Roles.Select(x => new Claim(ClaimTypes.Role, x)));
        claims.AddRange(dto.Permissions.Select(x => new Claim("permission", x)));
        var expires = DateTime.UtcNow.AddMinutes(options.AccessMinutes);
        // HMAC signs for integrity/authenticity; it does not encrypt JWT claims.
        // Keep tokens out of response DTOs/logs and let the browser store the HttpOnly cookie.
        var jwt = new JwtSecurityToken(options.Issuer, options.Audience, claims, expires: expires,
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256));
        context.Response.Cookies.Append(AccessCookie, new JwtSecurityTokenHandler().WriteToken(jwt), Cookie("/api", expires));
    }
    public async Task IssueAsync(HttpContext context, ApplicationUser user, CancellationToken ct)
    {
        // One sign-in creates a family. Rotations stay in that family and cannot extend
        // its absolute seven-day expiry. The security stamp detects later account changes.
        var family = new RefreshFamily { Id = Guid.NewGuid(), UserId = user.Id, SecurityStamp = user.SecurityStamp!, ExpiresAtUtc = DateTime.UtcNow.AddDays(7) };
        var token = RandomToken();
        db.RefreshFamilies.Add(family);
        db.RefreshSessions.Add(new RefreshSession { Family = family, TokenHash = Hash(token), ExpiresAtUtc = family.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);
        await WriteAccessAsync(context, user);
        context.Response.Cookies.Append(RefreshCookie, token, Cookie("/api/auth", family.ExpiresAtUtc));
    }
    private Task LockAsync(Guid familyId, CancellationToken ct)
    {
        // A SQL application lock names a logical resource, not a table row. Transaction
        // ownership releases it on commit/rollback; all cooperating rotations/logouts use this name.
        // ExecuteSqlInterpolated binds resource as a parameter; timeout/failure is not ignored.
        var resource = "refresh-family:" + familyId;
        return db.Database.ExecuteSqlInterpolatedAsync($@"DECLARE @r int; EXEC @r = sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @r < 0 THROW 51000, 'Refresh lock unavailable', 1;", ct);
    }
    public async Task<bool> RotateAsync(HttpContext context, CancellationToken ct)
    {
        var raw = context.Request.Cookies[RefreshCookie];
        if (string.IsNullOrEmpty(raw)) return false;
        var hash = Hash(raw);
        // This first read discovers the lock key only. Do not track/trust its mutable
        // state: another request may consume the token before we acquire the family lock.
        var lookup = await db.RefreshSessions.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (lookup == null) return false;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAsync(lookup.FamilyId, ct);
        // Re-read AFTER the lock so reuse checks see the preceding transaction's commit,
        // rather than an old object cached in EF's change tracker.
        var token = await db.RefreshSessions.Include(x => x.Family).SingleAsync(x => x.Id == lookup.Id, ct);
        var family = token.Family;
        var user = await users.FindByIdAsync(family.UserId);
        var now = DateTime.UtcNow;
        if (token.ConsumedAtUtc != null || token.ExpiresAtUtc <= now || family.ExpiresAtUtc <= now || family.RevokedAtUtc != null
            || user == null || !user.EmailConfirmed || user.SecurityStamp != family.SecurityStamp || await users.IsLockedOutAsync(user))
        {
            // Replaying even one consumed token invalidates its successor chain.
            // Throwing before commit would roll back revocation and leave that chain usable.
            family.RevokedAtUtc = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct); // Persist revocation even though the request fails.
            return false;
        }
        // Consume-old + insert-successor share one transaction: never two live successors.
        // Keep consumed hashes until family expiry so future replay can still be recognized.
        token.ConsumedAtUtc = now;
        var successor = RandomToken();
        db.RefreshSessions.Add(new RefreshSession { FamilyId = family.Id, TokenHash = Hash(successor), ExpiresAtUtc = family.ExpiresAtUtc });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await WriteAccessAsync(context, user);
        context.Response.Cookies.Append(RefreshCookie, successor, Cookie("/api/auth", family.ExpiresAtUtc));
        return true;
    }
    public async Task LogoutAsync(HttpContext context, CancellationToken ct)
    {
        // Logout needs the refresh cookie, not a still-valid access JWT. The controller
        // allows anonymous requests but the unsafe request must still pass CSRF validation.
        var raw = context.Request.Cookies[RefreshCookie];
        if (!string.IsNullOrEmpty(raw))
        {
            var hash = Hash(raw);
            var familyId = await db.RefreshSessions.Where(x => x.TokenHash == hash).Select(x => (Guid?)x.FamilyId).SingleOrDefaultAsync(ct);
            if (familyId.HasValue)
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await LockAsync(familyId.Value, ct);
                var family = await db.RefreshFamilies.SingleAsync(x => x.Id == familyId, ct);
                family.RevokedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
        }
        Clear(context);
    }
    public static void Clear(HttpContext context)
    {
        // Deletion must match the original cookie paths; deleting only at '/' leaves
        // /api and /api/auth cookies intact. Issued access JWTs are not server-revoked here.
        context.Response.Cookies.Delete(AccessCookie, Cookie("/api"));
        context.Response.Cookies.Delete(RefreshCookie, Cookie("/api/auth"));
    }
    public async Task RevokeUserAsync(ApplicationUser user, CancellationToken ct)
    {
        // Rotations check the security stamp too, so a concurrent refresh cannot escape this revocation.
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) throw new BusinessException("Account changed. Retry the operation.", 409);
        var families = await db.RefreshFamilies.Where(x => x.UserId == user.Id && x.RevokedAtUtc == null).ToListAsync(ct);
        foreach (var family in families) family.RevokedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
