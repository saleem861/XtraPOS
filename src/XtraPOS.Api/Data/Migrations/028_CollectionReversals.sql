-- Branch repayment reversals and outbox. Not sales revenue. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchCollectionReversals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchCollectionReversals
    (
        ReversalOperationID uniqueidentifier NOT NULL CONSTRAINT PK_BranchCollectionReversals PRIMARY KEY,
        CollectionOperationID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CashSessionID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        ActorUserID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        ReasonCode nvarchar(40) NOT NULL,
        CashEffect nvarchar(30) NOT NULL,
        Status nvarchar(30) NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchCollectionReversals_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_BranchCollectionReversals_Collection FOREIGN KEY (CollectionOperationID) REFERENCES dbo.BranchDebtCollections (CollectionOperationID),
        CONSTRAINT FK_BranchCollectionReversals_Session FOREIGN KEY (CashSessionID, BranchID) REFERENCES dbo.POSCashSessions (CashSessionID, BranchID),
        CONSTRAINT UQ_BranchCollectionReversals_Collection UNIQUE (CollectionOperationID),
        CONSTRAINT CK_BranchCollectionReversals_Amount CHECK (Amount > 0),
        CONSTRAINT CK_BranchCollectionReversals_Reason CHECK (ReasonCode IN (N'RefundToCustomer', N'IncorrectRecording')),
        CONSTRAINT CK_BranchCollectionReversals_Effect CHECK (CashEffect IN (N'RefundOutflow', N'RecordingCorrection')),
        CONSTRAINT CK_BranchCollectionReversals_Status CHECK (Status IN (N'Pending', N'Completed', N'Rejected', N'ReconciliationRequired'))
    );
END;

IF OBJECT_ID(N'dbo.BranchReversalOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchReversalOutbox
    (
        OutboxEventID uniqueidentifier NOT NULL CONSTRAINT PK_BranchReversalOutbox PRIMARY KEY,
        ReversalOperationID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        Payload nvarchar(2000) NOT NULL,
        Status nvarchar(30) NOT NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_BranchReversalOutbox_AttemptCount DEFAULT (0),
        NextRetryAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchReversalOutbox_NextRetryAt DEFAULT (SYSUTCDATETIME()),
        LastError nvarchar(400) NULL,
        LeaseOwner nvarchar(80) NULL,
        LeaseUntil datetime2(0) NULL,
        CONSTRAINT UQ_BranchReversalOutbox_Operation UNIQUE (ReversalOperationID),
        CONSTRAINT FK_BranchReversalOutbox_Reversal FOREIGN KEY (ReversalOperationID) REFERENCES dbo.BranchCollectionReversals (ReversalOperationID),
        CONSTRAINT CK_BranchReversalOutbox_Status CHECK (Status IN (N'Pending', N'Completed', N'ReconciliationRequired'))
    );
END;
