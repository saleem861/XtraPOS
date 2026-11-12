-- One ordered branch operation log. Relay rows only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.BranchFinancialOutbox', N'AuthorizationID') IS NOT NULL
   AND EXISTS
   (
       SELECT 1
       FROM sys.columns
       WHERE object_id = OBJECT_ID(N'dbo.BranchFinancialOutbox')
         AND name = N'AuthorizationID'
         AND is_nullable = 0
   )
BEGIN
    ALTER TABLE dbo.BranchFinancialOutbox ALTER COLUMN AuthorizationID uniqueidentifier NULL;
END;

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchOperationLog
    (
        Serial int IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_BranchOperationLog PRIMARY KEY,
        OperationID uniqueidentifier NOT NULL,
        ReferenceNumber nvarchar(80) NOT NULL
            CONSTRAINT DF_BranchOperationLog_Reference DEFAULT (N''),
        OriginBranchCode nvarchar(50) NOT NULL,
        OriginBranchID uniqueidentifier NOT NULL,
        POSNumber int NOT NULL,
        StationID uniqueidentifier NULL,
        ActorUserID uniqueidentifier NOT NULL,
        OperationType nvarchar(40) NOT NULL,
        PayloadVersion int NOT NULL,
        Payload nvarchar(max) NOT NULL,
        Description nvarchar(400) NOT NULL,
        SourceEntityType nvarchar(64) NOT NULL,
        SourceEntityID uniqueidentifier NOT NULL,
        DestinationKind nvarchar(20) NOT NULL,
        DestinationBranchCode nvarchar(50) NULL,
        DestinationBranchID uniqueidentifier NULL,
        DestinationName nvarchar(100) NOT NULL,
        Status nvarchar(20) NOT NULL,
        ResultMessage nvarchar(max) NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_BranchOperationLog_AttemptCount DEFAULT (0),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchOperationLog_CreatedAt DEFAULT (SYSUTCDATETIME()),
        StartedAt datetime2(0) NULL,
        CompletedAt datetime2(0) NULL,
        LastAttemptAt datetime2(0) NULL,
        LeaseOwner nvarchar(80) NULL,
        LeaseUntil datetime2(0) NULL,
        CONSTRAINT UQ_BranchOperationLog_Operation UNIQUE (OperationID),
        CONSTRAINT CK_BranchOperationLog_Status CHECK (Status IN (N'Pending', N'Executing', N'Completed', N'Blocked', N'Cancelled')),
        CONSTRAINT CK_BranchOperationLog_Destination CHECK (DestinationKind IN (N'Central', N'Branch')),
        CONSTRAINT CK_BranchOperationLog_Attempts CHECK (AttemptCount >= 0),
        CONSTRAINT CK_BranchOperationLog_Version CHECK (PayloadVersion >= 1)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchOperationLog_Open' AND object_id = OBJECT_ID(N'dbo.BranchOperationLog'))
    CREATE INDEX IX_BranchOperationLog_Open
        ON dbo.BranchOperationLog (Serial)
        WHERE Status IN (N'Pending', N'Executing', N'Blocked');

IF OBJECT_ID(N'dbo.SystemSettings', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'OperationLogRetentionDays')
    INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'OperationLogRetentionDays', N'30');

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.BranchOperationLog
        (OperationID, OriginBranchCode, OriginBranchID, POSNumber, ActorUserID, OperationType, PayloadVersion, Payload, Description, SourceEntityType, SourceEntityID, DestinationKind, DestinationName, Status, CreatedAt)
    SELECT box.CreditOperationID,
           ISNULL(branch.BranchCode, N''),
           box.BranchID,
           0,
           ISNULL(sale.CashierUserID, '00000000-0000-0000-0000-000000000000'),
           N'CreditSale',
           1,
           box.Payload,
           N'ترحيل بيع بالدين محفوظ سابقاً',
           N'Sales',
           box.SourceSaleID,
           N'Central',
           N'المركز',
           CASE WHEN box.Status = N'Completed' THEN N'Completed' WHEN box.Status = N'ReconciliationRequired' THEN N'Blocked' ELSE N'Pending' END,
           box.CreatedAt
    FROM dbo.BranchFinancialOutbox AS box
    LEFT JOIN dbo.Branches AS branch ON branch.BranchID = box.BranchID
    LEFT JOIN dbo.Sales AS sale ON sale.SaleID = box.SourceSaleID
    WHERE box.Status <> N'Completed'
      AND NOT EXISTS (SELECT 1 FROM dbo.BranchOperationLog AS logRow WHERE logRow.OperationID = box.CreditOperationID);
END;

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.BranchOperationLog
        (OperationID, OriginBranchCode, OriginBranchID, POSNumber, ActorUserID, OperationType, PayloadVersion, Payload, Description, SourceEntityType, SourceEntityID, DestinationKind, DestinationName, Status, CreatedAt)
    SELECT box.CreditOperationID,
           ISNULL(branch.BranchCode, N''),
           box.BranchID,
           0,
           ISNULL(receipt.CashierUserID, '00000000-0000-0000-0000-000000000000'),
           N'DebtCollection',
           1,
           box.Payload,
           N'ترحيل تسديد دين محفوظ سابقاً',
           N'BranchDebtCollections',
           box.CreditOperationID,
           N'Central',
           N'المركز',
           CASE WHEN box.Status = N'Completed' THEN N'Completed' WHEN box.Status = N'ReconciliationRequired' THEN N'Blocked' ELSE N'Pending' END,
           box.CreatedAt
    FROM dbo.BranchCollectionOutbox AS box
    LEFT JOIN dbo.Branches AS branch ON branch.BranchID = box.BranchID
    LEFT JOIN dbo.BranchDebtCollections AS receipt ON receipt.CollectionOperationID = box.CreditOperationID
    WHERE box.Status <> N'Completed'
      AND NOT EXISTS (SELECT 1 FROM dbo.BranchOperationLog AS logRow WHERE logRow.OperationID = box.CreditOperationID);
END;

IF OBJECT_ID(N'dbo.BranchOperationLog', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchReversalOutbox', N'U') IS NOT NULL
BEGIN
    INSERT INTO dbo.BranchOperationLog
        (OperationID, OriginBranchCode, OriginBranchID, POSNumber, ActorUserID, OperationType, PayloadVersion, Payload, Description, SourceEntityType, SourceEntityID, DestinationKind, DestinationName, Status, CreatedAt)
    SELECT box.ReversalOperationID,
           ISNULL(branch.BranchCode, N''),
           reversal.BranchID,
           0,
           ISNULL(reversal.ActorUserID, '00000000-0000-0000-0000-000000000000'),
           N'CollectionReversal',
           1,
           box.Payload,
           N'ترحيل عكس تسديد محفوظ سابقاً',
           N'BranchCollectionReversals',
           box.ReversalOperationID,
           N'Central',
           N'المركز',
           CASE WHEN box.Status = N'ReconciliationRequired' THEN N'Blocked' ELSE N'Pending' END,
           SYSUTCDATETIME()
    FROM dbo.BranchReversalOutbox AS box
    INNER JOIN dbo.BranchCollectionReversals AS reversal ON reversal.ReversalOperationID = box.ReversalOperationID
    LEFT JOIN dbo.Branches AS branch ON branch.BranchID = reversal.BranchID
    WHERE box.Status <> N'Completed'
      AND NOT EXISTS (SELECT 1 FROM dbo.BranchOperationLog AS logRow WHERE logRow.OperationID = box.ReversalOperationID);
END;
