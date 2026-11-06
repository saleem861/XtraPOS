-- Additive credit distribution. Legacy ledger rows stay valid.
-- Positive amounts remain the ledger convention. Direction is the transaction type
-- and the signed effect stored on the distributable event.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerCreditLedger', N'BusinessID') IS NULL
    ALTER TABLE dbo.CustomerCreditLedger ADD BusinessID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.CustomerCreditLedger', N'POSNumber') IS NULL
    ALTER TABLE dbo.CustomerCreditLedger ADD POSNumber int NULL;

IF COL_LENGTH(N'dbo.CustomerCreditLedger', N'OriginOperationID') IS NULL
    ALTER TABLE dbo.CustomerCreditLedger ADD OriginOperationID nvarchar(80) NULL;

IF COL_LENGTH(N'dbo.CustomerCreditLedger', N'ReversedOperationID') IS NULL
    ALTER TABLE dbo.CustomerCreditLedger ADD ReversedOperationID nvarchar(80) NULL;

IF COL_LENGTH(N'dbo.CustomerCreditLedger', N'Reason') IS NULL
    ALTER TABLE dbo.CustomerCreditLedger ADD Reason nvarchar(400) NULL;

EXEC(N'
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''UX_CustomerCreditLedger_OriginOperation'' AND object_id = OBJECT_ID(N''dbo.CustomerCreditLedger''))
    CREATE UNIQUE INDEX UX_CustomerCreditLedger_OriginOperation
        ON dbo.CustomerCreditLedger (OriginOperationID)
        WHERE OriginOperationID IS NOT NULL;
');

IF OBJECT_ID(N'dbo.CentralFinancialEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CentralFinancialEvents
    (
        EventSequence bigint IDENTITY(1, 1) NOT NULL
            CONSTRAINT PK_CentralFinancialEvents PRIMARY KEY,
        OperationID nvarchar(80) NOT NULL,
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
            CONSTRAINT DF_CentralFinancialEvents_Currency DEFAULT (N'ILS'),
        Reason nvarchar(400) NULL,
        ReversedOperationID nvarchar(80) NULL,
        Payload nvarchar(max) NOT NULL
            CONSTRAINT DF_CentralFinancialEvents_Payload DEFAULT (N'{}'),
        ConfirmedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CentralFinancialEvents_ConfirmedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT UQ_CentralFinancialEvents_Operation UNIQUE (OperationID),
        CONSTRAINT CK_CentralFinancialEvents_Amount CHECK (Amount > 0),
        CONSTRAINT CK_CentralFinancialEvents_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CentralFinancialEvents_Type CHECK (EventType IN (N'CreditSale', N'Repayment', N'DebtReversal', N'RepaymentReversal', N'Adjustment'))
    );
    CREATE INDEX IX_CentralFinancialEvents_BusinessCustomer
        ON dbo.CentralFinancialEvents (BusinessID, CustomerID, EventSequence);
END;
