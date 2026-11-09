using System.Data;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.MSSqlServer;
namespace OrderManagement.Api.Observability;

public static class LoggingConfiguration
{
    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration config)
    {
        // Structured events keep named properties for searching/correlation. Framework
        // noise is reduced; enrichers add common context to every enabled sink.
        logger.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Fatal)
            .Enrich.FromLogContext().Enrich.WithProperty("Application", "OrderManagement")
            // Exception messages can contain database values. The exception middleware logs safe type/trace summaries.
            .Filter.ByExcluding(e => e.Exception != null);
        if (config.GetValue("Observability:Console:Enabled", true))
            logger.WriteTo.Console(new JsonFormatter(renderMessage: true));
        // Daily/size rollover and retention bound file growth. A persistent volume/mount
        // is still required if these files must survive container replacement.
        if (config.GetValue("Observability:File:Enabled", true))
            logger.WriteTo.File(new JsonFormatter(renderMessage: true),
                config["Observability:File:Path"] ?? "logs/api-.json",
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024, rollOnFileSizeLimit: true, shared: true);
        if (config.GetValue<bool>("Observability:Seq:Enabled"))
        {
            var url = config["Observability:Seq:ServerUrl"];
            if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || (address.Scheme != "https" && address.Scheme != "http"))
                throw new InvalidOperationException("Configure a valid Seq server URL.");
            // The API key comes from configuration/Key Vault; never log the configuration object.
            logger.WriteTo.Seq(url!, apiKey: config["Observability:Seq:ApiKey"]);
        }
        if (config.GetValue<bool>("Observability:SqlServer:Enabled"))
        {
            var connection = config.GetConnectionString("Logs");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Configure ConnectionStrings:Logs when enabling the SQL sink.");
            var columns = new ColumnOptions();
            columns.Store.Clear();
            columns.Store.Add(StandardColumn.Message);
            columns.Store.Add(StandardColumn.Level);
            columns.Store.Add(StandardColumn.TimeStamp);
            columns.Store.Add(StandardColumn.LogEvent);
            columns.TimeStamp.DataType = SqlDbType.DateTimeOffset;
            columns.TimeStamp.ConvertToUtc = true;
            // Batch operational logs without a database write per request. Provision the
            // table with the migration identity; the runtime writer must not need DDL rights.
            // Asynchronous sinks can fail independently and are not a transactional audit trail.
            logger.WriteTo.MSSqlServer(connection,
                sinkOptions: new MSSqlServerSinkOptions { SchemaName = "dbo", TableName = "ApplicationLogs", AutoCreateSqlTable = false, BatchPostingLimit = 50, BatchPeriod = TimeSpan.FromSeconds(5) },
                columnOptions: columns);
        }
        return logger;
    }
}
