-- Registration challenges do not require a customer row.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'CustomerNumber') IS NULL
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ADD CustomerNumber nvarchar(50) NULL;
IF COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'ConsumedAt') IS NULL
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ADD ConsumedAt datetime2(0) NULL;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerPhoneVerificationChallenges_Customer')
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges DROP CONSTRAINT FK_CustomerPhoneVerificationChallenges_Customer;

IF COL_LENGTH(N'dbo.CustomerPhoneVerificationChallenges', N'CustomerID') IS NOT NULL
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges ALTER COLUMN CustomerID uniqueidentifier NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CustomerPhoneVerificationChallenges_Customer')
    ALTER TABLE dbo.CustomerPhoneVerificationChallenges
        ADD CONSTRAINT FK_CustomerPhoneVerificationChallenges_Customer
        FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID);
