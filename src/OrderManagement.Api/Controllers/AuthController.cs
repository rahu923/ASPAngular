using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OrderManagement.Api.Security;
using OrderManagement.Application;
using OrderManagement.Infrastructure;
namespace OrderManagement.Api.Controllers;
public record Credentials([Required, EmailAddress] string Email, [Required, StringLength(128, MinimumLength = 12)] string Password);
public record EmailRequest([Required, EmailAddress] string Email);
public record ResetRequest([Required, EmailAddress] string Email, [Required] string Token, [Required, StringLength(128, MinimumLength = 12)] string Password);
public record ConfirmRequest([Required, EmailAddress] string Email, [Required] string Token);
public record ChangePasswordRequest([Required] string CurrentPassword, [Required, StringLength(128, MinimumLength = 12)] string NewPassword);
[ApiController, Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly UserManager<ApplicationUser> users;
    private readonly SignInManager<ApplicationUser> signIn;
    private readonly TokenService tokens;
    private readonly IEmailSender email;
    private readonly IConfiguration config;
    public AuthController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, TokenService tokens, IEmailSender email, IConfiguration config)
        => (this.users, this.signIn, this.tokens, this.email, this.config) = (users, signIn, tokens, email, config);
    [HttpGet("csrf"), AllowAnonymous]
    public IActionResult Csrf([FromServices] IAntiforgery antiforgery)
    {
        // Concept: antiforgery uses an HttpOnly internal cookie plus an identity-bound
        // request token. Only the request token is copied to a JS-readable cookie for Angular.
        // Never use this pattern for access/refresh tokens; those must stay HttpOnly.
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers.Pragma = "no-cache";
        var token = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append("XSRF-TOKEN", token.RequestToken!, new CookieOptions { Secure = true, HttpOnly = false, SameSite = SameSiteMode.Lax, Path = "/", IsEssential = true });
        return NoContent();
    }
    [HttpPost("register"), AllowAnonymous]
    public async Task<IActionResult> Register(Credentials input, CancellationToken ct)
    {
        // Validate the password equally for existing and new emails to avoid a response oracle.
        var candidate = new ApplicationUser { UserName = input.Email, Email = input.Email };
        foreach (var validator in users.PasswordValidators)
            if (!(await validator.ValidateAsync(users, candidate, input.Password)).Succeeded)
                throw new BusinessException("Password must contain uppercase, lowercase, a number and a symbol, with at least 12 characters.");
        if (await users.FindByEmailAsync(input.Email) == null)
        {
            var user = new ApplicationUser { UserName = input.Email, Email = input.Email };
            var result = await users.CreateAsync(user, input.Password);
            if (!result.Succeeded)
            {
                if (result.Errors.Any(x => x.Code.StartsWith("Password"))) throw new BusinessException("Password must contain uppercase, lowercase, a number and a symbol, with at least 12 characters.");
            }
            else
            {
                var role = await users.AddToRoleAsync(user, "Customer");
                if (!role.Succeeded) throw new InvalidOperationException("Customer role assignment failed.");
                await SendLinkAsync(user, false, ct);
            }
        }
        // Same acknowledgement for new/existing emails limits account enumeration.
        // The input DTO has no role: public callers cannot promote themselves to Admin.
        return Accepted(new { message = "If registration is available, check your email to confirm your account." });
    }
    [HttpPost("login"), AllowAnonymous]
    public async Task<IActionResult> Login(Credentials input, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(input.Email);
        if (user == null)
        {
            // Perform a password hash verification even for a missing account.
            _ = new PasswordHasher<ApplicationUser>().VerifyHashedPassword(new ApplicationUser(), DummyHash.Value, input.Password);
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials or account unavailable." });
        }
        // CheckPasswordSignInAsync uses Identity's password hasher and lockout state,
        // but does not issue its own login cookie. TokenService owns cookie JWT issuance.
        var result = await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true);
        if (!result.Succeeded || !user.EmailConfirmed) return Unauthorized(new ProblemDetails { Status = 401, Title = "Invalid credentials or account unavailable." });
        await tokens.IssueAsync(HttpContext, user, ct);
        return Ok(await tokens.DescribeAsync(user));
    }
    private static readonly Lazy<string> DummyHash = new(() => new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString()));
    [HttpPost("refresh"), AllowAnonymous]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        if (await tokens.RotateAsync(HttpContext, ct)) return NoContent();
        TokenService.Clear(HttpContext);
        return Unauthorized(new ProblemDetails { Status = 401, Title = "Session expired. Sign in again." });
    }
    [HttpPost("logout"), AllowAnonymous]
    public async Task<IActionResult> Logout(CancellationToken ct) { await tokens.LogoutAsync(HttpContext, ct); return NoContent(); }
    [HttpGet("me"), Authorize]
    public async Task<IActionResult> Me()
    {
        // User details are enough for UI restoration; never serialize the JWT or refresh cookie.
        Response.Headers.CacheControl = "no-store";
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = userId == null ? null : await users.FindByIdAsync(userId);
        return user == null ? Unauthorized() : Ok(await tokens.DescribeAsync(user));
    }
    [HttpPost("change-password"), Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest input, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var user = userId == null ? null : await users.FindByIdAsync(userId);
        if (user == null) return Unauthorized();
        // Identity validates the current password and new-password policy and updates its hash.
        // Revoke refresh families afterward; existing access JWTs still live until expiry.
        var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded) throw new BusinessException("Password change failed. Check the current password and password requirements.");
        await tokens.RevokeUserAsync(user, ct);
        TokenService.Clear(HttpContext);
        return NoContent();
    }
    [HttpPost("forgot-password"), AllowAnonymous]
    public async Task<IActionResult> Forgot(EmailRequest input, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(input.Email);
        if (user != null && user.EmailConfirmed) await SendLinkAsync(user, true, ct);
        return Accepted(new { message = "If the account is eligible, check your email." });
    }
    [HttpPost("resend-confirmation"), AllowAnonymous]
    public async Task<IActionResult> Resend(EmailRequest input, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(input.Email);
        if (user != null && !user.EmailConfirmed) await SendLinkAsync(user, false, ct);
        return Accepted(new { message = "If the account is eligible, check your email." });
    }
    [HttpPost("reset-password"), AllowAnonymous]
    public async Task<IActionResult> Reset(ResetRequest input, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(input.Email);
        if (user == null || !(await users.ResetPasswordAsync(user, input.Token, input.Password)).Succeeded) throw new BusinessException("Reset link invalid or expired, or password requirements not met.");
        await tokens.RevokeUserAsync(user, ct);
        TokenService.Clear(HttpContext);
        return NoContent();
    }
    [HttpPost("confirm-email"), AllowAnonymous]
    public async Task<IActionResult> Confirm(ConfirmRequest input)
    {
        var user = await users.FindByEmailAsync(input.Email);
        if (user == null || !(await users.ConfirmEmailAsync(user, input.Token)).Succeeded) throw new BusinessException("Confirmation link invalid or expired.");
        return NoContent();
    }
    private async Task SendLinkAsync(ApplicationUser user, bool reset, CancellationToken ct)
    {
        var token = reset ? await users.GeneratePasswordResetTokenAsync(user) : await users.GenerateEmailConfirmationTokenAsync(user);
        // Use a configured trusted origin, not an attacker-controlled request Host header,
        // when creating account-recovery links. Identity generated the signed recovery token above.
        var origin = config["PublicOrigin"] ?? "https://localhost:4200";
        // Fragment keeps recovery tokens out of web-server request paths and access logs.
        var link = $"{origin}/account/{(reset ? "reset-password" : "confirm-email")}#email={Uri.EscapeDataString(user.Email!)}&token={Uri.EscapeDataString(token)}";
        await email.SendAsync(user.Email!, reset ? "Reset password" : "Confirm email", link, ct);
    }
}
