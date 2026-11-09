-- Provision using the deployment identity, not the runtime log writer.
IF OBJECT_ID(N'dbo.ApplicationLogs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApplicationLogs
    (
        Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        Message nvarchar(max) NULL,
        Level nvarchar(128) NULL,
        TimeStamp datetimeoffset(7) NOT NULL,
        LogEvent nvarchar(max) NULL
    );
    CREATE INDEX IX_ApplicationLogs_TimeStamp ON dbo.ApplicationLogs(TimeStamp);
END;
-- Runtime sink identity needs INSERT and SELECT on this table (SqlBulkCopy metadata), not db_owner.
-- Schedule retention with Azure Automation/SQL Agent, e.g. batched deletion older than your policy.
