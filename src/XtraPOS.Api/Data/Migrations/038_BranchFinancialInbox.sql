-- Read-only financial history received from central. Applying an event
-- does not create a sale, a stock movement, or a cash receipt.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchFinancialInbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchFinancialInbox
    (
        OperationID nvarchar(80) NOT NULL
            CONSTRAINT PK_BranchFinancialInbox PRIMARY KEY,
        EventSequence bigint NOT NULL,
        EventType nvarchar(40) NOT NULL,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        OriginBranchID uniqueidentifier NOT NULL,
        POSNumber int NULL,
        ActorUserID uniqueidentifier NULL,
        SourceSaleID uniqueidentifier NULL,
        SourceSaleNumber bigint NULL,
        Amount decimal(19, 4) NOT NULL,
        SignedEffect decimal(19, 4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_BranchFinancialInbox_Currency DEFAULT (N'ILS'),
        Reason nvarchar(400) NULL,
        ReversedOperationID nvarchar(80) NULL,
        Payload nvarchar(max) NOT NULL
            CONSTRAINT DF_BranchFinancialInbox_Payload DEFAULT (N'{}'),
        ConfirmedAt datetime2(0) NOT NULL,
        AppliedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchFinancialInbox_AppliedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_BranchFinancialInbox_Sequence UNIQUE (EventSequence),
        CONSTRAINT CK_BranchFinancialInbox_Amount CHECK (Amount > 0),
        CONSTRAINT CK_BranchFinancialInbox_Type CHECK (EventType IN (N'CreditSale', N'Repayment', N'DebtReversal', N'RepaymentReversal', N'Adjustment'))
    );
    CREATE INDEX IX_BranchFinancialInbox_Customer
        ON dbo.BranchFinancialInbox (BusinessID, CustomerID, ConfirmedAt);
END;

IF OBJECT_ID(N'dbo.FinancialSyncCheckpoints', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FinancialSyncCheckpoints
    (
        StreamName nvarchar(40) NOT NULL
            CONSTRAINT PK_FinancialSyncCheckpoints PRIMARY KEY,
        LastEventSequence bigint NOT NULL,
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_FinancialSyncCheckpoints_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
END;
