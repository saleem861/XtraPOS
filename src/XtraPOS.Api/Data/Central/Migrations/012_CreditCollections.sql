-- Central collection intents. Does not import legacy debt. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CreditCollectionIntents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditCollectionIntents
    (
        CollectionOperationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditCollectionIntents PRIMARY KEY,
        CustomerID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL
            CONSTRAINT DF_CreditCollectionIntents_Currency DEFAULT (N'ILS'),
        PaymentMethodCode nvarchar(20) NOT NULL,
        Status nvarchar(30) NOT NULL,
        IdempotencyKey nvarchar(80) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        CreatedBy uniqueidentifier NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CreditCollectionIntents_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_CreditCollectionIntents_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_CreditCollectionIntents_Idempotency UNIQUE (IdempotencyKey),
        CONSTRAINT CK_CreditCollectionIntents_Amount CHECK (Amount > 0),
        CONSTRAINT CK_CreditCollectionIntents_Currency CHECK (CurrencyCode = N'ILS'),
        CONSTRAINT CK_CreditCollectionIntents_Method CHECK (PaymentMethodCode = N'Cash'),
        CONSTRAINT CK_CreditCollectionIntents_Status CHECK (Status IN (N'Authorized', N'Posting', N'Completed', N'Released', N'ReconciliationRequired'))
    );
    CREATE INDEX IX_CreditCollectionIntents_Customer ON dbo.CreditCollectionIntents (CustomerID, Status);
END;

IF OBJECT_ID(N'dbo.CreditCollectionReservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CreditCollectionReservations
    (
        ReservationID uniqueidentifier NOT NULL CONSTRAINT PK_CreditCollectionReservations PRIMARY KEY,
        CollectionOperationID uniqueidentifier NOT NULL,
        ReceivableID uniqueidentifier NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        CONSTRAINT FK_CreditCollectionReservations_Intent FOREIGN KEY (CollectionOperationID) REFERENCES dbo.CreditCollectionIntents (CollectionOperationID),
        CONSTRAINT FK_CreditCollectionReservations_Receivable FOREIGN KEY (ReceivableID) REFERENCES dbo.CustomerReceivables (ReceivableID),
        CONSTRAINT UQ_CreditCollectionReservations_IntentReceivable UNIQUE (CollectionOperationID, ReceivableID),
        CONSTRAINT CK_CreditCollectionReservations_Amount CHECK (Amount > 0)
    );
END;
