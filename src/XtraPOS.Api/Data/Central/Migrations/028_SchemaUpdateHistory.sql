-- Same additive structures as the branch schema release.
-- Does not alter credit, receivable, or ledger columns.

IF OBJECT_ID(N'dbo.Cashboxes', N'U') IS NULL
   AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
BEGIN
    CREATE TABLE dbo.Cashboxes
    (
        CashboxID uniqueidentifier NOT NULL CONSTRAINT PK_Cashboxes PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CashboxNumber int NOT NULL,
        DisplayName nvarchar(200) NOT NULL,
        DeviceID uniqueidentifier NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_Cashboxes_Status DEFAULT (N'Active'),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Cashboxes_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_Cashboxes_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT UQ_Cashboxes_BranchNumber UNIQUE (BranchID, CashboxNumber),
        CONSTRAINT CK_Cashboxes_Number CHECK (CashboxNumber > 0),
        CONSTRAINT CK_Cashboxes_Status CHECK (Status IN (N'Active', N'Inactive'))
    );
END;

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.POSCashSessions', N'CashboxID') IS NULL
    ALTER TABLE dbo.POSCashSessions ADD CashboxID uniqueidentifier NULL;

IF OBJECT_ID(N'dbo.SchemaUpdateHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SchemaUpdateHistory
    (
        UpdateID uniqueidentifier NOT NULL CONSTRAINT PK_SchemaUpdateHistory PRIMARY KEY,
        DatabaseName nvarchar(128) NOT NULL,
        SchemaVersion nvarchar(64) NOT NULL,
        Status nvarchar(20) NOT NULL,
        Detail nvarchar(400) NULL,
        AttemptedAt datetime2(0) NOT NULL CONSTRAINT DF_SchemaUpdateHistory_AttemptedAt DEFAULT (SYSUTCDATETIME())
    );
END;
