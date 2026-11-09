using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderManagement.Api.Security;
using OrderManagement.Application;
using OrderManagement.Infrastructure;
namespace OrderManagement.Api.Controllers;
public record RoleRequest([Required, RegularExpression("^(Admin|Manager|Customer)$")] string Role);
public record LockRequest(bool Locked);
// Role-based backend boundary: a Manager's product/status permissions never grant this controller.
[ApiController, Route("api/admin"), Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly UserManager<ApplicationUser> users;
    private readonly RoleManager<IdentityRole> roles;
    private readonly TokenService tokens;
    private readonly OrderDbContext db;
    public AdminController(UserManager<ApplicationUser> users, RoleManager<IdentityRole> roles, TokenService tokens, OrderDbContext db)
        => (this.users, this.roles, this.tokens, this.db) = (users, roles, tokens, db);
    [HttpGet("roles")]
    public async Task<string[]> Roles(CancellationToken ct) => await roles.Roles.Select(x => x.Name!).OrderBy(x => x).ToArrayAsync(ct);
    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery] ProductQuery query, CancellationToken ct)
    {
        var source = users.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Search)) source = source.Where(x => x.Email!.Contains(query.Search));
        var count = await source.CountAsync(ct);
        var page = await source.OrderBy(x => x.Email).Skip((query.PageNumber - 1) * query.PageSize).Take(query.PageSize)
            .Select(x => new { x.Id, x.Email, x.EmailConfirmed, x.LockoutEnd }).ToListAsync(ct);
        var ids = page.Select(x => x.Id).ToArray();
        // Fetch memberships for the page in one query rather than calling GetRolesAsync
        // per displayed user (N+1 queries). The final in-memory projection joins these small results.
        var memberships = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ids.Contains(ur.UserId) select new { ur.UserId, r.Name }).ToListAsync(ct);
        return Ok(new { items = page.Select(x => new { x.Id, x.Email, x.EmailConfirmed, locked = x.LockoutEnd > DateTimeOffset.UtcNow, roles = memberships.Where(m => m.UserId == x.Id).Select(m => m.Name).ToArray() }), totalCount = count, query.PageNumber, query.PageSize });
    }
    [HttpPut("users/{id}/role")]
    public async Task<IActionResult> Assign(string id, RoleRequest input, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id) ?? throw new BusinessException("User not found.", 404);
        if (id == users.GetUserId(User)) throw new BusinessException("Use a second administrator to change your own privileges.");
        if (!await roles.RoleExistsAsync(input.Role)) throw new BusinessException("Role not found.", 404);
        // Identity operations call SaveChanges internally. The explicit transaction keeps
        // remove-old-role, add-new-role and refresh revocation together if any later step fails.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var remove = await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
        if (!remove.Succeeded || !(await users.AddToRoleAsync(user, input.Role)).Succeeded) throw new BusinessException("Role update conflicted. Retry.", 409);
        await tokens.RevokeUserAsync(user, ct);
        await tx.CommitAsync(ct);
        return NoContent();
    }
    [HttpPut("users/{id}/lock")]
    public async Task<IActionResult> Lock(string id, LockRequest input, CancellationToken ct)
    {
        if (id == users.GetUserId(User)) throw new BusinessException("You cannot lock your own account.");
        var user = await users.FindByIdAsync(id) ?? throw new BusinessException("User not found.", 404);
        var result = await users.SetLockoutEndDateAsync(user, input.Locked ? DateTimeOffset.UtcNow.AddYears(100) : null);
        if (!result.Succeeded) throw new BusinessException("User update failed.", 409);
        await tokens.RevokeUserAsync(user, ct);
        return NoContent();
    }
}
