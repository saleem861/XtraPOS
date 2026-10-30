-- Branch debt-collection receipts and outbox. Not sales revenue. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchDebtCollections
    (
        CollectionOperationID uniqueidentifier NOT NULL CONSTRAINT PK_BranchDebtCollections PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CashSessionID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        CashierUserID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_BranchDebtCollections_Currency DEFAULT (N'ILS'),
        PaymentMethodCode nvarchar(20) NOT NULL,
        Status nvarchar(30) NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchDebtCollections_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_BranchDebtCollections_Session FOREIGN KEY (CashSessionID, BranchID) REFERENCES dbo.POSCashSessions (CashSessionID, BranchID),
        CONSTRAINT CK_BranchDebtCollections_Amount CHECK (Amount > 0),
        CONSTRAINT CK_BranchDebtCollections_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_BranchDebtCollections_Method CHECK (PaymentMethodCode = N'Cash'),
        CONSTRAINT CK_BranchDebtCollections_Status CHECK (Status IN (N'Pending', N'Completed', N'ReconciliationRequired'))
    );
END;

IF OBJECT_ID(N'dbo.BranchCollectionOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchCollectionOutbox
    (
        OutboxEventID uniqueidentifier NOT NULL CONSTRAINT PK_BranchCollectionOutbox PRIMARY KEY,
        CreditOperationID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        Payload nvarchar(2000) NOT NULL,
        Status nvarchar(30) NOT NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_BranchCollectionOutbox_AttemptCount DEFAULT (0),
        LastAttemptAt datetime2(0) NULL,
        NextRetryAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchCollectionOutbox_NextRetryAt DEFAULT (SYSUTCDATETIME()),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchCollectionOutbox_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        LastError nvarchar(400) NULL,
        LeaseOwner nvarchar(80) NULL,
        LeaseUntil datetime2(0) NULL,
        CONSTRAINT UQ_BranchCollectionOutbox_Operation UNIQUE (CreditOperationID),
        CONSTRAINT FK_BranchCollectionOutbox_Collection FOREIGN KEY (CreditOperationID) REFERENCES dbo.BranchDebtCollections (CollectionOperationID),
        CONSTRAINT CK_BranchCollectionOutbox_Status CHECK (Status IN (N'Pending', N'Completed', N'ReconciliationRequired')),
        CONSTRAINT CK_BranchCollectionOutbox_Attempts CHECK (AttemptCount >= 0)
    );
END;
