using Microsoft.Data.SqlClient;
namespace OrderManagement.Api.Deployment;
public static class ProductionConfiguration
{
    // Fail configuration early rather than accepting a local SQL/certificate shortcut in
    // Production. These checks do not prove network access, delivery or cloud readiness.
    public static void Validate(IConfiguration configuration)
    {
        foreach (var name in new[] { "Orders", "Logs" })
        {
            if (name == "Logs" && !configuration.GetValue<bool>("Observability:SqlServer:Enabled")) continue;
            var value = configuration.GetConnectionString(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Configure the {name} production SQL connection.");
            var sql = new SqlConnectionStringBuilder(value);
            if (sql.DataSource.Contains("localdb", StringComparison.OrdinalIgnoreCase) || sql.TrustServerCertificate
                || new[] { "false", "optional", "no" }.Contains(sql["Encrypt"].ToString()?.ToLowerInvariant()))
                throw new InvalidOperationException($"The {name} production connection requires encrypted SQL with a trusted server certificate.");
        }
        if (configuration.GetValue<bool>("Observability:Seq:Enabled")
            && (!Uri.TryCreate(configuration["Observability:Seq:ServerUrl"], UriKind.Absolute, out var seq) || seq.Scheme != "https"))
            throw new InvalidOperationException("Production Seq ingestion requires HTTPS.");
    }
}
