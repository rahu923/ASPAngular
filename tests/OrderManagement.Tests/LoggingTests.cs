using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderManagement.Api.Observability;
using OrderManagement.Infrastructure;
using Serilog;
using Xunit;
namespace OrderManagement.Tests;

public class LoggingTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture fixture;
    public LoggingTests(ApiFixture fixture) => this.fixture = fixture;
    [Fact]
    public async Task File_sql_and_seq_receive_structured_events_without_raw_exception_details()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await db.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "Logging.sql")));
        // Bridge the asynchronous ingestion callback to an awaitable test result.
        // RunContinuationsAsynchronously avoids executing test continuations inline on the HTTP handler.
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
        builder.Logging.ClearProviders();
        // Port 0 asks the OS for a free port; this receiver checks the real Seq HTTP
        // transport contract without requiring a Seq instance in the LocalDB test suite.
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var seq = builder.Build();
        seq.MapPost("/api/events/raw", async context =>
        {
            Stream stream = context.Request.Body;
            if (context.Request.Headers.ContentEncoding == "gzip") stream = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(stream);
            received.TrySetResult(await reader.ReadToEndAsync());
            context.Response.StatusCode = 201;
        });
        await seq.StartAsync();
        using (var probe = new HttpClient())
        {
            // Confirm the real local HTTP endpoint is reachable before exercising the batched sink.
            using var response = await probe.PostAsync(seq.Urls.Single() + "/api/events/raw", new StringContent("probe"));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = Path.Combine(AppContext.BaseDirectory, "logs", Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Observability:Console:Enabled"] = "true",
            ["Observability:File:Enabled"] = "true",
            ["Observability:File:Path"] = Path.Combine(directory, "test-.json"),
            ["Observability:Seq:Enabled"] = "true",
            ["Observability:Seq:ServerUrl"] = seq.Urls.Single(),
            ["Observability:SqlServer:Enabled"] = "true",
            ["ConnectionStrings:Logs"] = db.Database.GetConnectionString()!
        }).Build();
        var marker = Guid.NewGuid().ToString("N");
        using (var logger = LoggingConfiguration.Configure(new LoggerConfiguration(), config).CreateLogger())
        {
            logger.Information("Sink verification {Marker}", marker);
            logger.Error(new Exception("private-exception-sentinel"), "Must not be forwarded");
            var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(completed == received.Task, "Seq did not receive the batched event within 15 seconds.");
        } // Dispose flushes the SQL/Seq batches.
        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Contains(marker, payload);
        Assert.DoesNotContain("private-exception-sentinel", payload);
        var file = await File.ReadAllTextAsync(Directory.GetFiles(directory).Single());
        Assert.Contains(marker, file);
        Assert.DoesNotContain("private-exception-sentinel", file);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.ApplicationLogs WHERE LogEvent LIKE @marker";
        var parameter = command.CreateParameter(); parameter.ParameterName = "@marker"; parameter.Value = "%" + marker + "%"; command.Parameters.Add(parameter);
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        await seq.StopAsync();
    }
}
