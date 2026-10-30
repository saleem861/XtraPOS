-- Central had no POS-number table. This registry is the allocation source of truth.
-- Passwords are not stored here.

IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CentralPosRegistry
    (
        PosNumber int NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NULL,
        PosName nvarchar(200) NOT NULL,
        Status nvarchar(20) NOT NULL,
        ReservedAt datetime2(0) NOT NULL CONSTRAINT DF_CentralPosRegistry_ReservedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_CentralPosRegistry PRIMARY KEY (PosNumber),
        CONSTRAINT CK_CentralPosRegistry_PosNumber CHECK (PosNumber > 0),
        CONSTRAINT CK_CentralPosRegistry_Status CHECK (Status IN (N'Active', N'Revoked'))
    );
END;

IF OBJECT_ID(N'dbo.BranchSqlEndpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchSqlEndpoints
    (
        BranchID uniqueidentifier NOT NULL,
        Host nvarchar(200) NOT NULL,
        Port int NOT NULL,
        InstanceName nvarchar(128) NULL,
        DatabaseName nvarchar(128) NOT NULL,
        AuthenticationMode nvarchar(32) NOT NULL,
        PrincipalName nvarchar(128) NOT NULL,
        TlsMode nvarchar(32) NOT NULL,
        Status nvarchar(32) NOT NULL,
        LastSuccessfulCheck datetime2(0) NULL,
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_BranchSqlEndpoints_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_BranchSqlEndpoints PRIMARY KEY (BranchID)
    );
END;
