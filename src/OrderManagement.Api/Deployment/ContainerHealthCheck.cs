using System.Security.Cryptography.X509Certificates;
namespace OrderManagement.Api.Deployment;
public static class ContainerHealthCheck
{
    public static async Task<int> RunAsync()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var url = Environment.GetEnvironmentVariable("HealthCheckUrl") ?? "http://localhost:8080/api/health/ready";
        if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development" && url.StartsWith("https://localhost:"))
        {
            using var expected = new X509Certificate2(Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path")!,
                Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Password"));
            var thumbprint = expected.Thumbprint;
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == thumbprint;
        }
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        try { using var response = await client.GetAsync(url); return response.IsSuccessStatusCode ? 0 : 1; }
        catch { return 1; }
    }
}
