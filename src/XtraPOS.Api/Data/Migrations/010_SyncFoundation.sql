-- Branch sync metadata. Does not copy or delete catalog data.
-- BranchRemote operational synchronization is not created here.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole')
BEGIN
    INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
    VALUES (N'DatabaseRole', N'Branch');
END;

IF OBJECT_ID(N'dbo.SyncEndpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SyncEndpoints
    (
        EndpointID uniqueidentifier NOT NULL CONSTRAINT PK_SyncEndpoints PRIMARY KEY,
        DatabaseRole nvarchar(20) NOT NULL,
        LocationKind nvarchar(20) NOT NULL,
        ServerName nvarchar(200) NOT NULL,
        InstanceName nvarchar(200) NULL,
        Port int NULL,
        DatabaseName nvarchar(128) NOT NULL,
        AuthenticationType nvarchar(20) NOT NULL,
        Username nvarchar(128) NULL,
        ProtectedPassword nvarchar(max) NULL,
        EncryptConnection bit NOT NULL,
        TrustServerCertificate bit NOT NULL,
        Status nvarchar(40) NOT NULL,
        LastSuccessfulSyncAt datetime2(3) NULL,
        LastError nvarchar(400) NULL,
        CreatedAt datetime2(3) NOT NULL
            CONSTRAINT DF_SyncEndpoints_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_SyncEndpoints_DatabaseRole CHECK (DatabaseRole IN (N'Central', N'BranchRemote')),
        CONSTRAINT CK_SyncEndpoints_LocationKind CHECK (LocationKind IN (N'Local', N'Remote'))
    );
END;

IF OBJECT_ID(N'dbo.SyncCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SyncCheckpoints
    (
        EndpointID uniqueidentifier NOT NULL,
        StreamName nvarchar(64) NOT NULL,
        LastAppliedVersion bigint NOT NULL,
        CONSTRAINT PK_SyncCheckpoints PRIMARY KEY (EndpointID, StreamName),
        CONSTRAINT FK_SyncCheckpoints_Endpoint FOREIGN KEY (EndpointID) REFERENCES dbo.SyncEndpoints (EndpointID)
    );
END;

IF OBJECT_ID(N'dbo.SyncInbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SyncInbox
    (
        EndpointID uniqueidentifier NOT NULL,
        ChangeSequence bigint NOT NULL,
        EntityType nvarchar(64) NOT NULL,
        EntityId uniqueidentifier NOT NULL,
        AppliedAt datetime2(3) NOT NULL
            CONSTRAINT DF_SyncInbox_AppliedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_SyncInbox PRIMARY KEY (EndpointID, ChangeSequence),
        CONSTRAINT FK_SyncInbox_Endpoint FOREIGN KEY (EndpointID) REFERENCES dbo.SyncEndpoints (EndpointID)
    );
END;

IF OBJECT_ID(N'dbo.SyncOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SyncOutbox
    (
        OutboxID uniqueidentifier NOT NULL CONSTRAINT PK_SyncOutbox PRIMARY KEY,
        EndpointID uniqueidentifier NOT NULL,
        EntityType nvarchar(64) NOT NULL,
        EntityId uniqueidentifier NOT NULL,
        Status nvarchar(20) NOT NULL,
        CreatedAt datetime2(3) NOT NULL
            CONSTRAINT DF_SyncOutbox_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_SyncOutbox_Endpoint FOREIGN KEY (EndpointID) REFERENCES dbo.SyncEndpoints (EndpointID)
    );
END;
