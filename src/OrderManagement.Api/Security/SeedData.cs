using Microsoft.AspNetCore.Identity;
using OrderManagement.Infrastructure;
namespace OrderManagement.Api.Security;
public static class SeedData
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, bool development)
    {
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var role in new[] { "Admin", "Manager", "Customer" })
            if (!await roles.RoleExistsAsync(role))
            {
                var result = await roles.CreateAsync(new IdentityRole(role));
                if (!result.Succeeded) throw new InvalidOperationException("Role seeding failed.");
            }
        var password = config["DevelopmentAccountsPassword"];
        if (!development || string.IsNullOrEmpty(password)) return;
        foreach (var role in new[] { "Admin", "Manager", "Customer" })
        {
            var email = role.ToLowerInvariant() + "@orders.local";
            if (await users.FindByEmailAsync(email) != null) continue;
            var user = new ApplicationUser { Email = email, UserName = email, EmailConfirmed = true };
            if (!(await users.CreateAsync(user, password)).Succeeded || !(await users.AddToRoleAsync(user, role)).Succeeded)
                throw new InvalidOperationException("Development account seeding failed. Check password policy.");
        }
    }
}
