-- Central customer credit authority. Additive only.
-- Does not import legacy Debts or CustomersAllowedToDebt.
-- Safe to run again. Not applied by this file itself.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerCreditPolicies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerCreditPolicies
    (
        CustomerID uniqueidentifier NOT NULL,
        CreditEnabled bit NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_CreditEnabled DEFAULT (0),
        CreditLimit decimal(19, 4) NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_CreditLimit DEFAULT (0),
        PaymentTermDays int NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_PaymentTermDays DEFAULT (30),
        CreditStatus nvarchar(20) NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_CreditStatus DEFAULT (N'Inactive'),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerCreditPolicies_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy uniqueidentifier NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_CustomerCreditPolicies PRIMARY KEY (CustomerID),
        CONSTRAINT FK_CustomerCreditPolicies_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT CK_CustomerCreditPolicies_Limit CHECK (CreditLimit >= 0),
        CONSTRAINT CK_CustomerCreditPolicies_Terms CHECK (PaymentTermDays >= 0 AND PaymentTermDays <= 3650),
        CONSTRAINT CK_CustomerCreditPolicies_Status CHECK (CreditStatus IN (N'Inactive', N'Active', N'Suspended'))
    );
END;

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
        CONSTRAINT FK_CustomerReceivables_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CustomerReceivables_SourceSale UNIQUE (SourceSaleID),
        CONSTRAINT CK_CustomerReceivables_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CustomerReceivables_Amounts CHECK (OriginalAmount > 0 AND OutstandingAmount >= 0 AND OutstandingAmount <= OriginalAmount),
        CONSTRAINT CK_CustomerReceivables_Status CHECK (Status IN (N'Open', N'Partial', N'Settled', N'Reversed'))
    );
    CREATE INDEX IX_CustomerReceivables_CustomerDue ON dbo.CustomerReceivables (CustomerID, DueDate, Status);
END;

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
        CONSTRAINT FK_CustomerCreditLedger_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CustomerCreditLedger_Idempotency UNIQUE (IdempotencyKey),
        CONSTRAINT FK_CustomerCreditLedger_Reversal FOREIGN KEY (ReversalReference) REFERENCES dbo.CustomerCreditLedger (LedgerEntryID),
        CONSTRAINT CK_CustomerCreditLedger_Amount CHECK (Amount > 0),
        CONSTRAINT CK_CustomerCreditLedger_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CustomerCreditLedger_Type CHECK (TransactionType IN (N'CreditSale', N'Repayment', N'Adjustment', N'Reversal'))
    );
    CREATE INDEX IX_CustomerCreditLedger_Customer ON dbo.CustomerCreditLedger (CustomerID, OccurredAt);
END;

IF OBJECT_ID(N'dbo.TR_CustomerCreditLedger_AppendOnly', N'TR') IS NULL
EXEC(N'
CREATE TRIGGER dbo.TR_CustomerCreditLedger_AppendOnly
ON dbo.CustomerCreditLedger
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51011, N''Posted credit ledger rows cannot be changed or deleted.'', 1;
END');

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

IF OBJECT_ID(N'dbo.CreditAuthorizations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditAuthorizations
    (
        AuthorizationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditAuthorizations PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        RequestedAmount decimal(19, 4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_CreditAuthorizations_CurrencyCode DEFAULT (N'ILS'),
        Status nvarchar(30) NOT NULL,
        IdempotencyKey nvarchar(80) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        SourceSaleID uniqueidentifier NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CreditAuthorizations_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_CreditAuthorizations_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CreditAuthorizations_Idempotency UNIQUE (IdempotencyKey),
        CONSTRAINT CK_CreditAuthorizations_Amount CHECK (RequestedAmount > 0),
        CONSTRAINT CK_CreditAuthorizations_Status CHECK (Status IN (N'Pending', N'Authorized', N'Posting', N'Completed', N'Rejected', N'ReconciliationRequired', N'Reversed', N'Expired'))
    );
    CREATE INDEX IX_CreditAuthorizations_Active ON dbo.CreditAuthorizations (CustomerID, Status, ExpiresAt);
END;

IF OBJECT_ID(N'dbo.CreditAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditAuditLog
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_CreditAuditLog PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        ActorUserID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NULL,
        DeviceID uniqueidentifier NULL,
        Action nvarchar(50) NOT NULL,
        OldValue nvarchar(500) NULL,
        NewValue nvarchar(500) NULL,
        Reason nvarchar(500) NULL,
        Result nvarchar(30) NOT NULL,
        OccurredAt datetime2(0) NOT NULL
            CONSTRAINT DF_CreditAuditLog_OccurredAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.TR_CreditAuditLog_AppendOnly', N'TR') IS NULL
EXEC(N'
CREATE TRIGGER dbo.TR_CreditAuditLog_AppendOnly
ON dbo.CreditAuditLog
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    THROW 51012, N''Credit audit rows cannot be changed or deleted.'', 1;
END');
