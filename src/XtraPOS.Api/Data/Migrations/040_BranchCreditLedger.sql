-- Branch copy of the central debt ledger. Same columns. Safe to run again.
-- A row is written when the branch records the debt. The idempotency key is the
-- operation id, so the same debt is not inserted again when central sends it back.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerReceivables', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerReceivables
    (
        ReceivableID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerReceivables PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        SourceSaleID uniqueidentifier NOT NULL,
        SourceBranchID uniqueidentifier NOT NULL,
        SourcePOSID uniqueidentifier NULL,
        OriginalAmount decimal(19, 4) NOT NULL,
        OutstandingAmount decimal(19, 4) NOT NULL,
        DueDate date NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_CustomerReceivables_CurrencyCode DEFAULT (N'ILS'),
        Status nvarchar(20) NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerReceivables_CreatedAt DEFAULT (SYSUTCDATETIME()),
        SourceSaleNumber bigint NULL,
        CONSTRAINT FK_CustomerReceivables_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CustomerReceivables_SourceSale UNIQUE (SourceSaleID),
        CONSTRAINT CK_CustomerReceivables_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CustomerReceivables_Amounts CHECK (OriginalAmount > 0 AND OutstandingAmount >= 0 AND OutstandingAmount <= OriginalAmount),
        CONSTRAINT CK_CustomerReceivables_Status CHECK (Status IN (N'Open', N'Partial', N'Settled', N'Reversed'))
    );
    CREATE INDEX IX_CustomerReceivables_CustomerDue ON dbo.CustomerReceivables (CustomerID, DueDate, Status);
END;

IF COL_LENGTH(N'dbo.CustomerReceivables', N'SourceSaleNumber') IS NULL
    ALTER TABLE dbo.CustomerReceivables ADD SourceSaleNumber bigint NULL;

IF OBJECT_ID(N'dbo.CustomerCreditLedger', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerCreditLedger
    (
        LedgerEntryID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerCreditLedger PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        TransactionID uniqueidentifier NOT NULL,
        TransactionType nvarchar(30) NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_CustomerCreditLedger_CurrencyCode DEFAULT (N'ILS'),
        SourceBranchID uniqueidentifier NOT NULL,
        SourceSaleID uniqueidentifier NULL,
        PaymentID uniqueidentifier NULL,
        OccurredAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerCreditLedger_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CreatedBy uniqueidentifier NOT NULL,
        ReversalReference uniqueidentifier NULL,
        IdempotencyKey nvarchar(80) NOT NULL,
        BusinessID uniqueidentifier NULL,
        POSNumber int NULL,
        OriginOperationID nvarchar(80) NULL,
        ReversedOperationID nvarchar(80) NULL,
        Reason nvarchar(400) NULL,
        CONSTRAINT FK_CustomerCreditLedger_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CustomerCreditLedger_Idempotency UNIQUE (IdempotencyKey),
        CONSTRAINT FK_CustomerCreditLedger_Reversal FOREIGN KEY (ReversalReference) REFERENCES dbo.CustomerCreditLedger (LedgerEntryID),
        CONSTRAINT CK_CustomerCreditLedger_Amount CHECK (Amount > 0),
        CONSTRAINT CK_CustomerCreditLedger_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CustomerCreditLedger_Type CHECK (TransactionType IN (N'CreditSale', N'Repayment', N'Adjustment', N'Reversal'))
    );
    CREATE INDEX IX_CustomerCreditLedger_Customer ON dbo.CustomerCreditLedger (CustomerID, OccurredAt);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CustomerCreditLedger_OriginOperation' AND object_id = OBJECT_ID(N'dbo.CustomerCreditLedger'))
    CREATE UNIQUE INDEX UX_CustomerCreditLedger_OriginOperation
        ON dbo.CustomerCreditLedger (OriginOperationID)
        WHERE OriginOperationID IS NOT NULL;

IF OBJECT_ID(N'dbo.CreditPaymentAllocations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditPaymentAllocations
    (
        AllocationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditPaymentAllocations PRIMARY KEY,
        LedgerEntryID uniqueidentifier NOT NULL,
        ReceivableID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        CONSTRAINT FK_CreditPaymentAllocations_Ledger FOREIGN KEY (LedgerEntryID) REFERENCES dbo.CustomerCreditLedger (LedgerEntryID),
        CONSTRAINT FK_CreditPaymentAllocations_Receivable FOREIGN KEY (ReceivableID) REFERENCES dbo.CustomerReceivables (ReceivableID),
        CONSTRAINT UQ_CreditPaymentAllocations_EntryReceivable UNIQUE (LedgerEntryID, ReceivableID),
        CONSTRAINT CK_CreditPaymentAllocations_Amount CHECK (Amount > 0)
    );
END;

INSERT INTO dbo.CustomerReceivables
    (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status, CreatedAt, SourceSaleNumber)
SELECT NEWID(), inbox.CustomerID, inbox.SourceSaleID, inbox.OriginBranchID, inbox.Amount, inbox.Amount,
       DATEADD(day, 30, CAST(inbox.ConfirmedAt AS date)), N'ILS', N'Open', inbox.ConfirmedAt, inbox.SourceSaleNumber
FROM dbo.BranchFinancialInbox AS inbox
WHERE inbox.EventType = N'CreditSale'
  AND inbox.SourceSaleID IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.Customers AS customer WHERE customer.CustomerID = inbox.CustomerID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CustomerReceivables AS receivable WHERE receivable.SourceSaleID = inbox.SourceSaleID);

INSERT INTO dbo.CustomerCreditLedger
    (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, OccurredAt, CreatedBy, IdempotencyKey, BusinessID, POSNumber, OriginOperationID)
SELECT NEWID(), inbox.CustomerID, NEWID(), N'CreditSale', inbox.Amount, N'ILS', inbox.OriginBranchID, inbox.SourceSaleID, inbox.ConfirmedAt,
       COALESCE(inbox.ActorUserID, '00000000-0000-0000-0000-000000000000'),
       inbox.OperationID,
       COALESCE(inbox.BusinessID, TRY_CONVERT(uniqueidentifier, (SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID'))),
       inbox.POSNumber,
       inbox.OperationID
FROM dbo.BranchFinancialInbox AS inbox
WHERE inbox.EventType = N'CreditSale'
  AND EXISTS (SELECT 1 FROM dbo.Customers AS customer WHERE customer.CustomerID = inbox.CustomerID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CustomerCreditLedger AS ledger WHERE ledger.IdempotencyKey = inbox.OperationID);

INSERT INTO dbo.CustomerReceivables
    (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status, CreatedAt, SourceSaleNumber)
SELECT NEWID(), box.CustomerID, box.SourceSaleID, box.BranchID, sale.Total, sale.Total,
       DATEADD(day, 30, CAST(COALESCE(sale.CompletedAt, box.CreatedAt) AS date)), N'ILS', N'Open', COALESCE(sale.CompletedAt, box.CreatedAt), sale.SaleNumber
FROM dbo.BranchFinancialOutbox AS box
INNER JOIN dbo.Sales AS sale ON sale.SaleID = box.SourceSaleID
WHERE box.EventType = N'FinalizeCredit'
  AND sale.Total > 0
  AND EXISTS (SELECT 1 FROM dbo.Customers AS customer WHERE customer.CustomerID = box.CustomerID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CustomerReceivables AS receivable WHERE receivable.SourceSaleID = box.SourceSaleID);

INSERT INTO dbo.CustomerCreditLedger
    (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, SourceSaleID, OccurredAt, CreatedBy, IdempotencyKey, POSNumber, OriginOperationID)
SELECT NEWID(), box.CustomerID, NEWID(), N'CreditSale', sale.Total, N'ILS', box.BranchID, box.SourceSaleID, COALESCE(sale.CompletedAt, box.CreatedAt),
       COALESCE(log.ActorUserID, '00000000-0000-0000-0000-000000000000'),
       LOWER(REPLACE(CONVERT(nvarchar(36), box.CreditOperationID), N'-', N'')),
       log.POSNumber,
       LOWER(REPLACE(CONVERT(nvarchar(36), box.CreditOperationID), N'-', N''))
FROM dbo.BranchFinancialOutbox AS box
INNER JOIN dbo.Sales AS sale ON sale.SaleID = box.SourceSaleID
LEFT JOIN dbo.BranchOperationLog AS log ON log.OperationID = box.CreditOperationID
WHERE box.EventType = N'FinalizeCredit'
  AND sale.Total > 0
  AND EXISTS (SELECT 1 FROM dbo.Customers AS customer WHERE customer.CustomerID = box.CustomerID)
  AND NOT EXISTS (
      SELECT 1 FROM dbo.CustomerCreditLedger AS ledger
      WHERE ledger.IdempotencyKey = LOWER(REPLACE(CONVERT(nvarchar(36), box.CreditOperationID), N'-', N'')));
