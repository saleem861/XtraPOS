-- Local copy of the customer credit card. Receivables and the ledger stay on central.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerCreditPolicies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerCreditPolicies
    (
        CustomerID uniqueidentifier NOT NULL,
        CreditEnabled bit NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_CreditEnabled DEFAULT (0),
        CreditLimit decimal(19, 4) NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_CreditLimit DEFAULT (0),
        PaymentTermDays int NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_PaymentTermDays DEFAULT (30),
        CreditStatus nvarchar(20) NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_CreditStatus DEFAULT (N'Inactive'),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchCustomerCreditPolicies_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy uniqueidentifier NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_BranchCustomerCreditPolicies PRIMARY KEY (CustomerID),
        CONSTRAINT FK_BranchCustomerCreditPolicies_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT CK_BranchCustomerCreditPolicies_Limit CHECK (CreditLimit >= 0),
        CONSTRAINT CK_BranchCustomerCreditPolicies_Terms CHECK (PaymentTermDays >= 0 AND PaymentTermDays <= 3650),
        CONSTRAINT CK_BranchCustomerCreditPolicies_Status CHECK (CreditStatus IN (N'Inactive', N'Active', N'Suspended'))
    );
END;
