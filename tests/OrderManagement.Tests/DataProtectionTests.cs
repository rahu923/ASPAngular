using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderManagement.Infrastructure;
using Xunit;
namespace OrderManagement.Tests;
public class DataProtectionTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture fixture;
    public DataProtectionTests(ApiFixture fixture) => this.fixture = fixture;
    [Fact]
    public async Task Encrypted_sql_key_ring_survives_a_new_service_provider()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var connection = db.Database.GetConnectionString();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=OrderManagement-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        ServiceProvider Provider()
        {
            // A fresh DI provider models a new process/replica with no in-memory key cache.
            // Both providers share the SQL key ring, application name, purpose and certificate.
            var services = new ServiceCollection().AddLogging();
            services.AddDbContext<OrderDbContext>(o => o.UseSqlServer(connection));
            services.AddDataProtection().SetApplicationName("OrderManagement-KeyRing-Test")
                .PersistKeysToDbContext<OrderDbContext>().ProtectKeysWithCertificate(certificate);
            return services.BuildServiceProvider();
        }
        string protectedValue;
        using (var first = Provider()) protectedValue = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Protect("private-payload");
        var xml = await db.DataProtectionKeys.AsNoTracking().Select(x => x.Xml).ToListAsync();
        // Persistence alone is insufficient: check encrypted-at-rest key XML AND prove
        // a second provider can recover the protected value using that persisted key.
        Assert.Contains(xml, value => value != null && value.Contains("encryptedSecret"));
        using var second = Provider();
        Assert.Equal("private-payload", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("restart-test").Unprotect(protectedValue));
    }
}
