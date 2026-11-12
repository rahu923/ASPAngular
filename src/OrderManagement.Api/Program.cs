using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using OrderManagement.Api.Controllers;
using OrderManagement.Api.Security;
using OrderManagement.Application;
using OrderManagement.Infrastructure;
using OrderManagement.Api.Observability;
using Serilog;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.HttpOverrides;
using System.Security.Cryptography.X509Certificates;
using OrderManagement.Api.Deployment;

// Walkthrough: composition first (configuration/services), then the ordered HTTP pipeline.
// CLI health/migration modes reuse this image without starting a second HTTP server.
if (args.Contains("--health-check")) { Environment.ExitCode = await ContainerHealthCheck.RunAsync(); return; }
var builder = WebApplication.CreateBuilder(args);
var migrate = args.Contains("--migrate");
// Later configuration providers override earlier ones. These optional local files are
// Development-only; Production relies on its environment/Key Vault-resolved settings.
if (builder.Environment.IsDevelopment()) builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
var local = Path.GetFullPath(builder.Configuration["DevelopmentStoragePath"] ?? Path.Combine(builder.Environment.ContentRootPath, "../../.local"));
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile(Path.Combine(local, "dev-secrets.json"), optional: true);
    Directory.CreateDirectory(Path.Combine(local, "keys"));
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(local, "keys"))).SetApplicationName("OrderManagement");
    if (string.IsNullOrWhiteSpace(builder.Configuration["Jwt:SigningKey"]))
    {
        var path = Path.Combine(local, "jwt-signing-key");
        if (!File.Exists(path)) File.WriteAllText(path, Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)));
        builder.Configuration["Jwt:SigningKey"] = File.ReadAllText(path);
    }
}
builder.Host.UseSerilog((context, _, logger) => LoggingConfiguration.Configure(logger, context.Configuration));
// Options pattern: bind a typed object, validate its attributes and fail early at host start.
builder.Services.AddOptions<JwtOptions>().Bind(builder.Configuration.GetSection("Jwt")).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();
// AddDbContext is scoped by default: one request shares one context across repositories/UoW.
// DbContext is not thread-safe; do not run parallel EF operations on that same instance.
builder.Services.AddDbContext<OrderDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Orders")));
builder.Services.Configure<ForwardedHeadersOptions>(o => ProxyConfiguration.Configure(o, builder.Configuration));
if (!builder.Environment.IsDevelopment() && builder.Environment.EnvironmentName != "Testing")
{
    // Data Protection secures antiforgery and Identity recovery tokens, not the JWT signature.
    // Shared, certificate-encrypted SQL keys let replicas/restarts decrypt those tokens.
    var protection = builder.Services.AddDataProtection().SetApplicationName("OrderManagement").PersistKeysToDbContext<OrderDbContext>();
    if (!migrate)
    {
        ProductionConfiguration.Validate(builder.Configuration);
        var certificate = builder.Configuration["DataProtection:CertificateBase64"];
        if (string.IsNullOrWhiteSpace(certificate)) throw new InvalidOperationException("Configure an encryption certificate for persisted Data Protection keys.");
        var keyCertificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(certificate), builder.Configuration["DataProtection:CertificatePassword"], X509KeyStorageFlags.EphemeralKeySet);
        if (!keyCertificate.HasPrivateKey || keyCertificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidOperationException("Data Protection requires a valid certificate with its private key.");
        protection.ProtectKeysWithCertificate(keyCertificate);
        var origin = builder.Configuration["PublicOrigin"];
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var publicOrigin) || publicOrigin.Scheme != "https") throw new InvalidOperationException("PublicOrigin must be an absolute HTTPS origin.");
    }
}
// IdentityCore supplies user/password/token-provider services without installing an
// application-cookie login scheme. JWT bearer below remains the sole authentication scheme.
builder.Services.AddIdentityCore<ApplicationUser>(o =>
{
    o.User.RequireUniqueEmail = true;
    o.Password.RequiredLength = 12;
    o.Password.RequireNonAlphanumeric = true;
    o.Lockout.MaxFailedAccessAttempts = 5;
    o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    o.SignIn.RequireConfirmedEmail = true;
}).AddRoles<IdentityRole>().AddSignInManager().AddEntityFrameworkStores<OrderDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
// Resolve validated options through DI when bearer options are built. Capturing a config
// snapshot too early would miss late configuration supplied by the integration-test host.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((o, configured) =>
{
    var jwt = configured.Value;
    o.MapInboundClaims = true;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
        // Allow a small clock difference, not the much larger default expiry tolerance.
        ClockSkew = TimeSpan.FromSeconds(5)
    };
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
    {
        context.Token = context.Request.Cookies[TokenService.AccessCookie];
        // NoResult prevents fallback to an Authorization header when this cookie is absent.
        if (string.IsNullOrEmpty(context.Token)) context.NoResult();
        return Task.CompletedTask;
    }
    };
});
builder.Services.AddAuthorization(o =>
{
    foreach (var permission in new[] { "ManageProducts", "ViewAllOrders", "ManageOrderStatus" })
        o.AddPolicy(permission, p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
});
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, OrderAccessHandler>();
builder.Services.AddAntiforgery(o =>
{
    // The internal cookie is HttpOnly; AuthController.Csrf exposes a DIFFERENT request
    // token via XSRF-TOKEN so Angular can send it in this header on unsafe requests.
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "oms-antiforgery";
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.Path = "/";
});
// Scoped services may depend on scoped DbContext; singleton handlers/cache must not capture it.
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<ProductService>();
builder.Services.AddScoped<ProductAdminService>();
builder.Services.AddScoped<OrderService>();
builder.Services.AddSingleton<CatalogCache>();
if (!builder.Configuration.GetValue("Email:Enabled", true))
    builder.Services.AddSingleton<IEmailSender, DisabledEmailSender>();
else if (builder.Environment.IsDevelopment()) builder.Services.AddSingleton<IEmailSender>(new DevelopmentEmailSender(Path.Combine(local, "mailbox")));
else if (builder.Environment.EnvironmentName == "Testing") builder.Services.AddSingleton<IEmailSender, UnconfiguredEmailSender>();
else
{
    var smtp = builder.Services.AddOptions<SmtpOptions>().Bind(builder.Configuration.GetSection("Smtp")).ValidateDataAnnotations();
    if (!migrate) smtp.ValidateOnStart();
    builder.Services.AddTransient<IEmailSender, SmtpEmailSender>();
}
var app = builder.Build();
if (migrate)
{
    // Explicit release operation with a separate DDL identity. Ordinary Production
    // startup must not race other replicas to modify schema or require schema permissions.
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    await db.Database.MigrateAsync();
    var loggingScript = Path.Combine(app.Environment.ContentRootPath, "sql", "Logging.sql");
    if (!File.Exists(loggingScript)) throw new InvalidOperationException("Logging schema script is missing from the published image.");
    await using (var logs = new SqlConnection(builder.Configuration.GetConnectionString("LogsMigration") ?? db.Database.GetConnectionString()))
    {
        await logs.OpenAsync();
        await using var command = logs.CreateCommand();
        command.CommandText = await File.ReadAllTextAsync(loggingScript);
        await command.ExecuteNonQueryAsync();
    }
    await SeedData.InitializeAsync(scope.ServiceProvider, builder.Configuration, false);
    return;
}
// Must run before HTTPS/authentication: only configured trusted proxies may establish
// the original scheme/IP. Trusting arbitrary forwarded headers permits caller spoofing.
// Capture the transport peer before forwarded headers replace RemoteIpAddress.
// Log only redirect diagnostics, never header values, cookies or connection strings.
app.Use(async (context, next) =>
{
    var transportPeer = context.Connection.RemoteIpAddress;
    await next();
    if (context.Response.StatusCode == StatusCodes.Status307TemporaryRedirect && !context.Request.IsHttps)
        app.Logger.LogWarning("HTTPS redirect issued with HTTP scheme after forwarded headers. Transport proxy: {TransportProxy}. Configure this verified hosting proxy in ReverseProxy:KnownProxies if it is trusted.", transportPeer);
});
app.UseForwardedHeaders();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var cause = exception;
    // EF can wrap SQL deadlocks/constraints in several exception layers. Inspect the
    // deepest cause to classify a conflict, but never return its database message to callers.
    while (cause?.InnerException != null) cause = cause.InnerException;
    var conflict = exception is DbUpdateConcurrencyException || cause is SqlException sql && (sql.Number == 1205 || sql.Number == 51000 || sql.Number == 2601 || sql.Number == 2627 || sql.Number == 547);
    var status = exception is BusinessException business ? business.Status : conflict ? 409 : 500;
    app.Logger.LogError("Request failed: {ExceptionType}; trace {TraceId}", exception?.GetType().Name, context.TraceIdentifier);
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = exception is BusinessException b ? b.Message : conflict ? "Conflicting change or constraint violation. Reload and retry." : "An unexpected error occurred.",
        Extensions = { ["traceId"] = context.TraceIdentifier }
    }, options: null, contentType: "application/problem+json");
}));
// Fill empty challenge/forbid/not-found bodies; existing validation/business bodies stay intact.
app.UseStatusCodePages(async statusContext =>
{
    var response = statusContext.HttpContext.Response;
    await response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = response.StatusCode,
        Title = response.StatusCode switch { 401 => "Authentication required.", 403 => "Permission denied.", 404 => "Resource not found.", 503 => "Service unavailable.", _ => "Request failed." },
        Extensions = { ["traceId"] = statusContext.HttpContext.TraceIdentifier }
    }, options: null, contentType: "application/problem+json");
});
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (!app.Environment.IsDevelopment()) context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' https://fonts.gstatic.com; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    if (context.Request.Path.StartsWithSegments("/api/auth") || context.Request.Path.StartsWithSegments("/api/admin") || context.Request.Path.StartsWithSegments("/api/orders"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
else app.UseHsts();
// Health probes carry no cookies or sensitive data and can originate over private HTTP.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api/health"), branch => branch.UseHttpsRedirection());
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    // Docker publishes Angular into wwwroot. Rewrite '/' to index.html before serving
    // assets; do not cache index.html like versioned JS or a deployment can load old asset names.
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = context.File.Name == "index.html" ? "no-store" : "public,max-age=3600";
    }
    });
}
app.UseRouting();
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnostics, context) =>
    {
        // Never log query strings, arbitrary URLs, headers, cookies or request/response bodies.
        diagnostics.Set("RequestPath", (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "/unmatched");
        diagnostics.Set("TraceId", context.TraceIdentifier);
    };
});
// Routing selects metadata; authentication builds User; authorization evaluates that
// user's endpoint policies. Their order matters. None of them validates the CSRF header.
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    // Authenticate first: antiforgery request tokens are bound to the current identity.
    // Apply to ALL unsafe endpoints, including anonymous login/refresh/recovery actions.
    if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new ProblemDetails { Status = 400, Title = "Invalid CSRF token. Reload and retry." }, options: null, contentType: "application/problem+json");
            return;
        }
    }
    await next();
});
// Liveness = process running; readiness also needs SQL connectivity and applied migrations.
// Neither is a guarantee that SMTP or asynchronous logging sinks are delivering successfully.
app.MapHealthChecks("/api/health/live");
app.MapGet("/api/health/ready", async (OrderDbContext db, CancellationToken ct) =>
{
    try { return await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any() ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503); }
    catch { return Results.StatusCode(503); }
});
app.MapControllers();
// Keep missing /api routes as JSON 404s rather than returning the SPA document.
app.MapFallback("/api/{**path}", () => Results.NotFound());
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"))) app.MapFallbackToFile("index.html");
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await SeedData.InitializeAsync(scope.ServiceProvider, builder.Configuration, builder.Environment.IsDevelopment());
}
app.Run();
public partial class Program { }
