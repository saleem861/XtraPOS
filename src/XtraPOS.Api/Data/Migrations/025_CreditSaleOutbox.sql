-- Pending credit sales and the branch financial outbox.
-- Does not post live debts. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF EXISTS
(
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_Sales_Status'
      AND parent_object_id = OBJECT_ID(N'dbo.Sales')
      AND definition NOT LIKE N'%CreditPending%'
)
BEGIN
    ALTER TABLE dbo.Sales DROP CONSTRAINT CK_Sales_Status;
    ALTER TABLE dbo.Sales ADD CONSTRAINT CK_Sales_Status
        CHECK (Status IN (N'Draft', N'Completed', N'Cancelled', N'CreditPending'));
END;

IF OBJECT_ID(N'dbo.BranchFinancialOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchFinancialOutbox
    (
        OutboxEventID uniqueidentifier NOT NULL CONSTRAINT PK_BranchFinancialOutbox PRIMARY KEY,
        CreditOperationID uniqueidentifier NOT NULL,
        SourceSaleID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        AuthorizationID uniqueidentifier NOT NULL,
        EventType nvarchar(40) NOT NULL,
        Payload nvarchar(2000) NOT NULL,
        Status nvarchar(30) NOT NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_BranchFinancialOutbox_AttemptCount DEFAULT (0),
        LastAttemptAt datetime2(0) NULL,
        NextRetryAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchFinancialOutbox_NextRetryAt DEFAULT (SYSUTCDATETIME()),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchFinancialOutbox_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        LastError nvarchar(400) NULL,
        CONSTRAINT UQ_BranchFinancialOutbox_Operation UNIQUE (CreditOperationID),
        CONSTRAINT UQ_BranchFinancialOutbox_Sale UNIQUE (SourceSaleID),
        CONSTRAINT FK_BranchFinancialOutbox_Sales FOREIGN KEY (SourceSaleID, BranchID) REFERENCES dbo.Sales (SaleID, BranchID),
        CONSTRAINT CK_BranchFinancialOutbox_Status CHECK (Status IN (N'Pending', N'Completed', N'ReconciliationRequired')),
        CONSTRAINT CK_BranchFinancialOutbox_Attempts CHECK (AttemptCount >= 0)
    );
END;
