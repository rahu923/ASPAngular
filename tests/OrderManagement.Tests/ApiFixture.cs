using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderManagement.Api.Security;
using OrderManagement.Application;
using OrderManagement.Infrastructure;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace OrderManagement.Tests;
public class TestMailbox : IEmailSender
{
    public Dictionary<string, string> Links { get; } = new();
    public Task SendAsync(string email, string subject, string link, CancellationToken ct) { Links[email] = link; return Task.CompletedTask; }
}
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    // WebApplicationFactory exercises the real middleware/controllers with an in-process
    // HTTP server. Persistence is REAL SQL Server, not the EF InMemory provider.
    public const string Password = "Integration!7aA-password";
    // Unique names isolate test data from the practice database and from other fixtures.
    public string DatabaseName { get; } = "OrderManagementTests_" + Guid.NewGuid().ToString("N");
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    public TestMailbox Mailbox { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string>
        {
            ["ConnectionStrings:Orders"] = $"Server=(localdb)\\MSSQLLocalDB;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True",
            ["Jwt:SigningKey"] = SigningKey,
            ["AllowedHosts"] = "*"
        }));
        // Replace only outbound email; keep real Identity/token/authorization/database behavior.
        builder.ConfigureServices(services => { services.RemoveAll<IEmailSender>(); services.AddSingleton<IEmailSender>(Mailbox); });
    }
    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await db.Database.MigrateAsync();
        await SeedData.InitializeAsync(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<IConfiguration>(), false);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var (email, role) in new[] { ("admin@test.local", "Admin"), ("manager@test.local", "Manager"), ("alice@test.local", "Customer"), ("bob@test.local", "Customer") })
        {
            var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }
    }
    async Task IAsyncLifetime.DisposeAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        // Only the unique test database created above is eligible for deletion.
        Assert.StartsWith("OrderManagementTests_", DatabaseName);
        Assert.Equal(DatabaseName, db.Database.GetDbConnection().Database);
        await db.Database.EnsureDeletedAsync();
        Dispose();
    }
    // Disable automatic cookies/redirects so BrowserClient can explicitly inspect cookie
    // handling and tests can detect a wrong login redirect instead of hiding it behind a 200 page.
    public BrowserClient Browser() => new(CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false, AllowAutoRedirect = false }));
    public async Task<BrowserClient> Login(string email)
    {
        var client = Browser();
        await client.Csrf();
        var response = await client.Send(HttpMethod.Post, "/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await client.Csrf();
        return client;
    }
}
public class BrowserClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient http;
    public CookieContainer Cookies { get; } = new();
    public BrowserClient(HttpClient http) => this.http = http;
    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null, bool csrf = true)
    {
        var uri = new Uri(http.BaseAddress!, path);
        var request = new HttpRequestMessage(method, uri);
        // CookieContainer models domain/path/Secure transport rules. It is useful HTTP
        // coverage, not a full browser implementation of every SameSite or rendering behavior.
        var cookie = Cookies.GetCookieHeader(uri);
        if (cookie.Length > 0) request.Headers.Add("Cookie", cookie);
        var xsrf = Cookies.GetCookies(uri)["XSRF-TOKEN"]?.Value;
        if (csrf && xsrf != null) request.Headers.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf));
        if (body != null) request.Content = JsonContent.Create(body, options: Json);
        var response = await http.SendAsync(request);
        if (response.Headers.TryGetValues("Set-Cookie", out var values)) foreach (var value in values) Cookies.SetCookies(uri, value);
        return response;
    }
    public async Task Csrf() => Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Get, "/api/auth/csrf")).StatusCode);
    public static async Task<T> Read<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Json))!;
    public void CopyCookiesTo(BrowserClient other)
    {
        var uri = new Uri("https://localhost/api/auth/");
        foreach (Cookie cookie in Cookies.GetCookies(uri)) other.Cookies.SetCookies(uri, cookie.ToString() + "; Path=" + cookie.Path + "; Secure");
    }
    public void Dispose() => http.Dispose();
}
