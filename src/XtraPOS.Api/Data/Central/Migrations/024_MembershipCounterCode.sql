-- Stable public code for one POS counter. The picture does not change between scans.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.MembershipCounterCodes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipCounterCodes
    (
        CounterID uniqueidentifier NOT NULL CONSTRAINT PK_MembershipCounterCodes PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        PublicToken nvarchar(80) NOT NULL,
        SaleContextID uniqueidentifier NULL,
        CustomerID uniqueidentifier NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_MembershipCounterCodes_Status DEFAULT (N'Waiting'),
        RecognizedCustomerID uniqueidentifier NULL,
        UpdatedAt datetime2(0) NOT NULL,
        CONSTRAINT UQ_MembershipCounterCodes_PublicToken UNIQUE (PublicToken),
        CONSTRAINT CK_MembershipCounterCodes_Status CHECK (Status IN (N'Waiting', N'Verified', N'Unlinked', N'Rejected'))
    );
END;
