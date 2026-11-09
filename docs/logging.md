# Serilog: console, rolling file, Seq and SQL Server

Serilog replaces the default logging provider in Program.cs. All four sinks are wired in Observability/LoggingConfiguration.cs and can be enabled independently. Normal local execution enables console/file; Docker enables all four. Azure Bicep enables all four with secret references.

| Setting | Default | Purpose |
|---|---|---|
| Observability:Console:Enabled | true | Structured JSON to stdout, collected by the container host |
| Observability:File:Enabled | true | Rolling JSON files |
| Observability:File:Path | logs/api-.json | 20 MB size rollover, daily rollover, 14 files retained |
| Observability:Seq:Enabled | false | Batched Seq ingestion |
| Observability:Seq:ServerUrl | http://localhost:5341 | Use HTTPS and an ingestion API key outside local Compose |
| Observability:Seq:ApiKey | unset | Secret; supply through environment/Key Vault |
| Observability:SqlServer:Enabled | false | Batched SQL sink, 50 events / 5 seconds |
| ConnectionStrings:Logs | unset | Separate least-privilege logging connection |

Environment variables replace colons with double underscores. Example: `Observability__Seq__Enabled=true`. Do not commit API keys or SQL credentials. For local LocalDB, set ConnectionStrings__Logs to the Windows-authenticated practice database and run `dotnet run --project src/OrderManagement.Api -- --migrate` once before enabling the SQL sink. Docker performs provisioning through its one-shot migration service.

SQL schema is explicitly provisioned from scripts/sql/Logging.sql; the sink never creates tables at runtime. Its columns are Id, Message, Level, TimeStamp (UTC datetimeoffset), and LogEvent JSON. It can use a separate database: pass ConnectionStrings__LogsMigration only to the migration command, and ConnectionStrings__Logs only to the app. Runtime logs permissions should be INSERT and SELECT on dbo.ApplicationLogs; do not give the sink db_owner. Batch sinks are operational logging, not a transactional financial/audit trail.

Request logging records method, route template, status, elapsed milliseconds and trace ID. It intentionally omits query strings, headers, cookies and bodies. Raw exception-bearing events are dropped because database/SMTP exception messages can include submitted values; the exception middleware emits safe exception-type/trace summaries. EF command/error logging is suppressed in favor of those summaries. Do not enable sensitive-data logging or Serilog SelfLog in production.

This is not a universal secret redactor: application code must never log passwords, JWTs, refresh tokens, recovery URLs, connection strings or configuration dumps. Add approved structured properties deliberately. Restrict log-reader access and set a retention policy appropriate for the organization.

Docker logs: `docker compose logs app`. Rolling files persist in the app-logs volume. Seq UI is http://localhost:5341, bound only to loopback; username admin and the generated SEQ_PASSWORD in ignored .env. Development Seq accepts ingestion on the private Compose network; configure an ingestion API key for a real environment. SQL and Seq have persistent named volumes. Stopping the stack without `--volumes` preserves data.

Azure file path is /home/LogFiles/ordermanagement/api-.json with App Service storage enabled. Validate non-root permissions on that mount in your environment. Console logs remain the primary platform collection route; Seq and SQL are additional searchable stores. Set independent retention outside the app for Seq/SQL. A sink outage is not an application-readiness signal; monitor ingestion failures and use an external synthetic event to alert if ingestion stops.

Tests/OrderManagement.Tests/LoggingTests.cs verifies console emission, rolling-file JSON, real SQL inserts, Seq-compatible HTTP delivery, and exclusion of raw exception details. No secret values are printed by that test.

Package sources: [Serilog ASP.NET Core](https://github.com/serilog/serilog-aspnetcore), [SQL sink](https://github.com/serilog-mssql/serilog-sinks-mssqlserver), [Seq sink](https://github.com/datalust/serilog-sinks-seq).
