IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.POSDeviceConfigurations
    (
        DeviceID uniqueidentifier NOT NULL,
        POSNumber int NULL,
        BranchID uniqueidentifier NULL,
        OrganizationID uniqueidentifier NULL,
        DeviceName nvarchar(200) NULL,
        ConfigurationJson nvarchar(max) NOT NULL,
        SchemaVersion int NOT NULL CONSTRAINT DF_POSDeviceConfigurations_SchemaVersion DEFAULT (1),
        ConfigVersion int NOT NULL CONSTRAINT DF_POSDeviceConfigurations_ConfigVersion DEFAULT (1),
        IsSetupCompleted bit NOT NULL CONSTRAINT DF_POSDeviceConfigurations_IsSetupCompleted DEFAULT (0),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_POSDeviceConfigurations_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedBy nvarchar(100) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_POSDeviceConfigurations PRIMARY KEY (DeviceID)
    );
END;

IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CentralPosReplica
    (
        PosNumber int NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        PosName nvarchar(200) NOT NULL,
        Status nvarchar(20) NOT NULL,
        SyncedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CentralPosReplica_SyncedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_CentralPosReplica PRIMARY KEY (PosNumber)
    );
END;
