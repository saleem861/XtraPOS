-- Smart membership identities, sessions, and one-use POS verification requests.
-- Additive only. Does not change customers, prices, stock, or credit balances.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerMembershipIdentities', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerMembershipIdentities
    (
        IdentityID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerMembershipIdentities PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        Provider nvarchar(20) NOT NULL,
        ProviderSubject nvarchar(256) NOT NULL,
        LinkedAt datetime2(0) NOT NULL,
        LastAuthenticatedAt datetime2(0) NULL,
        Status nvarchar(20) NOT NULL,
        RevokedAt datetime2(0) NULL,
        CreatedAt datetime2(0) NOT NULL,
        UpdatedAt datetime2(0) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_CustomerMembershipIdentities_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT CK_CustomerMembershipIdentities_Provider CHECK (Provider IN (N'Google', N'Apple')),
        CONSTRAINT CK_CustomerMembershipIdentities_Status CHECK (Status IN (N'Active', N'Revoked'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UQ_CustomerMembershipIdentities_ActiveSubject' AND object_id = OBJECT_ID(N'dbo.CustomerMembershipIdentities'))
    CREATE UNIQUE INDEX UQ_CustomerMembershipIdentities_ActiveSubject
        ON dbo.CustomerMembershipIdentities (BusinessID, Provider, ProviderSubject)
        WHERE Status = N'Active';

IF OBJECT_ID(N'dbo.CustomerMembershipSessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerMembershipSessions
    (
        SessionID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerMembershipSessions PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        IdentityID uniqueidentifier NOT NULL,
        SessionTokenHash binary(32) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        LastUsedAt datetime2(0) NOT NULL,
        IdleExpiresAt datetime2(0) NOT NULL,
        AbsoluteExpiresAt datetime2(0) NOT NULL,
        RevokedAt datetime2(0) NULL,
        RevocationReason nvarchar(80) NULL,
        DeviceDisplayName nvarchar(80) NULL,
        BrowserFamily nvarchar(40) NULL,
        Platform nvarchar(40) NULL,
        LastSeenAt datetime2(0) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_CustomerMembershipSessions_Identities FOREIGN KEY (IdentityID) REFERENCES dbo.CustomerMembershipIdentities (IdentityID),
        CONSTRAINT UQ_CustomerMembershipSessions_TokenHash UNIQUE (SessionTokenHash)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CustomerMembershipSessions_Customer' AND object_id = OBJECT_ID(N'dbo.CustomerMembershipSessions'))
    CREATE INDEX IX_CustomerMembershipSessions_Customer ON dbo.CustomerMembershipSessions (BusinessID, CustomerID, RevokedAt, IdleExpiresAt);

IF OBJECT_ID(N'dbo.MembershipVerificationRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipVerificationRequests
    (
        VerificationRequestID uniqueidentifier NOT NULL CONSTRAINT PK_MembershipVerificationRequests PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CounterID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        CashierUserID uniqueidentifier NOT NULL,
        SaleContextID uniqueidentifier NOT NULL,
        QRTokenHash binary(32) NOT NULL,
        Status nvarchar(20) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        VerifiedAt datetime2(0) NULL,
        VerifiedByMembershipSessionID uniqueidentifier NULL,
        ConsumedAt datetime2(0) NULL,
        CancelledAt datetime2(0) NULL,
        CorrelationID uniqueidentifier NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT CK_MembershipVerificationRequests_Status CHECK (Status IN (N'Pending', N'Verified', N'Rejected', N'Expired', N'Cancelled')),
        CONSTRAINT UQ_MembershipVerificationRequests_TokenHash UNIQUE (QRTokenHash)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_MembershipVerificationRequests_Counter' AND object_id = OBJECT_ID(N'dbo.MembershipVerificationRequests'))
    CREATE INDEX IX_MembershipVerificationRequests_Counter
        ON dbo.MembershipVerificationRequests (BusinessID, BranchID, CounterID, SaleContextID, Status);

IF OBJECT_ID(N'dbo.MembershipVerificationAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipVerificationAudit
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_MembershipVerificationAudit PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        EventCode nvarchar(40) NOT NULL,
        CustomerID uniqueidentifier NULL,
        VerificationRequestID uniqueidentifier NULL,
        CorrelationID uniqueidentifier NULL,
        CreatedAt datetime2(0) NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.MembershipEnrollmentRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MembershipEnrollmentRequests
    (
        EnrollmentID uniqueidentifier NOT NULL CONSTRAINT PK_MembershipEnrollmentRequests PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        TokenHash binary(32) NOT NULL,
        Status nvarchar(20) NOT NULL,
        ExpiresAt datetime2(0) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        CONSTRAINT UQ_MembershipEnrollmentRequests_TokenHash UNIQUE (TokenHash),
        CONSTRAINT CK_MembershipEnrollmentRequests_Status CHECK (Status IN (N'Pending', N'Completed', N'Expired'))
    );
END;
