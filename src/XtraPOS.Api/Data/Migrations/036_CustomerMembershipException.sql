-- Same registration-exception mark on the branch customer row.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionAt') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionAt datetime2(0) NULL;
IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionByUserID') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionByUserID uniqueidentifier NULL;
IF COL_LENGTH(N'dbo.Customers', N'MembershipExceptionApprovedAt') IS NULL
    ALTER TABLE dbo.Customers ADD MembershipExceptionApprovedAt datetime2(0) NULL;
