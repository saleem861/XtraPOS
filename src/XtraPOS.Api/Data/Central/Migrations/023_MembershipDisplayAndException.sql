-- Membership display values, open POS recognition, and registration exceptions.
-- Additive. Does not change prices, stock, or credit.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerMembershipIdentities', N'ProviderAccountDisplay') IS NULL
    ALTER TABLE dbo.CustomerMembershipIdentities ADD ProviderAccountDisplay nvarchar(256) NULL;
IF COL_LENGTH(N'dbo.CustomerMembershipIdentities', N'ProviderEmail') IS NULL
    ALTER TABLE dbo.CustomerMembershipIdentities ADD ProviderEmail nvarchar(256) NULL;

IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'CashierUserID') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD CashierUserID uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'CounterID') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD CounterID uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'Provider') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD Provider nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'ProviderSubject') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD ProviderSubject nvarchar(256) NULL;
IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'ProviderAccountDisplay') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD ProviderAccountDisplay nvarchar(256) NULL;
IF COL_LENGTH(N'dbo.MembershipEnrollmentRequests', N'ProviderEmail') IS NULL
    ALTER TABLE dbo.MembershipEnrollmentRequests ADD ProviderEmail nvarchar(256) NULL;

IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_MembershipEnrollmentRequests_Customers')
    ALTER TABLE dbo.MembershipEnrollmentRequests DROP CONSTRAINT FK_MembershipEnrollmentRequests_Customers;
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MembershipEnrollmentRequests') AND name = N'CustomerID' AND is_nullable = 0)
    ALTER TABLE dbo.MembershipEnrollmentRequests ALTER COLUMN CustomerID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.MembershipVerificationRequests', N'CustomerID') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.MembershipVerificationRequests') AND name = N'CustomerID' AND is_nullable = 0)
    ALTER TABLE dbo.MembershipVerificationRequests ALTER COLUMN CustomerID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.MembershipVerificationAudit', N'ActorUserID') IS NULL
    ALTER TABLE dbo.MembershipVerificationAudit ADD ActorUserID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionAt') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionAt datetime2(0) NULL;
IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionByUserID') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionByUserID uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionApprovedAt') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionApprovedAt datetime2(0) NULL;
