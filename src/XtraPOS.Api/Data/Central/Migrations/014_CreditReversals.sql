-- Central repayment reversals. Append-only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CreditRepaymentReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditRepaymentReversals
    (
        ReversalOperationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditRepaymentReversals PRIMARY KEY,
        CollectionOperationID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        ReasonCode nvarchar(40) NOT NULL,
        Status nvarchar(30) NOT NULL,
        IdempotencyKey nvarchar(80) NOT NULL,
        LedgerEntryID uniqueidentifier NULL,
        CreatedBy uniqueidentifier NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CreditRepaymentReversals_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ExpiresAt datetime2(0) NOT NULL,
        CONSTRAINT FK_CreditRepaymentReversals_Collection FOREIGN KEY (CollectionOperationID) REFERENCES dbo.CreditCollectionIntents (CollectionOperationID),
        CONSTRAINT UQ_CreditRepaymentReversals_Collection UNIQUE (CollectionOperationID),
        CONSTRAINT UQ_CreditRepaymentReversals_Idempotency UNIQUE (IdempotencyKey),
        CONSTRAINT CK_CreditRepaymentReversals_Amount CHECK (Amount > 0),
        CONSTRAINT CK_CreditRepaymentReversals_Reason CHECK (ReasonCode IN (N'RefundToCustomer', N'IncorrectRecording')),
        CONSTRAINT CK_CreditRepaymentReversals_Status CHECK (Status IN (N'Authorized', N'Posting', N'Completed', N'Released', N'ReconciliationRequired'))
    );
END;

IF OBJECT_ID(N'dbo.CreditAllocationReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditAllocationReversals
    (
        ReversalAllocationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditAllocationReversals PRIMARY KEY,
        ReversalOperationID uniqueidentifier NOT NULL,
        OriginalAllocationID uniqueidentifier NOT NULL,
        ReceivableID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        LedgerEntryID uniqueidentifier NOT NULL,
        CONSTRAINT FK_CreditAllocationReversals_Reversal FOREIGN KEY (ReversalOperationID) REFERENCES dbo.CreditRepaymentReversals (ReversalOperationID),
        CONSTRAINT FK_CreditAllocationReversals_Original FOREIGN KEY (OriginalAllocationID) REFERENCES dbo.CreditPaymentAllocations (AllocationID),
        CONSTRAINT UQ_CreditAllocationReversals_Original UNIQUE (OriginalAllocationID),
        CONSTRAINT CK_CreditAllocationReversals_Amount CHECK (Amount > 0)
    );
END;
