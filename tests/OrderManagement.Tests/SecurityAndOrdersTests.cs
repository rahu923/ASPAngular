using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using OrderManagement.Application;
using OrderManagement.Domain;
using OrderManagement.Infrastructure;
using Xunit;
namespace OrderManagement.Tests;
public class SecurityAndOrdersTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture fixture;
    public SecurityAndOrdersTests(ApiFixture fixture) => this.fixture = fixture;
    private static object ProductBody(string sku, int stock, decimal price = 12.5m, string? rowVersion = null) => new { name = "Test " + sku, sku, description = "Integration test product", price, stockQuantity = stock, rowVersion };
    private async Task<ProductDto> Product(int stock = 10, decimal price = 12.5m)
    {
        using var admin = await fixture.Login("admin@test.local");
        var response = await admin.Send(HttpMethod.Post, "/api/products", ProductBody("T-" + Guid.NewGuid().ToString("N").ToUpperInvariant(), stock, price));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BrowserClient.Read<ProductDto>(response);
    }
    private async Task<OrderDto> Create(BrowserClient client, int productId, int quantity = 2)
    {
        var response = await client.Send(HttpMethod.Post, "/api/orders", new { items = new[] { new { productId, quantity } } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await BrowserClient.Read<OrderDto>(response);
    }
    private async Task<ProductDto> GetProduct(int id)
    {
        using var client = fixture.Browser();
        return await BrowserClient.Read<ProductDto>(await client.Send(HttpMethod.Get, "/api/products/" + id));
    }
    [Fact]
    public async Task Login_cookie_flags_failures_csrf_and_unauthenticated_responses()
    {
        using var browser = fixture.Browser();
        var anonymous = await browser.Send(HttpMethod.Get, "/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(anonymous.Headers.Location);
        var noCsrf = await browser.Send(HttpMethod.Post, "/api/auth/login", new { email = "alice@test.local", password = ApiFixture.Password }, false);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        await browser.Csrf();
        var failure = await browser.Send(HttpMethod.Post, "/api/auth/login", new { email = "missing@test.local", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        var wrong = await browser.Send(HttpMethod.Post, "/api/auth/login", new { email = "alice@test.local", password = "Wrong!7aA-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var success = await browser.Send(HttpMethod.Post, "/api/auth/login", new { email = "alice@test.local", password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var cookies = success.Headers.GetValues("Set-Cookie").ToArray();
        Assert.Equal(2, cookies.Length);
        // Parse attributes rather than relying on casing or header formatting.
        var parsedCookies = cookies.Select(c => SetCookieHeaderValue.Parse(c)).ToArray();
        Assert.All(parsedCookies, c =>
        {
            Assert.True(c.Secure);
            Assert.True(c.HttpOnly);
            Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, c.SameSite);
        });
        Assert.Contains(parsedCookies, c => c.Name == "oms-access" && c.Path == "/api");
        Assert.Contains(parsedCookies, c => c.Name == "oms-refresh" && c.Path == "/api/auth");
        var json = await success.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", json); Assert.DoesNotContain("refreshToken", json);
        await browser.Csrf();
        Assert.Equal(HttpStatusCode.OK, (await browser.Send(HttpMethod.Get, "/api/auth/me")).StatusCode);
    }
    [Fact]
    public async Task Rotation_replay_revokes_the_entire_family_and_hashes_tokens()
    {
        using var browser = await fixture.Login("alice@test.local");
        using var replay = fixture.Browser(); browser.CopyCookiesTo(replay);
        var raw = browser.Cookies.GetCookies(new Uri("https://localhost/api/auth/"))["oms-refresh"]!.Value;
        Assert.Equal(HttpStatusCode.NoContent, (await browser.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
        Assert.NotEqual(raw, browser.Cookies.GetCookies(new Uri("https://localhost/api/auth/"))["oms-refresh"]!.Value);
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            Assert.False(await db.RefreshSessions.AnyAsync(x => x.TokenHash == raw));
            Assert.True(await db.RefreshSessions.AllAsync(x => x.TokenHash.Length == 64));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
    }
    [Fact]
    public async Task Concurrent_refresh_has_one_winner_and_reuse_revokes_winner()
    {
        using var a = await fixture.Login("alice@test.local"); using var b = fixture.Browser(); a.CopyCookiesTo(b);
        var responses = await Task.WhenAll(a.Send(HttpMethod.Post, "/api/auth/refresh", new { }), b.Send(HttpMethod.Post, "/api/auth/refresh", new { }));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Unauthorized);
        var winner = responses[0].StatusCode == HttpStatusCode.NoContent ? a : b;
        Assert.Equal(HttpStatusCode.Unauthorized, (await winner.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
    }
    [Fact]
    public async Task Logout_revokes_refresh_but_access_remains_valid_until_expiry()
    {
        using var browser = await fixture.Login("alice@test.local"); using var replay = fixture.Browser(); browser.CopyCookiesTo(replay);
        var response = await browser.Send(HttpMethod.Post, "/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Send(HttpMethod.Get, "/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await replay.Send(HttpMethod.Get, "/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
    }
    [Fact]
    public async Task Expired_refresh_is_rejected()
    {
        using var browser = await fixture.Login("bob@test.local");
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var id = await db.Users.Where(x => x.Email == "bob@test.local").Select(x => x.Id).SingleAsync();
            var families = await db.RefreshFamilies.Where(x => x.UserId == id).ToListAsync();
            foreach (var family in families) family.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
    }
    [Fact]
    public async Task Roles_policies_csrf_and_ownership_are_enforced()
    {
        var product = await Product();
        using var alice = await fixture.Login("alice@test.local"); using var bob = await fixture.Login("bob@test.local"); using var manager = await fixture.Login("manager@test.local");
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Send(HttpMethod.Post, "/api/products", ProductBody("DENIED-1", 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Send(HttpMethod.Get, "/api/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Send(HttpMethod.Post, "/api/products", ProductBody("CSRF-1", 1), false)).StatusCode);
        var order = await Create(alice, product.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Send(HttpMethod.Get, $"/api/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Send(HttpMethod.Get, $"/api/orders/{order.Id}/items")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.Send(HttpMethod.Put, $"/api/orders/{order.Id}/items/{order.Items[0].Id}", new { quantity = 1, rowVersion = order.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.Send(HttpMethod.Get, $"/api/orders/{order.Id}")).StatusCode);
        var list = await BrowserClient.Read<Page<OrderDto>>(await bob.Send(HttpMethod.Get, "/api/orders"));
        Assert.DoesNotContain(list.Items, x => x.Id == order.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.Send(HttpMethod.Put, $"/api/orders/{order.Id}/status", new { status = "Confirmed", rowVersion = order.RowVersion })).StatusCode);
    }
    [Fact]
    public async Task Order_prices_stock_snapshot_cancellation_and_history()
    {
        var product = await Product(10, 12.5m);
        using var alice = await fixture.Login("alice@test.local"); using var admin = await fixture.Login("admin@test.local");
        var order = await Create(alice, product.Id, 2);
        Assert.Equal(25m, order.TotalAmount); Assert.Equal(8, (await GetProduct(product.Id)).StockQuantity);
        var current = await GetProduct(product.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Put, $"/api/products/{product.Id}", ProductBody(product.Sku, 8, 20m, current.RowVersion))).StatusCode);
        var updated = await BrowserClient.Read<OrderDto>(await alice.Send(HttpMethod.Put, $"/api/orders/{order.Id}/items/{order.Items[0].Id}", new { quantity = 3, rowVersion = order.RowVersion }));
        Assert.Equal(37.5m, updated.TotalAmount); Assert.Equal(12.5m, updated.Items[0].UnitPrice);
        var cancelled = await BrowserClient.Read<OrderDto>(await alice.Send(HttpMethod.Post, $"/api/orders/{order.Id}/cancel", new { rowVersion = updated.RowVersion }));
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status); Assert.Equal(10, (await GetProduct(product.Id)).StockQuantity);
        Assert.Equal(HttpStatusCode.OK, (await alice.Send(HttpMethod.Post, $"/api/orders/{order.Id}/cancel", new { rowVersion = updated.RowVersion })).StatusCode);
        Assert.Equal(10, (await GetProduct(product.Id)).StockQuantity);
        current = await GetProduct(product.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Delete, $"/api/products/{product.Id}?rowVersion={Uri.EscapeDataString(current.RowVersion)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.Send(HttpMethod.Get, $"/api/orders/{order.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Send(HttpMethod.Get, $"/api/products/{product.Id}")).StatusCode);
    }
    [Fact]
    public async Task Invalid_quantities_and_insufficient_stock_roll_back_entire_order()
    {
        var first = await Product(5); var second = await Product(0);
        using var alice = await fixture.Login("alice@test.local");
        var invalid = await alice.Send(HttpMethod.Post, "/api/orders", new { items = new[] { new { productId = first.Id, quantity = 0 } } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var failure = await alice.Send(HttpMethod.Post, "/api/orders", new { items = new[] { new { productId = first.Id, quantity = 2 }, new { productId = second.Id, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Conflict, failure.StatusCode); Assert.Equal(5, (await GetProduct(first.Id)).StockQuantity);
        using var scope = fixture.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.False(await db.OrderItems.AnyAsync(x => x.ProductId == first.Id));
    }
    [Fact]
    public async Task Stale_rowversion_and_invalid_transitions_are_rejected()
    {
        var product = await Product(); using var alice = await fixture.Login("alice@test.local"); using var manager = await fixture.Login("manager@test.local"); using var admin = await fixture.Login("admin@test.local");
        var order = await Create(alice, product.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.Send(HttpMethod.Put, $"/api/orders/{order.Id}/status", new { status = "Shipped", rowVersion = order.RowVersion })).StatusCode);
        var confirmed = await BrowserClient.Read<OrderDto>(await manager.Send(HttpMethod.Put, $"/api/orders/{order.Id}/status", new { status = "Confirmed", rowVersion = order.RowVersion }));
        Assert.Equal(HttpStatusCode.Conflict, (await manager.Send(HttpMethod.Put, $"/api/orders/{order.Id}/status", new { status = "Shipped", rowVersion = order.RowVersion })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.Send(HttpMethod.Put, $"/api/orders/{order.Id}/items/{order.Items[0].Id}", new { quantity = 4, rowVersion = confirmed.RowVersion })).StatusCode);
        var current = await GetProduct(product.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Put, $"/api/products/{product.Id}", ProductBody(product.Sku, 8, 15, current.RowVersion))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Send(HttpMethod.Put, $"/api/products/{product.Id}", ProductBody(product.Sku, 8, 15, current.RowVersion))).StatusCode);
    }
    [Fact]
    public async Task Concurrent_last_stock_unit_can_only_be_reserved_once()
    {
        var product = await Product(1); using var alice = await fixture.Login("alice@test.local"); using var bob = await fixture.Login("bob@test.local");
        var input = new { items = new[] { new { productId = product.Id, quantity = 1 } } };
        var responses = await Task.WhenAll(alice.Send(HttpMethod.Post, "/api/orders", input), bob.Send(HttpMethod.Post, "/api/orders", input));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(0, (await GetProduct(product.Id)).StockQuantity);
    }
    [Fact]
    public async Task Registration_confirmation_and_password_reset_use_identity_and_customer_role()
    {
        using var client = fixture.Browser(); await client.Csrf(); var email = "new-" + Guid.NewGuid().ToString("N") + "@test.local";
        var register = await client.Send(HttpMethod.Post, "/api/auth/register", new { email, password = ApiFixture.Password, role = "Admin" });
        Assert.Equal(HttpStatusCode.Accepted, register.StatusCode); Assert.DoesNotContain("token", await register.Content.ReadAsStringAsync());
        var link = fixture.Mailbox.Links[email]; var fragment = link.Split('#')[1].Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.Send(HttpMethod.Post, "/api/auth/confirm-email", new { email, token = fragment["token"] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = ApiFixture.Password })).StatusCode); await client.Csrf();
        using var scope = fixture.Services.CreateScope(); var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>(); var user = await users.FindByEmailAsync(email);
        Assert.NotNull(user);
        Assert.Equal(new[] { "Customer" }, await users.GetRolesAsync(user));
        Assert.Equal(HttpStatusCode.Accepted, (await client.Send(HttpMethod.Post, "/api/auth/forgot-password", new { email })).StatusCode);
        link = fixture.Mailbox.Links[email]; fragment = link.Split('#')[1].Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        Assert.Equal(HttpStatusCode.NoContent, (await client.Send(HttpMethod.Post, "/api/auth/reset-password", new { email, token = fragment["token"], password = "Updated!7aA-password" })).StatusCode);
        await client.Csrf(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = ApiFixture.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = "Updated!7aA-password" })).StatusCode);
    }
    [Fact]
    public async Task Nested_add_remove_and_concurrent_cancellation_preserve_stock()
    {
        var first = await Product(10); var second = await Product(10, 5m);
        using var alice = await fixture.Login("alice@test.local");
        var order = await Create(alice, first.Id, 2);
        var added = await alice.Send(HttpMethod.Post, $"/api/orders/{order.Id}/items", new { productId = second.Id, quantity = 3, rowVersion = order.RowVersion });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode); order = await BrowserClient.Read<OrderDto>(added);
        Assert.Equal(40m, order.TotalAmount); Assert.Equal(7, (await GetProduct(second.Id)).StockQuantity);
        var secondItem = order.Items.Single(x => x.ProductId == second.Id);
        var removed = await alice.Send(HttpMethod.Delete, $"/api/orders/{order.Id}/items/{secondItem.Id}?rowVersion={Uri.EscapeDataString(order.RowVersion)}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode); order = await BrowserClient.Read<OrderDto>(removed);
        Assert.Equal(25m, order.TotalAmount); Assert.Equal(10, (await GetProduct(second.Id)).StockQuantity);
        using var other = fixture.Browser(); alice.CopyCookiesTo(other);
        var input = new { rowVersion = order.RowVersion };
        var responses = await Task.WhenAll(alice.Send(HttpMethod.Post, $"/api/orders/{order.Id}/cancel", input), other.Send(HttpMethod.Post, $"/api/orders/{order.Id}/cancel", input));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(10, (await GetProduct(first.Id)).StockQuantity);
    }
    [Fact]
    public async Task Admin_role_and_lock_operations_revoke_refresh_and_password_change_uses_identity()
    {
        var email = "managed-" + Guid.NewGuid().ToString("N") + "@test.local"; string id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Email = email, UserName = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, ApiFixture.Password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, "Customer")).Succeeded); id = user.Id;
        }
        using var target = await fixture.Login(email); using var admin = await fixture.Login("admin@test.local");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Put, $"/api/admin/users/{id}/role", new { role = "Manager" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await target.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
        using var promoted = await fixture.Login(email);
        Assert.Equal(HttpStatusCode.OK, (await promoted.Send(HttpMethod.Get, "/api/products/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Put, $"/api/admin/users/{id}/lock", new { locked = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await promoted.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
        using var locked = fixture.Browser(); await locked.Csrf();
        Assert.Equal(HttpStatusCode.Unauthorized, (await locked.Send(HttpMethod.Post, "/api/auth/login", new { email, password = ApiFixture.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Put, $"/api/admin/users/{id}/lock", new { locked = false })).StatusCode);
        using var unlocked = await fixture.Login(email); using var oldSession = fixture.Browser(); unlocked.CopyCookiesTo(oldSession);
        Assert.Equal(HttpStatusCode.NoContent, (await unlocked.Send(HttpMethod.Post, "/api/auth/change-password", new { currentPassword = ApiFixture.Password, newPassword = "Changed!7aA-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.Send(HttpMethod.Post, "/api/auth/refresh", new { })).StatusCode);
    }
    [Fact]
    public async Task Repeated_password_failures_lock_the_account()
    {
        var email = "lockout-" + Guid.NewGuid().ToString("N") + "@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Email = email, UserName = email, EmailConfirmed = true }; Assert.True((await users.CreateAsync(user, ApiFixture.Password)).Succeeded);
        }
        using var client = fixture.Browser(); await client.Csrf();
        for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(HttpStatusCode.Unauthorized, (await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = "Wrong!7aA-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = ApiFixture.Password })).StatusCode);
    }
    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    public async Task Invalid_jwt_is_rejected(string invalid)
    {
        using var client = fixture.Browser();
        var key = invalid == "signature" ? new string('x', 88) : fixture.SigningKey;
        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: invalid == "issuer" ? "wrong" : "OrderManagement",
            audience: invalid == "audience" ? "wrong" : "OrderManagement.Browser",
            expires: invalid == "expiry" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key)), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        var raw = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(jwt);
        client.Cookies.SetCookies(new Uri("https://localhost/api/"), $"oms-access={raw}; Path=/api; Secure");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Send(HttpMethod.Get, "/api/auth/me")).StatusCode);
    }
}
